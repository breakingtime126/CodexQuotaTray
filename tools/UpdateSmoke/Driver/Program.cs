using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuota.Core;
namespace CodexQuota.Tray;
internal static class Program
{
    private static string Hash(string path) { using var input = File.OpenRead(path); using var sha = SHA256.Create(); return string.Concat(sha.ComputeHash(input).Select(b => b.ToString("x2"))); }
    private static void Assert(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
    private static void Wait(Func<bool> test, string message, int seconds = 15) { var time = Stopwatch.StartNew(); while (!test()) { if (time.Elapsed.TotalSeconds > seconds) throw new TimeoutException(message); Thread.Sleep(50); } }
    internal static void Run(string[] args)
    {
        var package = AppDomain.CurrentDomain.BaseDirectory;
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray UpdateSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results = new List<object>();
        using var key = new RSACng(2048); // Unnamed CNG key is ephemeral; never exported privately or written to disk.
        foreach (var name in new[] { "success", "corrupt-download", "locked-replace", "health-failure", "health-marker-lock", "delayed-shutdown" })
        {
            var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
            var stage = Path.Combine(folder, ".cqt-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            Process? old = null, helper = null; FileStream? locked = null;
            try
            {
                var target = Path.Combine(folder, "CodexQuotaTray.exe");
                var candidate = Path.Combine(stage, "CodexQuotaTray.exe");
                File.Copy(Path.Combine(package, "old", "CodexQuotaTray.exe"), target);
                File.Copy(Path.Combine(package, "new", "CodexQuotaTray.exe"), candidate);
                var oldHash = Hash(target); var newHash = Hash(candidate);
                Assert(new Version(FileVersionInfo.GetVersionInfo(target).FileVersion!) == new Version(0, 0, 1, 0), "old fixture version");
                Assert(new Version(FileVersionInfo.GetVersionInfo(candidate).FileVersion!) == new Version(0, 0, 2, 0), "new fixture version");
                var publicKey = key.ExportParameters(false);
                File.WriteAllBytes(Path.Combine(stage, "public-key.json"), JsonSerializer.SerializeToUtf8Bytes(new { modulus = Convert.ToBase64String(publicKey.Modulus!), exponent = Convert.ToBase64String(publicKey.Exponent!) }));
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, app = "CodexQuotaTray", channel = "stable", version = "0.0.2.0", assetUrl = UpdateManifest.AssetUrlFor(new Version(0, 0, 2, 0)), size = new FileInfo(candidate).Length, sha256 = newHash, publishedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") });
                File.WriteAllBytes(Path.Combine(stage, "update.json"), JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) }));
                if (name == "corrupt-download") { using var file = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite); file.Position = file.Length - 1; var b = file.ReadByte(); file.Position--; file.WriteByte((byte)(b ^ 1)); }
                if (name == "delayed-shutdown") File.WriteAllText(Path.Combine(folder, "delay-old"), "yes");
                if (name == "health-failure") File.WriteAllText(Path.Combine(stage, "fail-health"), "yes");
                if (name == "health-marker-lock") File.WriteAllText(Path.Combine(stage, "lock-health"), "yes");
                old = Process.Start(new ProcessStartInfo(target, "--fixture-old") { UseShellExecute = false, WorkingDirectory = folder })!;
                Wait(() => File.Exists(Path.Combine(folder, "old-started")), "old did not start");
                var nonce = Guid.NewGuid().ToString("N");
                var request = Path.Combine(stage, "request.json");
                File.WriteAllBytes(request, JsonSerializer.SerializeToUtf8Bytes(new { TargetName = "CodexQuotaTray.exe", ParentPid = old.Id, ParentStartUtcTicks = old.StartTime.ToUniversalTime().Ticks, OldSha256 = oldHash, CliPath = (string?)null, Nonce = nonce }));
                var helperPath = Path.Combine(stage, "CodexQuotaTray.Update.exe"); File.Copy(target, helperPath);
                helper = Process.Start(new ProcessStartInfo(helperPath, "--apply-update " + StartupCommand.QuoteArgument(request)) { UseShellExecute = false, WorkingDirectory = stage })!;
                if (name == "corrupt-download")
                {
                    Assert(helper.WaitForExit(15000), "corrupt helper failed to exit");
                    Assert(!File.Exists(Path.Combine(stage, "ready")), "corrupt candidate was accepted");
                    Assert(!old.HasExited && Hash(target) == oldHash, "old process or file altered before verification");
                }
                else
                {
                    Wait(() => File.Exists(Path.Combine(stage, "ready")) || helper.HasExited, "helper did not become ready");
                    Assert(File.Exists(Path.Combine(stage, "ready")), "helper failed preflight: " + ReadStatus(stage));
                    if (name == "locked-replace") locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    File.WriteAllText(Path.Combine(folder, "exit-old"), "yes");
                    Assert(helper.WaitForExit(name == "delayed-shutdown" ? 95000 : 45000), "helper did not finish");
                    locked?.Dispose(); locked = null;
                    if (name == "success" || name == "health-marker-lock" || name == "delayed-shutdown")
                    {
                        Assert(Hash(target) == newHash, "new image not installed");
                        Assert(File.ReadAllText(Path.Combine(stage, "healthy")) == nonce, "health not acknowledged");
                        Assert(Hash(Path.Combine(stage, "previous.exe")) == oldHash, "old backup not retained");
                        AssertRunningFixture(Path.Combine(folder, "new-pid"), target);
                    }
                    else
                    {
                        Assert(Hash(target) == oldHash, "rollback did not preserve old image");
                        Wait(() => File.Exists(Path.Combine(folder, "restarted-old")), "old did not restart");
                        AssertRunningFixture(Path.Combine(folder, "restarted-old"), target);
                        Assert(File.Exists(Path.Combine(stage, "fixture-failed-version")) == (name == "health-failure"), "failed-version marker must reflect attempted launch, not a temporary file lock");
                    }
                }
                results.Add(new { test = name, passed = true, status = ReadStatus(stage) });
                Console.WriteLine("PASS " + name);
            }
            catch (Exception e) { results.Add(new { test = name, passed = false, status = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
            finally
            {
                locked?.Dispose(); File.WriteAllText(Path.Combine(folder, "stop"), "yes");
                foreach (var p in new[] { old, helper }) if (p is not null) { try { if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); } } catch { } p.Dispose(); }
                // Restarted/new fixtures voluntarily stop via marker; never enumerate/kill other processes.
                Thread.Sleep(200);
            }
        }
        var report = Path.Combine(root, "results.json");
        File.WriteAllBytes(report, JsonSerializer.SerializeToUtf8Bytes(new { fixtureOnly = true, noProductionRegistryOrAccounts = true, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("REPORT " + report);
        Environment.ExitCode = results.Any(x => JsonSerializer.Serialize(x).Contains("\"passed\":false")) ? 1 : 0;
    }
    private static void AssertRunningFixture(string pidFile, string target)
    {
        Wait(() =>
        {
            try
            {
                if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile), out var pid)) return false;
                using var process = Process.GetProcessById(pid);
                return !process.HasExited && string.Equals(process.MainModule!.FileName, target, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }, "expected fixture did not become running from target");
    }
    private static string ReadStatus(string stage) { var path = Path.Combine(stage, "status.txt"); return File.Exists(path) ? File.ReadAllText(path) : "no status"; }
}
