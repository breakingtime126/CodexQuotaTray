using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexQuota.Core;

public enum CliSource { None, Explicit, Path, Npm, Desktop, VsCode, EmbeddedReader }
public enum CliStage { Discovery, Start, Initialize, AccountRead, RateLimitsRead, HelperExtract, AuthLoad, UsageRead, ResetCardsRead }
public enum CliStartOperation
{
    None, ValidateConfiguration, CreateJob, ConfigureJob, CreateStdinPipe, CreateStdoutPipe, CreateStderrPipe,
    ProtectStdinHandle, ProtectStdoutHandle, ProtectStderrHandle, InitializeAttributes, SetHandleList, SetJobList,
    CreateProcess, OpenStdioStreams, ManagedProcessStart, SetMitigationPolicy, ValidateDesktopRuntime
}
public enum CliErrorKind
{
    None, RoutingUnauthorized401, RoutingDiscoveryFailed, RoutingDiscoveryTimeout,
    QuotaAuthenticationRequired, QuotaChatGptRequired,
    HelperMissing, HelperIntegrityFailure, HelperProtocolError, HelperProcessFailure,
    MissingCredentials, UnsupportedAuth, InvalidCredentials, CredentialReadFailed, CredentialKeyMissing,
    EphemeralCredentials, UnsupportedConfiguration, ExpiredCredentials, Unauthorized, Forbidden,
    RateLimited, RedirectBlocked, NetworkFailure, Timeout, ResponseTooLarge, InvalidResponse,
    AccountChanged, InvalidArguments, InternalError,
    DnsFailure, ConnectFailure, ProxyConfiguration, ProxyConnectFailure, ProxyAuthentication, TlsFailure,
    CertificateConfiguration, NetworkConfiguration, SourceAmbiguous, SourceMismatch, DesktopRuntimeUnverified, DesktopEvidenceLimit
}

