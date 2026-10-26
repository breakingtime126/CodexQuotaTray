using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

public enum HandoffDisposition { Reject, KeepExisting, Prepare }
public enum HandoffStartupStage { Identity, Coordination, ExistingInstance }

public static class HandoffStartupFailure
{
    public static string Message(HandoffStartupStage stage) => stage switch
    {
        HandoffStartupStage.Identity => "无法完成 Windows 登录身份检查，程序尚未启动。\n这是启动初始化错误，请反馈此提示以便排查。",
        HandoffStartupStage.Coordination => "无法建立本用户的单实例启动检查，程序尚未启动。\n请反馈此提示以便排查权限或启动状态。",
        _ => "未能完成版本交接。\n如果旧版仍在运行，请先从其托盘菜单选择“退出”，再双击新版。\n未强制关闭任何程序。"
    };
}

/// <summary>Small, bounded local protocol. Release versions are numeric, never filenames or timestamps.</summary>
public sealed record HandoffMessage(string Kind, int Protocol, string Product, string Version, string Nonce, string? CliPath = null);
public sealed record HandoffPeerIdentity(string? UserSid, string? LogonSid, int Session, string? ProductName, string? OriginalFilename, string? FileVersion);

public static class InstanceHandoffProtocol
{
    public const int ProtocolVersion = 1;
    public const int MaximumFrameBytes = 8192;
    public const string Product = "CodexQuotaTray.c0da730b-8089-4679-9ce1-e44b70296f4b";

    public static HandoffMessage Message(string kind, Version version, string nonce, string? cliPath = null) =>
        new(kind, ProtocolVersion, Product, version.ToString(4), nonce, cliPath);

    public static bool TryVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null || value.Length > 23 || !Version.TryParse(value, out var parsed) ||
            parsed.Major > 65535 || parsed.Minor > 65535 || parsed.Build is < 0 or > 65535 ||
            parsed.Revision is < 0 or > 65535 || parsed.ToString(4) != value) return false;
        version = parsed;
        return true;
    }

    public static bool IsValid(HandoffMessage message) => message.Protocol == ProtocolVersion && message.Product == Product &&
        TryVersion(message.Version, out _) && message.Nonce is { Length: 32 } &&
        Guid.TryParseExact(message.Nonce, "N", out _) &&
        (message.CliPath is null || (message.CliPath.Length is > 0 and <= 4096 && message.CliPath.IndexOfAny(['\0', '\r', '\n']) < 0));

    public static HandoffDisposition Evaluate(Version current, Version verifiedPeer, HandoffMessage request)
    {
        if (!IsValid(request) || request.Kind != "hello" || request.Version != verifiedPeer.ToString(4) || request.CliPath is not null)
            return HandoffDisposition.Reject;
        return verifiedPeer > current ? HandoffDisposition.Prepare : HandoffDisposition.KeepExisting;
    }

    public static bool IsReply(HandoffMessage message, string kind, Version verifiedPeer, string nonce) =>
        IsValid(message) && message.Kind == kind && message.Version == verifiedPeer.ToString(4) && message.Nonce == nonce;

    public static string? SelectCliPath(string? explicitPath, string? inheritedPath) => explicitPath ?? inheritedPath;

    public static bool VerifyIdentity(HandoffPeerIdentity peer, string userSid, string logonSid, int session, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        return peer.UserSid == userSid && peer.LogonSid == logonSid && peer.Session == session &&
            peer.ProductName == "CodexQuotaTray" && peer.OriginalFilename is "CodexQuotaTray.exe" or "CodexQuotaTray.dll" &&
            TryVersion(peer.FileVersion, out version);
    }

    public static async Task WriteAsync(Stream stream, HandoffMessage message, CancellationToken token)
    {
        if (!IsValid(message)) throw new InvalidDataException("Invalid handoff message.");
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        if (body.Length > MaximumFrameBytes) throw new InvalidDataException("Handoff message too long.");
        var header = new byte[] { (byte)body.Length, (byte)(body.Length >> 8), (byte)(body.Length >> 16), (byte)(body.Length >> 24) };
        await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<HandoffMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
        var length = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Invalid handoff length.");
        var body = new byte[length];
        await ReadExactlyAsync(stream, body, token).ConfigureAwait(false);
        var message = JsonSerializer.Deserialize<HandoffMessage>(body);
        if (message is null || !IsValid(message)) throw new InvalidDataException("Invalid handoff message.");
        return message;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}

/// <summary>A prepared request is not permission to exit until its matching commit arrives.</summary>
public sealed class HandoffCommitGate
{
    private string? _nonce;
    private Version? _peer;
    public bool IsCommitted { get; private set; }
    public bool Prepare(Version current, Version peer, HandoffMessage request)
    {
        if (_nonce is not null || IsCommitted || InstanceHandoffProtocol.Evaluate(current, peer, request) != HandoffDisposition.Prepare) return false;
        _nonce = request.Nonce;
        _peer = peer;
        return true;
    }
    public bool Commit(HandoffMessage message)
    {
        if (IsCommitted || _nonce is null || _peer is null || message.CliPath is not null ||
            !InstanceHandoffProtocol.IsReply(message, "commit", _peer, _nonce)) return false;
        IsCommitted = true;
        return true;
    }
}
