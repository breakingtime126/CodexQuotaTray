using System.Globalization;
using System.Text.Json;

namespace CodexQuota.Core;

public enum NetworkRoute { Unknown, Direct, EnvironmentProxy, WindowsProxy, OtherSystemProxy, WindowsAutoProxy }
public enum NetworkProxyProtocol { None, Http, Https, Socks4, Socks4a, Socks5, Socks5h, Other }
public enum NetworkTlsBackend { Unknown, NativeTls, Rustls }
public enum NetworkDotenvState { NotChecked, Absent, Loaded, Failed }
public enum NetworkErrorLayer { None, Reqwest, HyperConnect, Io, NativeTls, Rustls, HttpStatus, Timeout, Unknown, SystemProxy }
public enum NetworkSystemProxyConfig { NotChecked, Absent, StaticOnly, AutoConfig, AutoDetect, AutoConfigAndDetect, ReadFailed }
public enum NetworkSystemProxyResolution { NotChecked, NotNeeded, Resolving, ResolvedProxy, ResolvedDirect, Failed, TimedOut, Unsupported, Unavailable }
public enum NetworkIoKind
{
    None, ConnectionRefused, ConnectionReset, ConnectionAborted, NotConnected, AddrInUse, AddrNotAvailable,
    NetworkUnreachable, HostUnreachable, PermissionDenied, NotFound, TimedOut, WouldBlock, UnexpectedEof, InvalidData, Other
}

/// <summary>Closed transport evidence only; cannot contain addresses, credentials, paths or raw errors.</summary>
public sealed record NetworkDiagnostic(NetworkRoute Route, NetworkProxyProtocol ProxyProtocol,
    NetworkTlsBackend TlsBackend, bool CaOverride, NetworkDotenvState Dotenv, NetworkErrorLayer ErrorLayer,
    NetworkIoKind IoKind, int? OsCode, bool ChainTruncated, bool NoProxyBypass,
    NetworkSystemProxyConfig SystemProxyConfig, NetworkSystemProxyResolution SystemProxyResolution,
    bool SystemProxyAutoDetect, bool SystemProxyPacPresent, bool SystemProxyStaticPresent)
{
    public string ToSafeLogCode() => "networkRoute=" + Label(Route) + " proxyProtocol=" + Label(ProxyProtocol) +
        " noProxyBypass=" + Bit(NoProxyBypass) + " tls=" + Label(TlsBackend) + " caOverride=" + Bit(CaOverride) +
        " dotenv=" + Label(Dotenv) + " errorLayer=" + Label(ErrorLayer) + " ioKind=" + Label(IoKind) +
        " osCode=" + (OsCode?.ToString(CultureInfo.InvariantCulture) ?? "none") + " chainTruncated=" + Bit(ChainTruncated) +
        " systemProxyConfig=" + Label(SystemProxyConfig) + " systemProxyResolution=" + Label(SystemProxyResolution) +
        " systemProxyAutoDetect=" + Bit(SystemProxyAutoDetect) + " systemProxyPacPresent=" + Bit(SystemProxyPacPresent) +
        " systemProxyStaticPresent=" + Bit(SystemProxyStaticPresent);

    public override string ToString() => ToSafeLogCode();

    internal static NetworkDiagnostic Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (property.Name is not ("route" or "proxyProtocol" or "tlsBackend" or "caOverride" or "dotenv" or
                "errorLayer" or "ioKind" or "osCode" or "chainTruncated" or "noProxyBypass" or
                "systemProxyConfig" or "systemProxyResolution" or "systemProxyAutoDetect" or
                "systemProxyPacPresent" or "systemProxyStaticPresent") || !names.Add(property.Name))
                throw Invalid();
        if (names.Count != 15) throw Invalid();
        var os = value.GetProperty("osCode");
        int? osCode = null;
        if (os.ValueKind != JsonValueKind.Null)
        {
            if (os.ValueKind != JsonValueKind.Number || !os.TryGetInt32(out var code)) throw Invalid();
            osCode = code;
        }
        return new(EnumValue<NetworkRoute>(value, "route"), EnumValue<NetworkProxyProtocol>(value, "proxyProtocol"),
            EnumValue<NetworkTlsBackend>(value, "tlsBackend"), Boolean(value, "caOverride"),
            EnumValue<NetworkDotenvState>(value, "dotenv"), EnumValue<NetworkErrorLayer>(value, "errorLayer"),
            EnumValue<NetworkIoKind>(value, "ioKind"), osCode, Boolean(value, "chainTruncated"), Boolean(value, "noProxyBypass"),
            EnumValue<NetworkSystemProxyConfig>(value, "systemProxyConfig"),
            EnumValue<NetworkSystemProxyResolution>(value, "systemProxyResolution"),
            Boolean(value, "systemProxyAutoDetect"), Boolean(value, "systemProxyPacPresent"),
            Boolean(value, "systemProxyStaticPresent"));
    }

    private static T EnumValue<T>(JsonElement parent, string name) where T : struct
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString();
        // Reject numeric strings, combinations, alternate casing and unrecognized labels.
        if (text is null || !Enum.GetNames(typeof(T)).Contains(text, StringComparer.Ordinal) ||
            !Enum.TryParse<T>(text, false, out var result)) throw Invalid();
        return result;
    }
    private static bool Boolean(JsonElement parent, string name) => parent.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid()
    };
    private static string Label<T>(T value) where T : struct => Enum.GetName(typeof(T), value) ?? "Unknown";
    private static string Bit(bool value) => value ? "true" : "false";
    private static InvalidDataException Invalid() => new("Invalid embedded network diagnostic.");
}
