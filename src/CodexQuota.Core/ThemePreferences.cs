using System.Security;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

public enum ThemePreference { System, Light, Dark }

public static class ThemePreferenceNames
{
    public static bool IsValid(ThemePreference preference) => preference is
        ThemePreference.System or ThemePreference.Light or ThemePreference.Dark;

    public static string DisplayName(this ThemePreference preference) => preference switch
    {
        ThemePreference.System => "跟随系统",
        ThemePreference.Light => "浅色",
        ThemePreference.Dark => "深色",
        _ => "未知"
    };
}

/// <summary>Session-only policy. System changes are remembered but affect only the System selection.</summary>
public sealed class ThemeSelection
{
    public ThemeSelection(ThemePreference preference = ThemePreference.System, bool systemIsDark = false)
    {
        if (!ThemePreferenceNames.IsValid(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        Preference = preference;
        SystemIsDark = systemIsDark;
    }

    public ThemePreference Preference { get; private set; }
    public bool SystemIsDark { get; private set; }
    public bool IsDark => Preference == ThemePreference.Dark ||
        (Preference == ThemePreference.System && SystemIsDark);

    /// <summary>Applies immediately, independently of saving. Returns whether the effective theme changed.</summary>
    public bool Select(ThemePreference preference)
    {
        if (!ThemePreferenceNames.IsValid(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        var wasDark = IsDark;
        Preference = preference;
        return wasDark != IsDark;
    }

    /// <summary>Returns false for fixed Light/Dark selections, including after an OS theme notification.</summary>
    public bool SetSystemTheme(bool isDark)
    {
        var wasDark = IsDark;
        SystemIsDark = isDark;
        return wasDark != IsDark;
    }
}

public enum ThemePreferenceStatus { Default, Loaded, Saved, Invalid, ReadFailed, SaveFailed }

/// <summary>Fixed labels only: never includes the settings path, JSON, or exception text.</summary>
public sealed record ThemePreferenceResult(ThemePreference Preference, ThemePreferenceStatus Status)
{
    public string Explanation => Status switch
    {
        ThemePreferenceStatus.Default => "尚无主题设置，默认跟随系统",
        ThemePreferenceStatus.Loaded => "已读取保存的主题设置",
        ThemePreferenceStatus.Saved => "主题设置已保存",
        ThemePreferenceStatus.Invalid => "主题设置无效，本次跟随系统；可重新选择以保存",
        ThemePreferenceStatus.ReadFailed => "无法读取主题设置，本次跟随系统",
        ThemePreferenceStatus.SaveFailed => "无法保存主题设置，本次选择仍有效，重启后可能恢复原设置",
        _ => "设置状态未知"
    };

    public bool IsWarning => Status is ThemePreferenceStatus.Invalid or ThemePreferenceStatus.ReadFailed or
        ThemePreferenceStatus.SaveFailed;

    public bool IsPersisted => ThemePreferenceNames.IsValid(Preference) &&
        Status is ThemePreferenceStatus.Loaded or ThemePreferenceStatus.Saved;

    public string PersistedPreferenceDescription => IsPersisted ? Preference.DisplayName() :
        Status == ThemePreferenceStatus.Default ? "无（使用默认值）" : "未知（未能确认已保存的主题）";

    public string ToSafeLogCode() => "themePreference selectedTheme=" +
        (Enum.GetName(typeof(ThemePreference), Preference) ?? "Unknown") + " status=" +
        (Enum.GetName(typeof(ThemePreferenceStatus), Status) ?? "Unknown") + " persistedTheme=" +
        (IsPersisted ? Enum.GetName(typeof(ThemePreference), Preference) ?? "Unknown" :
            Status == ThemePreferenceStatus.Default ? "None" : "Unknown");
}

/// <summary>Bounded, atomic app-only settings. Kept separate from source preferences and Windows settings.</summary>
public sealed class ThemePreferencesStore
{
    public const int MaximumBytes = 4096;
    private readonly string _path;

    public ThemePreferencesStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray", "theme.json");

    public ThemePreferenceResult Load()
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
            if (length > MaximumBytes) return Default(ThemePreferenceStatus.Invalid);
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 0, length),
                new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("theme", out var theme) || theme.ValueKind != JsonValueKind.String)
                return Default(ThemePreferenceStatus.Invalid);
            var preference = theme.GetString() switch
            {
                "system" => ThemePreference.System,
                "light" => ThemePreference.Light,
                "dark" => ThemePreference.Dark,
                _ => (ThemePreference?)null
            };
            return preference is null ? Default(ThemePreferenceStatus.Invalid) :
                new(preference.Value, ThemePreferenceStatus.Loaded);
        }
        catch (FileNotFoundException) { return Default(ThemePreferenceStatus.Default); }
        catch (DirectoryNotFoundException) { return Default(ThemePreferenceStatus.Default); }
        catch (JsonException) { return Default(ThemePreferenceStatus.Invalid); }
        catch (Exception error) when (IsFileError(error)) { return Default(ThemePreferenceStatus.ReadFailed); }
    }

    public ThemePreferenceResult Save(ThemePreference preference)
    {
        if (!ThemePreferenceNames.IsValid(preference)) return new(preference, ThemePreferenceStatus.SaveFailed);
        string? temporary = null;
        try
        {
            var path = Path.GetFullPath(_path);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".theme-" + Guid.NewGuid().ToString("N") + ".tmp");
            var label = preference switch
            {
                ThemePreference.Light => "light", ThemePreference.Dark => "dark", _ => "system"
            };
            var bytes = Encoding.UTF8.GetBytes("{\"version\":1,\"theme\":\"" + label + "\"}\n");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            temporary = null;
            return new(preference, ThemePreferenceStatus.Saved);
        }
        catch (Exception error) when (IsFileError(error)) { return new(preference, ThemePreferenceStatus.SaveFailed); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (IsFileError(error)) { }
        }
    }

    private static ThemePreferenceResult Default(ThemePreferenceStatus status) => new(ThemePreference.System, status);
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;
}
