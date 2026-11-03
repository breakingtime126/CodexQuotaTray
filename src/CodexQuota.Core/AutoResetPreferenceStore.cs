using System.Security;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

public enum AutoResetPreferenceStatus { Default, Loaded, Saved, Invalid, ReadFailed, SaveFailed }

/// <summary>Only fixed labels are exposed; no path, account, credit identifier, or exception text.</summary>
public sealed record AutoResetPreferenceResult(bool Enabled, AutoResetPreferenceStatus Status)
{
    public bool IsWarning => Status is AutoResetPreferenceStatus.Invalid or AutoResetPreferenceStatus.ReadFailed or AutoResetPreferenceStatus.SaveFailed;
    public string Explanation => Status switch
    {
        AutoResetPreferenceStatus.Default => "默认开启，在免费重置卡到期前 60 秒自动尝试使用；可在设置中关闭",
        AutoResetPreferenceStatus.Loaded => "已读取自动使用重置卡设置",
        AutoResetPreferenceStatus.Saved => "自动使用重置卡设置已保存",
        AutoResetPreferenceStatus.Invalid => "设置无效，已安全关闭；可重新开启并保存",
        AutoResetPreferenceStatus.ReadFailed => "设置无法读取，已安全关闭",
        AutoResetPreferenceStatus.SaveFailed when Enabled => "开启设置未能保存，自动使用仍关闭",
        AutoResetPreferenceStatus.SaveFailed => "本次已立即关闭，但保存失败，重启后可能重新开启；请检查并重新保存",
        _ => "设置状态未知"
    };
    public string ToSafeLogCode() => "autoResetPreference requested=" + (Enabled ? "On" : "Off") +
        " status=" + (Enum.GetName(typeof(AutoResetPreferenceStatus), Status) ?? "Unknown");
}

/// <summary>Separate preference: missing defaults on; invalid or unreadable data fails closed. Strict bounded JSON and atomic same-directory replacement.</summary>
public sealed class AutoResetPreferenceStore
{
    public const int MaximumBytes = 1024;
    private readonly string _path;
    public AutoResetPreferenceStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray", "auto-reset.json");

    public AutoResetPreferenceResult Load()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumBytes) return new(false, AutoResetPreferenceStatus.Invalid);
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 0, length),
                new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("enabled", out var enabled) ||
                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(false, AutoResetPreferenceStatus.Invalid);
            return new(enabled.GetBoolean(), AutoResetPreferenceStatus.Loaded);
        }
        catch (FileNotFoundException) { return MissingPreference(); }
        catch (DirectoryNotFoundException) { return MissingPreference(); }
        catch (JsonException) { return new(false, AutoResetPreferenceStatus.Invalid); }
        catch (Exception error) when (IsFileError(error)) { return new(false, AutoResetPreferenceStatus.ReadFailed); }
    }

    private AutoResetPreferenceResult MissingPreference()
    {
        // DirectoryNotFound can also mean that an ancestor is a file, not a fresh install.
        // Do not use File.Exists: it hides permission/I/O failures as a missing setting.
        try
        {
            var path = Path.GetFullPath(_path);
            while (path is not null)
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                        (attributes & FileAttributes.Directory) == 0)
                        return new(false, AutoResetPreferenceStatus.ReadFailed);
                    return new(true, AutoResetPreferenceStatus.Default);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                path = Path.GetDirectoryName(path);
            }
        }
        catch (Exception error) when (IsFileError(error)) { }
        return new(false, AutoResetPreferenceStatus.ReadFailed);
    }

    public AutoResetPreferenceResult Save(bool enabled)
    {
        string? temporary = null;
        try
        {
            var path = Path.GetFullPath(_path);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".auto-reset-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = Encoding.UTF8.GetBytes("{\"version\":1,\"enabled\":" + (enabled ? "true" : "false") + "}\n");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            temporary = null;
            return new(enabled, AutoResetPreferenceStatus.Saved);
        }
        catch (Exception error) when (IsFileError(error)) { return new(enabled, AutoResetPreferenceStatus.SaveFailed); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (IsFileError(error)) { }
        }
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;
}
