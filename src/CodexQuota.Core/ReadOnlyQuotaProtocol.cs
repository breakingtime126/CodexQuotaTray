using System.Text.Json;

namespace CodexQuota.Core;

/// <summary>Versioned, bounded output from the bundled one-shot reader. No server text is retained.</summary>
public static class ReadOnlyQuotaProtocol
{
    public const int ProtocolVersion = 1;
    public const string HelperVersion = "0.2.16";
    public const int MaximumResponseBytes = 1024 * 1024;
    internal static readonly Version DiagnosticVersion = new(HelperVersion);

    internal sealed record Response(QuotaRead Read, CliStage Stage, int? Code, CliErrorKind ErrorKind)
    {
        public NetworkDiagnostic? Network { get; init; }
    }

    internal static Response Parse(byte[] utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaximumResponseBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("protocolVersion" or "helperVersion" or "status" or "stage" or "code" or "errorKind" or "result" or "network") ||
                    !fields.Add(property.Name)) throw Invalid();
            if (fields.Count != (fields.Contains("network") ? 8 : 7) || root.GetProperty("protocolVersion").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("protocolVersion").TryGetInt32(out var version) || version != ProtocolVersion ||
                Text(root, "helperVersion") != HelperVersion) throw Invalid();

            var network = root.TryGetProperty("network", out var networkValue) && networkValue.ValueKind != JsonValueKind.Null
                ? NetworkDiagnostic.Parse(networkValue) : null;