/// <summary>Only allowlisted labels and numbers. Never contains paths, account data, or server text.</summary>
public sealed record CliDiagnostic(CliSource Source, Version? Version, CliStage Stage, ReadStatus Status, int? Code = null,
    CliErrorKind ErrorKind = CliErrorKind.None, CliStartOperation StartOperation = CliStartOperation.None,
    int? CandidateIndex = null, int? AttemptIndex = null)
{
    public NetworkDiagnostic? Network { get; init; }
    public DesktopEvidenceDiagnostic? Desktop { get; init; }

    public string? Explanation => ErrorKind switch
    {
        CliErrorKind.SourceAmbiguous => "CLI 搜索中排除了其他来源目录或 Windows 应用别名；请选择对应来源，或指定独立 CLI。",
        CliErrorKind.DesktopEvidenceLimit => Desktop?.Limit switch
        {
            DesktopEvidenceLimit.HashTime => "Desktop 组件验证达到累计读取/哈希时间预算；尚未完成身份校验，未启动。",
            DesktopEvidenceLimit.VerificationTime => "Desktop 组件验证达到本轮总时间截止；同步系统调用返回后停止，未启动。",
            DesktopEvidenceLimit.ImageBytes => "Desktop 组件文件超过单文件验证大小上限；未启动。",
            DesktopEvidenceLimit.TotalHashBytes => "Desktop 组件验证达到累计文件字节上限；未启动。",
            DesktopEvidenceLimit.DirectoryCount => "Desktop 目录扫描条目数量超过验证上限；未启动。",
            _ => "Desktop 组件验证达到资源预算；未完成验证，未启动。"
        },
        CliErrorKind.DesktopRuntimeUnverified => "Desktop 组件身份验证未完成或未通过；具体失败步骤与数字码见诊断，未启动。",
        CliErrorKind.SourceMismatch => "所选来源与提供的组件不一致；未启动其他来源。",
        CliErrorKind.MissingCredentials => "没有可读取的已有登录，请先在官方 Codex 中登录。",
        CliErrorKind.ExpiredCredentials or CliErrorKind.Unauthorized => "已有登录已过期或被服务拒绝。本模块不会续期，请打开官方 Codex 更新登录。",
        CliErrorKind.CredentialKeyMissing => "加密登录缓存的已有系统密钥缺失；本模块不会生成或替换密钥。",
        CliErrorKind.EphemeralCredentials => "登录只存在于宿主进程内存中，独立只读模块无法继承。",
        CliErrorKind.UnsupportedConfiguration => "当前认证配置、企业策略或加密参数超出此原型支持范围；程序没有修改配置。",
        CliErrorKind.CredentialReadFailed => "已有登录存储无法读取；程序没有尝试修复或改写。",
        CliErrorKind.InvalidCredentials => "已有登录格式或账户关联无法确认；程序没有清理或改写缓存。",
        CliErrorKind.AccountChanged => "读取期间登录账户发生变化，本轮数据已丢弃。",
        CliErrorKind.UnsupportedAuth => "当前登录方式不支持订阅额度读取。",
        CliErrorKind.HelperIntegrityFailure => "内嵌模块的文件、散列或目录安全检查失败；未执行该模块。",
        CliErrorKind.HelperMissing => "内嵌模块资源缺失，请重新下载完整程序。",
        CliErrorKind.RedirectBlocked => "额度接口返回重定向，已停止请求，未向新地址发送凭据。",
        CliErrorKind.Forbidden => "官方额度接口拒绝访问；这不单独证明是套餐问题。",
        CliErrorKind.NetworkFailure or CliErrorKind.ConnectFailure when Network is
            { Route: NetworkRoute.Direct, ErrorLayer: NetworkErrorLayer.Io, OsCode: 10013 } =>
            "Windows拒绝了本次直连socket请求；具体拦截策略尚未确认。",
        CliErrorKind.NetworkFailure => "内嵌模块未能完成额度请求；具体网络原因尚未识别。",
        CliErrorKind.DnsFailure => "额度请求在域名解析阶段失败。",
        CliErrorKind.ConnectFailure => "额度请求在连接建立阶段失败。",
        CliErrorKind.ProxyConfiguration => "现有代理配置无法用于本次额度请求；程序未修改代理设置。",
        CliErrorKind.ProxyConnectFailure => "额度请求无法通过现有代理建立连接。",
        CliErrorKind.ProxyAuthentication => "现有代理要求认证或拒绝了代理认证。",
        CliErrorKind.TlsFailure => "额度请求的 TLS 连接或证书校验失败；未降低安全校验。",
        CliErrorKind.CertificateConfiguration => "已有自定义 CA 证书配置无法读取或解析；未改用忽略证书校验的连接。",
        CliErrorKind.NetworkConfiguration => "已有网络配置无法安全读取或解析；未改写配置，也未改用其他连接设置。",
        CliErrorKind.Timeout => "额度请求超时，稍后自动重试。",
        _ => null
    };

    public string ToSafeLogCode() => "cli source=" + Label(Source) +
        " version=" + (Version?.ToString() ?? "unknown") + " stage=" + Label(Stage) +
        " status=" + Label(Status) + " code=" + (Code?.ToString(CultureInfo.InvariantCulture) ?? "none") +
        " errorKind=" + Label(ErrorKind) + " startOperation=" + Label(StartOperation) +
        " candidate=" + (CandidateIndex?.ToString(CultureInfo.InvariantCulture) ?? "none") +
        " attempt=" + (AttemptIndex?.ToString(CultureInfo.InvariantCulture) ?? "none") +
        (Network is null ? "" : " " + Network.ToSafeLogCode()) +
        (Desktop is null ? "" : " " + Desktop.FormatSafe());

    private static string Label<T>(T value) where T : struct => Enum.GetName(typeof(T), value) ?? "Unknown";

    internal static Version? ReadVersion(JsonElement initialize)
    {
        if (initialize.ValueKind != JsonValueKind.Object ||
            !initialize.TryGetProperty("userAgent", out var agent) || agent.ValueKind != JsonValueKind.String) return null;
        var text = agent.GetString();
        if (text is null || text.Length > 1024) return null;
        // Keep only the numeric backend version, never the OS/device/user-agent suffix.
        var match = Regex.Match(text,
            @"^(?:codex_cli_rs|codex_app_server|codex_quota_tray)/([0-9]{1,6}(?:\.[0-9]{1,6}){2,3})(?=$|[\s-])",
            RegexOptions.CultureInvariant);
        return match.Success && System.Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }
}

internal sealed record CliCandidate(CliSource Source, ProcessStartInfo StartInfo)
{
    public SourceBackendLocation Location { get; init; } = CliLocator.Location(Source);
    public SourceBackendIdentityEvidence IdentityEvidence { get; init; } = SourceBackendIdentityEvidence.Unknown;
    public Func<IDisposable>? AcquireLaunchLease { get; init; }
    public SourceBackendComponent Component { get; init; } = Source switch
    {
        CliSource.Desktop => SourceBackendComponent.DesktopBundledCodex,
        CliSource.VsCode => SourceBackendComponent.VsCodeBundledCodex,
        _ => SourceBackendComponent.CodexExecutable
    };
}
