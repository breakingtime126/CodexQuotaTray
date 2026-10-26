using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodexQuota.Core;

namespace CodexQuota.Tray;

internal sealed record UpdateRequest(string TargetName, int ParentPid, long ParentStartUtcTicks, string OldSha256, string? CliPath, string Nonce);

/// <summary>Runs from a verified copy of this executable, before WinForms/singleton/startup registration.</summary>
internal static class UpdateTransaction
{
    internal const string Prefix = ".cqt-update-";
    internal static void RequireUnelevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new System.Security.SecurityException("请以普通用户运行程序后更新；不会提权更新。");
    }

    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        if (args.Length != 2) return true;
        try { Apply(Path.GetFullPath(args[1])); }
        catch (Exception error) { TryStatus(args[1], "更新未完成；stage=Preflight error=" + error.GetType().Name + " hresult=0x" + error.HResult.ToString("X8")); }
        return true;
    }

    internal static string Hash(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(input).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路径不能包含链接或重解析点。");
    }

    internal static async Task<Process> PrepareAsync(string directory, string target, string? cli, CancellationToken stop)
    {
        RequireUnelevated();
        NoLinks(directory); NoLinks(target);
        using var current = Process.GetCurrentProcess();
        var request = new UpdateRequest(Path.GetFileName(target), current.Id, current.StartTime.ToUniversalTime().Ticks,
            Hash(target), cli, Guid.NewGuid().ToString("N"));
        var requestPath = Path.Combine(directory, "request.json");
        WriteNew(requestPath, JsonSerializer.SerializeToUtf8Bytes(request));
        var helper = Path.Combine(directory, "CodexQuotaTray.Update.exe");
        File.Copy(target, helper, overwrite: false);
        if (Hash(helper) != request.OldSha256) throw new InvalidDataException("更新助手校验失败。");
        var start = new ProcessStartInfo(helper, "--apply-update " + Quote(requestPath))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        var process = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
        try
        {
            var ready = Path.Combine(directory, "ready");
            for (var i = 0; i < 100; i++)
            {
                stop.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("更新助手提前退出。");
                if (MarkerMatches(ready, request.Nonce)) return process;
                await Task.Delay(100, stop);
            }
            throw new TimeoutException("更新助手准备超时。");
        }
        catch { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); throw; }
    }

    private static void Apply(string requestPath)
    {
        RequireUnelevated();
        NoLinks(requestPath);
        var directory = Path.GetDirectoryName(requestPath)!;
        if (Path.GetFileName(requestPath) != "request.json" ||
            !Path.GetFileName(directory).StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(directory).Substring(Prefix.Length), "N", out _))
            throw new InvalidDataException("Invalid update directory.");
        var own = Process.GetCurrentProcess().MainModule!.FileName;
        if (!string.Equals(Path.GetFullPath(own), Path.Combine(directory, "CodexQuotaTray.Update.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid helper location.");
        var bytes = ReadBounded(requestPath, 8192);
        var request = JsonSerializer.Deserialize<UpdateRequest>(bytes) ?? throw new InvalidDataException();
        if (Path.GetFileName(request.TargetName) != request.TargetName || !request.TargetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(request.Nonce, "N", out _) || request.OldSha256.Length != 64)
            throw new InvalidDataException();
        var target = Path.Combine(Path.GetDirectoryName(directory)!, request.TargetName);
        NoLinks(target);
        if (Hash(own) != request.OldSha256 || Hash(target) != request.OldSha256) throw new InvalidDataException("Old image changed.");
        var oldVersion = new Version(FileVersionInfo.GetVersionInfo(target).FileVersion ?? "0.0.0.0");
        var manifest = UpdateManifest.ParseAndVerify(ReadBounded(Path.Combine(directory, "update.json"), 32768), UpdateTrust.PublicKey, DateTimeOffset.UtcNow);
        if (!manifest.IsNewerThan(oldVersion)) throw new InvalidDataException("Downgrade rejected.");
        var candidate = Path.Combine(directory, "CodexQuotaTray.exe");
        NoLinks(candidate); manifest.VerifyFile(candidate);
        var candidateInfo = FileVersionInfo.GetVersionInfo(candidate);
        if (candidateInfo.OriginalFilename != "CodexQuotaTray.exe" || !Version.TryParse(candidateInfo.FileVersion, out var candidateVersion) || candidateVersion != manifest.Version)
            throw new InvalidDataException("Candidate metadata does not match signed version.");
        using var old = Process.GetProcessById(request.ParentPid);
        if (old.StartTime.ToUniversalTime().Ticks != request.ParentStartUtcTicks ||
            !string.Equals(old.MainModule!.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Parent identity changed.");
        // Preflight all writes before asking the live application to stop.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, ".cqt-write-" + request.Nonce);
        WriteNew(probe, Array.Empty<byte>()); File.Delete(probe);
        WriteNew(Path.Combine(directory, "ready"), System.Text.Encoding.UTF8.GetBytes(request.Nonce));
        // After readiness is acknowledged the app commits to shutdown. Keep the retained
        // parent handle until it really exits; a fixed deadline here could abandon a slow
        // shutdown and leave no process to restart the application. Never kill the parent.
        old.WaitForExit();
        var backup = Path.Combine(directory, "previous.exe");
        Process? next = null;
        var healthy = false;
        var attemptedLaunch = false;
        var phase = "Revalidate";
        try
        {
            if (Hash(target) != request.OldSha256) throw new InvalidDataException("Target changed during shutdown.");
            // ReplaceFileW requires an exclusive replacement-file open. Do not retain
            // any candidate handle across File.Replace, even one sharing Delete.
            manifest.VerifyFile(candidate);
            phase = "Replace";
            File.Replace(candidate, target, backup);
            phase = "VerifyInstalled";
            // Pin the installed path before verification and retain through startup/health.
            // Invalid or concurrently altered bytes are never executed.
            using var launchLease = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest.VerifyFile(target);
            var args = "--update-health " + Quote(directory) + " " + request.Nonce;
            if (request.CliPath is not null) args += " --codex-path " + Quote(request.CliPath);
            phase = "Launch";
            attemptedLaunch = true;
            next = Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! });
            if (next is null) throw new IOException("New application did not start.");
            phase = "Health";
            var health = Path.Combine(directory, "healthy");
            for (var i = 0; i < 300; i++)
            {
                if (next.HasExited) break;
                if (MarkerMatches(health, request.Nonce)) { healthy = true; break; }
                Thread.Sleep(100);
            }
            if (!healthy) throw new IOException("New application did not confirm startup.");
            TryStatus(requestPath, "更新成功 " + manifest.Version);
        }
        catch (Exception error)
        {
            var diagnostic = " stage=" + phase + " error=" + error.GetType().Name + " hresult=0x" + error.HResult.ToString("X8");
            if (next is not null && !next.HasExited) { next.Kill(); if (!next.WaitForExit(10000)) throw new IOException("New process exit unconfirmed; backup retained."); }
            // Recover even when replacement itself failed after the old application stopped.
            var targetHash = File.Exists(target) ? Hash(target) : null;
            if (targetHash != request.OldSha256)
            {
                if (targetHash is not null && targetHash != manifest.Sha256)
                    throw new IOException("Target changed; rollback retained for manual recovery.");
                if (!File.Exists(backup) || Hash(backup) != request.OldSha256)
                    throw new IOException("Verified rollback image unavailable.");
                var failed = Path.Combine(directory, "failed.exe");
                if (targetHash is null) File.Move(backup, target);
                else File.Replace(backup, target, failed);
            }
            if (attemptedLaunch) UpdateFailureStore.Record(manifest.Version.ToString());
            var args = request.CliPath is null ? "" : "--codex-path " + Quote(request.CliPath);
            Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! });
            TryStatus(requestPath, "更新未完成，已恢复旧版。" + diagnostic);
        }
        finally { next?.Dispose(); }
    }

    internal static bool MarkerMatches(string path, string nonce)
    {
        try { return System.Text.Encoding.UTF8.GetString(ReadBounded(path, 64)) == nonce; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static byte[] ReadBounded(string path, int limit)
    {
        NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException("File exceeds limit.");
        var bytes = new byte[checked((int)file.Length)]; var offset = 0;
        while (offset < bytes.Length) { var count = file.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
        return bytes;
    }
    internal static void WriteNew(string path, byte[] bytes)
    {
        NoLinks(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes, 0, bytes.Length); file.Flush(true);
    }
    internal static string Quote(string text) => StartupCommand.QuoteArgument(text);
    private static void TryStatus(string requestPath, string message)
    { try { var directory = Path.GetDirectoryName(Path.GetFullPath(requestPath))!; if (!Path.GetFileName(directory).StartsWith(Prefix, StringComparison.Ordinal) || Path.GetFileName(requestPath) != "request.json") return; NoLinks(directory); File.WriteAllText(Path.Combine(directory, "status.txt"), message); } catch { } }
}
