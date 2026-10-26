using System.Security;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

public enum SourcePreferenceStatus { Default, Loaded, Saved, Invalid, ReadFailed, SaveFailed, Migrated, MigrationSaveFailed }

/// <summary>Fixed labels only: never includes the settings path, JSON, or exception text.</summary>
public sealed record SourcePreferenceResult(QuotaDataSource Source, SourcePreferenceStatus Status)
{
    public string Explanation => Status switch
    {
        SourcePreferenceStatus.Default => "尚无设置，默认自动查找可用数据来源",
        SourcePreferenceStatus.Loaded => "已读取保存的数据来源",
        SourcePreferenceStatus.Saved => "数据来源已保存",
        SourcePreferenceStatus.Invalid => "数据来源设置无效，本次自动查找可用来源；可重新选择以保存",
        SourcePreferenceStatus.ReadFailed => "无法读取数据来源设置，本次自动查找可用来源",
        SourcePreferenceStatus.SaveFailed => "无法保存数据来源，本次选择仍有效，重启后可能恢复原设置",
        SourcePreferenceStatus.Migrated => "旧版数据来源设置已升级并保存为自动；仍可手动选择单一来源",
        SourcePreferenceStatus.MigrationSaveFailed => "旧版设置本次改用自动，但升级保存失败；可重新选择以保存",
        _ => "设置状态未知"
    };

    public bool IsWarning => Status is SourcePreferenceStatus.Invalid or SourcePreferenceStatus.ReadFailed or
        SourcePreferenceStatus.SaveFailed or SourcePreferenceStatus.MigrationSaveFailed;

    public string PersistedSourceDescription => Status switch
    {
        SourcePreferenceStatus.Loaded or SourcePreferenceStatus.Saved or SourcePreferenceStatus.Migrated => Source.DisplayName(),
        SourcePreferenceStatus.Default => "无（使用默认值）",
        _ => "未知（未能确认已保存的来源）"
    };

    public string ToSafeLogCode() => "sourcePreference selectedSource=" +
        (Enum.GetName(typeof(QuotaDataSource), Source) ?? "Unknown") + " status=" +
        (Enum.GetName(typeof(SourcePreferenceStatus), Status) ?? "Unknown") + " persistedSource=" +
        (Status is SourcePreferenceStatus.Loaded or SourcePreferenceStatus.Saved or SourcePreferenceStatus.Migrated
            ? Enum.GetName(typeof(QuotaDataSource), Source) ?? "Unknown"
            : Status == SourcePreferenceStatus.Default ? "None" : "Unknown");
}

/// <summary>Bounded, atomic, non-authentication settings. Only version and an allowlisted source are stored.</summary>
public sealed class SourcePreferencesStore
{
    public const int MaximumBytes = 4096;
    private readonly string _path;

    public SourcePreferencesStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray", "settings.json");

    public SourcePreferenceResult Load()
    {
        try
        {
            // Close the read handle before migration replaces the file, including on Windows.
            QuotaDataSource selected;
            int number;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bytes = new byte[MaximumBytes + 1];
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = stream.Read(bytes, length, bytes.Length - length);
                    if (read == 0) break;
                    length += read;
                }
                if (length > MaximumBytes) return Default(SourcePreferenceStatus.Invalid);
                using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 0, length),
                    new JsonDocumentOptions { MaxDepth = 4 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                    !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out number) || number is not (1 or 2) ||
                    !root.TryGetProperty("dataSource", out var source) || source.ValueKind != JsonValueKind.String)
                    return Default(SourcePreferenceStatus.Invalid);
                var value = source.GetString() switch
                {
                    "auto" when number == 2 => QuotaDataSource.Auto,
                    "cli" => QuotaDataSource.Cli,
                    "desktop" => QuotaDataSource.Desktop,
                    "vscode" => QuotaDataSource.VsCode,
                    _ => (QuotaDataSource?)null
                };
                if (value is null) return Default(SourcePreferenceStatus.Invalid);
                selected = value.Value;
            }
            if (number == 2) return new(selected, SourcePreferenceStatus.Loaded);
            // Version 1 had no Auto policy. Upgrade once to the new requested default, while
            // exposing the change instead of disguising an old manual selection as a winner.
            var migration = Save(QuotaDataSource.Auto);
            return new(QuotaDataSource.Auto, migration.Status == SourcePreferenceStatus.Saved
                ? SourcePreferenceStatus.Migrated : SourcePreferenceStatus.MigrationSaveFailed);
        }
        catch (FileNotFoundException) { return Default(SourcePreferenceStatus.Default); }
        catch (DirectoryNotFoundException) { return Default(SourcePreferenceStatus.Default); }
        catch (JsonException) { return Default(SourcePreferenceStatus.Invalid); }
        catch (Exception error) when (IsFileError(error)) { return Default(SourcePreferenceStatus.ReadFailed); }
    }

    public SourcePreferenceResult Save(QuotaDataSource source)
    {
        if (!QuotaDataSourceNames.IsValid(source)) return new(source, SourcePreferenceStatus.SaveFailed);
        string? temporary = null;
        try
        {
            var path = Path.GetFullPath(_path);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            var label = source switch
            {
                QuotaDataSource.Auto => "auto", QuotaDataSource.Desktop => "desktop",
                QuotaDataSource.VsCode => "vscode", _ => "cli"
            };
            var bytes = Encoding.UTF8.GetBytes("{\"version\":2,\"dataSource\":\"" + label + "\"}\n");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            temporary = null;
            return new(source, SourcePreferenceStatus.Saved);
        }
        catch (Exception error) when (IsFileError(error)) { return new(source, SourcePreferenceStatus.SaveFailed); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (IsFileError(error)) { }
        }
    }

    private static SourcePreferenceResult Default(SourcePreferenceStatus status) => new(QuotaDataSource.Auto, status);
    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or NotSupportedException;
}
