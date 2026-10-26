namespace CodexQuota.Core;

/// <summary>The user-selected policy. Only Auto permits fallback; concrete sources stay strict.</summary>
public enum QuotaDataSource { Cli, Desktop, VsCode, Auto }

public static class QuotaDataSourceNames
{
    public static string DisplayName(this QuotaDataSource source) => source switch
    {
        QuotaDataSource.Auto => "自动",
        QuotaDataSource.Cli => "Codex CLI",
        QuotaDataSource.Desktop => "Codex桌面端",
        QuotaDataSource.VsCode => "VSCode插件",
        _ => "未知来源"
    };

    public static bool IsValid(QuotaDataSource source) => source is
        QuotaDataSource.Auto or QuotaDataSource.Cli or QuotaDataSource.Desktop or QuotaDataSource.VsCode;

    /// <summary>A legacy --codex-path never overrides a saved desktop or extension selection.</summary>
    public static string? CliOverridePath(this QuotaDataSource source, string? path) =>
        source == QuotaDataSource.Cli ? path : null;
}