            var stage = Text(root, "stage") switch
            {
                "AuthLoad" => CliStage.AuthLoad,
                "UsageRead" => CliStage.UsageRead,
                "ResetCardsRead" => CliStage.ResetCardsRead,
                "RateLimitsRead" => CliStage.RateLimitsRead,
                _ => throw Invalid()
            };
            int? code = null;
            var codeValue = root.GetProperty("code");
            if (codeValue.ValueKind != JsonValueKind.Null)
            {
                if (codeValue.ValueKind != JsonValueKind.Number || !codeValue.TryGetInt32(out var number) || number is < 0 or > ushort.MaxValue)
                    throw Invalid();
                code = number;
            }
            var errorValue = root.GetProperty("errorKind");
            var error = errorValue.ValueKind == JsonValueKind.Null ? CliErrorKind.None : Text(root, "errorKind") switch
            {
                "MissingCredentials" => CliErrorKind.MissingCredentials,
                "UnsupportedAuth" => CliErrorKind.UnsupportedAuth,
                "InvalidCredentials" => CliErrorKind.InvalidCredentials,
                "CredentialReadFailed" => CliErrorKind.CredentialReadFailed,
                "CredentialKeyMissing" => CliErrorKind.CredentialKeyMissing,
                "EphemeralCredentials" => CliErrorKind.EphemeralCredentials,
                "UnsupportedConfiguration" => CliErrorKind.UnsupportedConfiguration,
                "ExpiredCredentials" => CliErrorKind.ExpiredCredentials,
                "Unauthorized" => CliErrorKind.Unauthorized,
                "Forbidden" => CliErrorKind.Forbidden,
                "RateLimited" => CliErrorKind.RateLimited,
                "RedirectBlocked" => CliErrorKind.RedirectBlocked,
                "NetworkFailure" => CliErrorKind.NetworkFailure,
                "DnsFailure" => CliErrorKind.DnsFailure,
                "ConnectFailure" => CliErrorKind.ConnectFailure,
                "ProxyConfiguration" => CliErrorKind.ProxyConfiguration,
                "ProxyConnectFailure" => CliErrorKind.ProxyConnectFailure,
                "ProxyAuthentication" => CliErrorKind.ProxyAuthentication,
                "TlsFailure" => CliErrorKind.TlsFailure,
                "CertificateConfiguration" => CliErrorKind.CertificateConfiguration,
                "NetworkConfiguration" => CliErrorKind.NetworkConfiguration,
                "Timeout" => CliErrorKind.Timeout,
                "ResponseTooLarge" => CliErrorKind.ResponseTooLarge,
                "InvalidResponse" => CliErrorKind.InvalidResponse,
                "AccountChanged" => CliErrorKind.AccountChanged,
                "InvalidArguments" => CliErrorKind.InvalidArguments,
                "InternalError" => CliErrorKind.InternalError,
                _ => throw Invalid()
            };
            var status = Text(root, "status");
            var expectedStatus = error switch
            {
                CliErrorKind.None => "Ready",
                CliErrorKind.MissingCredentials or CliErrorKind.InvalidCredentials or CliErrorKind.ExpiredCredentials or CliErrorKind.Unauthorized => "NeedsLogin",
                CliErrorKind.UnsupportedAuth or CliErrorKind.EphemeralCredentials or CliErrorKind.UnsupportedConfiguration => "UnsupportedAuth",
                CliErrorKind.InvalidResponse or CliErrorKind.ResponseTooLarge => "InvalidResponse",
                _ => "Unavailable"
            };
            if (status != "Ready" && status != expectedStatus) throw Invalid();
            var result = root.GetProperty("result");
            if (status == "Ready")
            {
                if (result.ValueKind != JsonValueKind.Object ||
                    (error == CliErrorKind.None ? stage != CliStage.RateLimitsRead || code is not null : stage != CliStage.ResetCardsRead))
                    throw Invalid();
                var selection = QuotaParser.ReadSelection(result);
                if (!selection.IsValid)
                    return new(new(ReadStatus.ProtocolError, Selection: selection), CliStage.RateLimitsRead, null,
                        CliErrorKind.InvalidResponse) { Network = network };
                var read = new QuotaRead(selection.Active is null ? ReadStatus.NoQuotaWindow : ReadStatus.Ready,
                    selection.Active, selection, ResetCards.Parse(result));
                return new(read, stage, code, error) { Network = network };
            }
            if (result.ValueKind != JsonValueKind.Null || error == CliErrorKind.None) throw Invalid();
            var readStatus = status switch
            {
                "NeedsLogin" => error is CliErrorKind.ExpiredCredentials or CliErrorKind.Unauthorized
                    ? ReadStatus.CredentialsExpired : ReadStatus.NotSignedIn,
                "UnsupportedAuth" => ReadStatus.UnsupportedAccount,
                "Unavailable" => error switch
                {
                    CliErrorKind.AccountChanged => ReadStatus.NotSignedIn,
                    CliErrorKind.RateLimited => ReadStatus.RateLimited,
                    CliErrorKind.Timeout => ReadStatus.Timeout,
                    _ => ReadStatus.Unavailable
                },
                "InvalidResponse" => ReadStatus.ProtocolError,
                _ => throw Invalid()
            };
            readStatus = error switch
            {
                CliErrorKind.AccountChanged or CliErrorKind.MissingCredentials or CliErrorKind.InvalidCredentials => ReadStatus.NotSignedIn,
                CliErrorKind.CredentialKeyMissing or CliErrorKind.EphemeralCredentials or CliErrorKind.CredentialReadFailed => ReadStatus.CredentialsUnavailable,
                CliErrorKind.UnsupportedConfiguration => ReadStatus.AuthConfigurationUnsupported,
                _ => readStatus
            };
            return new(new(readStatus), stage, code, error) { Network = network };
        }
        catch (JsonException) { throw Invalid(); }
    }

    internal static async Task<Response> ReadAsync(Stream stdout, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await ReadChunkAsync(stdout, buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumResponseBytes) throw Invalid();
            output.Write(buffer, 0, count);
        }
        return Parse(output.ToArray());
    }

    internal static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var read = stream.ReadAsync(buffer, 0, buffer.Length, token);
        await AsyncCompatibility.WaitAsync(read, token).ConfigureAwait(false);
        return await read.ConfigureAwait(false);
    }

    private static string Text(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? throw Invalid() : throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Invalid embedded reader response.");
}
