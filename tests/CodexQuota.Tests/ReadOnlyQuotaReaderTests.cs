using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuota.Core;

internal static class ReadOnlyQuotaReaderTests
{
    internal const string ValidResult = """{"rateLimits":{"limitId":"codex","planType":"plus","primary":{"usedPercent":27,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":40,"windowDurationMins":10080}},"rateLimitResetCredits":{"availableCount":0,"credits":[]}}""";
    internal static string Envelope(string status = "Ready", string stage = "RateLimitsRead", string? error = null,
        int? code = null, string? result = ValidResult) => "{\"protocolVersion\":1,\"helperVersion\":\"0.2.16\",\"status\":" +
        JsonSerializer.Serialize(status) + ",\"stage\":" + JsonSerializer.Serialize(stage) + ",\"code\":" +
        (code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + ",\"errorKind\":" +
        JsonSerializer.Serialize(error) + ",\"result\":" + (result ?? "null") + "}";

    internal const string NetworkJson = """{"route":"EnvironmentProxy","proxyProtocol":"Https","tlsBackend":"NativeTls","caOverride":false,"dotenv":"Loaded","errorLayer":"Io","ioKind":"ConnectionRefused","osCode":10061,"chainTruncated":false,"noProxyBypass":false,"systemProxyConfig":"NotChecked","systemProxyResolution":"NotNeeded","systemProxyAutoDetect":false,"systemProxyPacPresent":false,"systemProxyStaticPresent":false}""";
    internal static string WithNetwork(string envelope, string network = NetworkJson) => envelope[..^1] + ",\"network\":" + network + "}";

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("network diagnostic: preserves closed route and numeric OS evidence", () =>
        {
            var response = Parse(WithNetwork(Envelope("Unavailable", "UsageRead", "ConnectFailure", result: null)));
            var network = response.Network!;
            Check.Equal(NetworkRoute.EnvironmentProxy, network.Route);
            Check.Equal(NetworkProxyProtocol.Https, network.ProxyProtocol);
            Check.Equal(NetworkIoKind.ConnectionRefused, network.IoKind);
            Check.Equal(10061, network.OsCode);
            Check.Equal(NetworkSystemProxyConfig.NotChecked, network.SystemProxyConfig);
            Check.Equal(NetworkSystemProxyResolution.NotNeeded, network.SystemProxyResolution);
            Check.False(network.SystemProxyAutoDetect);
            Check.False(network.SystemProxyPacPresent);
            Check.False(network.SystemProxyStaticPresent);
            Check.Contains("networkRoute=EnvironmentProxy", network.ToSafeLogCode());
            Check.Contains("osCode=10061", network.ToSafeLogCode());
            Check.Contains("dotenv=Loaded", network.ToSafeLogCode());
            Check.Contains("systemProxyConfig=NotChecked", network.ToSafeLogCode());
            Check.Contains("systemProxyResolution=NotNeeded", network.ToSafeLogCode());
            Check.Contains("systemProxyAutoDetect=false", network.ToSafeLogCode());
            Check.Contains("systemProxyPacPresent=false", network.ToSafeLogCode());
            Check.Contains("systemProxyStaticPresent=false", network.ToSafeLogCode());
            Check.Equal(network.ToSafeLogCode(), network.ToString());
            Check.Null(Parse(WithNetwork(Envelope(), "null")).Network);
            Check.Null(Parse(Envelope()).Network);
        });
        test("network diagnostic: labels cannot leak unknown enum payloads", () =>
        {
            var network = new NetworkDiagnostic((NetworkRoute)999, (NetworkProxyProtocol)999,
                (NetworkTlsBackend)999, false, (NetworkDotenvState)999, (NetworkErrorLayer)999,
                (NetworkIoKind)999, int.MinValue, true, false,
                (NetworkSystemProxyConfig)999, (NetworkSystemProxyResolution)999, true, false, true);
            var log = new CliDiagnostic(CliSource.EmbeddedReader, null, CliStage.UsageRead,
                ReadStatus.Unavailable) { Network = network };
            Check.Contains("networkRoute=Unknown", log.ToSafeLogCode());
            Check.Contains("osCode=-2147483648", log.ToSafeLogCode());
            Check.Contains("systemProxyConfig=Unknown", log.ToSafeLogCode());
            Check.Contains("systemProxyResolution=Unknown", log.ToSafeLogCode());
            Check.False(log.ToSafeLogCode().Contains("999", StringComparison.Ordinal));
            Check.Equal(network.ToSafeLogCode(), network.ToString());
            Check.Contains("systemProxyAutoDetect=true", network.ToString());
            Check.Contains("systemProxyPacPresent=false", network.ToString());
            Check.Contains("systemProxyStaticPresent=true", network.ToString());
            Check.False(network.ToString().Contains("999", StringComparison.Ordinal));
        });
        var malformedDiagnosticIndex = 0;
        foreach (var bad in new[]
        {
            "[]", "1", "{}", NetworkJson.Replace("EnvironmentProxy", "private.proxy:password@host"),
            NetworkJson.Replace("NativeTls", "1"), NetworkJson.Replace("Loaded", "loaded"),
            NetworkJson.Replace("10061", "2147483648"), NetworkJson.Replace("10061", "1.5"),
            NetworkJson.Replace("false", "\"false\""), NetworkJson[..^1] + ",\"route\":\"Direct\"}",
            NetworkJson[..^1] + ",\"address\":\"private-token\"}",
            NetworkJson.Replace(",\"osCode\":10061", ""),
            NetworkJson.Replace(",\"systemProxyConfig\":\"NotChecked\"", ""),
            NetworkJson.Replace(",\"systemProxyResolution\":\"NotNeeded\"", ""),
            NetworkJson[..^1] + ",\"systemProxyConfig\":\"AutoConfig\"}",
            NetworkJson[..^1] + ",\"systemProxyResolution\":\"ResolvedProxy\"}",
            NetworkJson[..^1] + ",\"pacUrl\":\"https://private-token@private-host/private-path\"}"
        }) test("network diagnostic: rejects unsafe or malformed field " + malformedDiagnosticIndex++,
            () => Reject(() => Parse(WithNetwork(Envelope(), bad))));

        foreach (var field in new[] { "systemProxyAutoDetect", "systemProxyPacPresent", "systemProxyStaticPresent" })
        {
            test("network diagnostic: requires exactly one boolean for " + field, () =>
            {
                var property = ",\"" + field + "\":false";
                Reject(() => Parse(WithNetwork(Envelope(), NetworkJson.Replace(property, ""))));
                Reject(() => Parse(WithNetwork(Envelope(), NetworkJson[..^1] + property + "}")));
                foreach (var bad in new[] { "null", "0", "1", "\"false\"", "\"true\"", "[]", "{}",
                             "\"private-token@private-host/private-path\"" })
                    Reject(() => Parse(WithNetwork(Envelope(),
                        NetworkJson.Replace(property, ",\"" + field + "\":" + bad))));
            });
        }

        foreach (var field in new[] { "route", "proxyProtocol", "tlsBackend", "dotenv", "errorLayer", "ioKind",
                     "systemProxyConfig", "systemProxyResolution" })
            test("network diagnostic: rejects injected and numeric enum values for " + field, () =>
            {
                using var document = JsonDocument.Parse(NetworkJson);
                var fields = document.RootElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
                foreach (var bad in new object?[] { "private-token@private-host/private-path", "UnknownFutureValue",
                             "0", "999", "NotChecked, Absent", "notchecked", 0, 999, null, true })
                {
                    fields[field] = bad;
                    Reject(() => Parse(WithNetwork(Envelope(), JsonSerializer.Serialize(fields))));
                }
            });

        test("network diagnostic: every system proxy label roundtrips as a closed string", () =>
        {
            var seed = Parse(WithNetwork(Envelope())).Network!;
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            options.Converters.Add(new JsonStringEnumConverter());
            foreach (NetworkSystemProxyConfig config in Enum.GetValues(typeof(NetworkSystemProxyConfig)))
            foreach (NetworkSystemProxyResolution resolution in Enum.GetValues(typeof(NetworkSystemProxyResolution)))
            {
                var network = seed with { SystemProxyConfig = config, SystemProxyResolution = resolution };
                var json = JsonSerializer.Serialize(network, options);
                using var document = JsonDocument.Parse(json);
                Check.Equal(15, document.RootElement.EnumerateObject().Count());
                Check.Equal(config.ToString(), document.RootElement.GetProperty("systemProxyConfig").GetString());
                Check.Equal(resolution.ToString(), document.RootElement.GetProperty("systemProxyResolution").GetString());
                Check.Equal(network, Parse(WithNetwork(Envelope(), json)).Network);
            }
        });

        test("network diagnostic: proxy flags independently preserve static and automatic configuration", () =>
        {
            var seed = Parse(WithNetwork(Envelope())).Network!;
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            options.Converters.Add(new JsonStringEnumConverter());
            foreach (var autoDetect in new[] { false, true })
            foreach (var pacPresent in new[] { false, true })
            foreach (var staticPresent in new[] { false, true })
            {
                var network = seed with
                {
                    SystemProxyConfig = pacPresent ? autoDetect ? NetworkSystemProxyConfig.AutoConfigAndDetect :
                        NetworkSystemProxyConfig.AutoConfig : autoDetect ? NetworkSystemProxyConfig.AutoDetect :
                        staticPresent ? NetworkSystemProxyConfig.StaticOnly : NetworkSystemProxyConfig.Absent,
                    SystemProxyAutoDetect = autoDetect, SystemProxyPacPresent = pacPresent,
                    SystemProxyStaticPresent = staticPresent
                };
                var json = JsonSerializer.Serialize(network, options);
                using var document = JsonDocument.Parse(json);
                Check.Equal(autoDetect, document.RootElement.GetProperty("systemProxyAutoDetect").GetBoolean());
                Check.Equal(pacPresent, document.RootElement.GetProperty("systemProxyPacPresent").GetBoolean());
                Check.Equal(staticPresent, document.RootElement.GetProperty("systemProxyStaticPresent").GetBoolean());
                var parsed = Parse(WithNetwork(Envelope(), json)).Network!;
                Check.Equal(network, parsed);
                Check.Contains("systemProxyAutoDetect=" + (autoDetect ? "true" : "false"), parsed.ToString());
                Check.Contains("systemProxyPacPresent=" + (pacPresent ? "true" : "false"), parsed.ToString());
                Check.Contains("systemProxyStaticPresent=" + (staticPresent ? "true" : "false"), parsed.ToString());
            }
        });

        test("network diagnostic: unavailable resolution remains distinct from resolved direct", () =>
        {
            var json = NetworkJson.Replace("\"systemProxyResolution\":\"NotNeeded\"",
                "\"systemProxyResolution\":\"Unavailable\"");
            var network = Parse(WithNetwork(Envelope(), json)).Network!;
            Check.Equal(NetworkSystemProxyResolution.Unavailable, network.SystemProxyResolution);
            Check.Equal(NetworkRoute.EnvironmentProxy, network.Route);
            Check.Contains("systemProxyResolution=Unavailable", network.ToSafeLogCode());
            Reject(() => Parse(WithNetwork(Envelope(), json.Replace("\"Unavailable\"", "\"unavailable\""))));
        });

        test("network diagnostic: resolver before and after states preserve safe evidence", () =>
        {
            var seed = Parse(WithNetwork(Envelope())).Network!;
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            options.Converters.Add(new JsonStringEnumConverter());
            foreach (var (route, resolution, layer, osCode, error) in new[]
            {
                (NetworkRoute.Unknown, NetworkSystemProxyResolution.Resolving, NetworkErrorLayer.None, (int?)null, (string?)null),
                (NetworkRoute.WindowsAutoProxy, NetworkSystemProxyResolution.ResolvedProxy, NetworkErrorLayer.None, (int?)null, (string?)null),
                (NetworkRoute.Direct, NetworkSystemProxyResolution.ResolvedDirect, NetworkErrorLayer.None, (int?)null, (string?)null),
                (NetworkRoute.Unknown, NetworkSystemProxyResolution.Failed, NetworkErrorLayer.SystemProxy, (int?)12180, (string?)"ProxyConfiguration"),
                (NetworkRoute.Unknown, NetworkSystemProxyResolution.TimedOut, NetworkErrorLayer.SystemProxy, (int?)12002, (string?)"Timeout"),
                (NetworkRoute.Unknown, NetworkSystemProxyResolution.Unsupported, NetworkErrorLayer.SystemProxy, (int?)null, (string?)"ProxyConfiguration"),
                (NetworkRoute.Unknown, NetworkSystemProxyResolution.Unavailable, NetworkErrorLayer.SystemProxy, (int?)null, (string?)"ProxyConfiguration")
            })
            {
                var network = seed with { Route = route, SystemProxyConfig = NetworkSystemProxyConfig.AutoConfigAndDetect,
                    SystemProxyResolution = resolution, ErrorLayer = layer, OsCode = osCode,
                    SystemProxyAutoDetect = true, SystemProxyPacPresent = true, SystemProxyStaticPresent = true,
                    IoKind = NetworkIoKind.None, ProxyProtocol = route == NetworkRoute.WindowsAutoProxy
                        ? NetworkProxyProtocol.Http : NetworkProxyProtocol.None };
                var json = JsonSerializer.Serialize(network, options);
                var envelope = error is null ? Envelope() : Envelope("Unavailable", "UsageRead", error, result: null);
                var response = Parse(WithNetwork(envelope, json));
                Check.Equal(network, response.Network);
                Check.Contains("systemProxyResolution=" + resolution, response.Network!.ToSafeLogCode());
                Check.Equal(error == "Timeout" ? ReadStatus.Timeout : error is null ? ReadStatus.Ready : ReadStatus.Unavailable,
                    response.Read.Status);
            }
        });

        test("network diagnostic: direct Winsock access denial explains evidence without naming a policy", () =>
        {
            var network = Parse(WithNetwork(Envelope())).Network! with
            {
                Route = NetworkRoute.Direct, IoKind = NetworkIoKind.PermissionDenied, OsCode = 10013,
                SystemProxyConfig = NetworkSystemProxyConfig.AutoConfig,
                SystemProxyResolution = NetworkSystemProxyResolution.ResolvedDirect, SystemProxyPacPresent = true
            };
            var diagnostic = new CliDiagnostic(CliSource.EmbeddedReader, ReadOnlyQuotaProtocol.DiagnosticVersion,
                CliStage.UsageRead, ReadStatus.Unavailable, ErrorKind: CliErrorKind.ConnectFailure) { Network = network };
            Check.Equal("Windows拒绝了本次直连socket请求；具体拦截策略尚未确认。", diagnostic.Explanation);
            Check.Equal(diagnostic.Explanation, (diagnostic with { ErrorKind = CliErrorKind.NetworkFailure }).Explanation);
            foreach (var other in new[] { network with { Route = NetworkRoute.WindowsAutoProxy },
                         network with { ErrorLayer = NetworkErrorLayer.SystemProxy }, network with { OsCode = 10061 } })
                Check.Equal("额度请求在连接建立阶段失败。", (diagnostic with { Network = other }).Explanation);
            Check.Equal("额度请求在连接建立阶段失败。", (diagnostic with { Network = null }).Explanation);
        });

        test("embedded reader protocol: ready reuses quota and reset-card parsers", () =>
        {
            var actual = Parse(Envelope());
            Check.Equal(ReadStatus.Ready, actual.Read.Status);
            Check.Quota(actual.Read.Quota, 73, 73);
            Check.Equal(300, actual.Read.Quota!.WindowMinutes);
            Check.Equal(2, actual.Read.Selection!.AvailableWindows.Count);
            Check.Equal(ResetCardDataStatus.Details, actual.Read.ResetCards!.Status);
            Check.Equal(CliStage.RateLimitsRead, actual.Stage);
            Check.Equal(CliErrorKind.None, actual.ErrorKind);
        });
        test("embedded reader protocol: recognized empty quota bucket remains unknown", () =>
            Check.Equal(ReadStatus.NoQuotaWindow, Parse(Envelope(result: "{\"rateLimits\":{}}")).Read.Status));
        test("embedded reader protocol: missing quota bucket is malformed, not authoritative absence", () =>
            Check.Equal(ReadStatus.ProtocolError, Parse(Envelope(result: "{}")).Read.Status));
        test("embedded reader protocol: detail failure preserves good usage", () =>
        {
            var actual = Parse(Envelope(stage: "ResetCardsRead", error: "Forbidden", code: 403));
            Check.Equal(ReadStatus.Ready, actual.Read.Status);
            Check.Quota(actual.Read.Quota, 73, 73);
            Check.Equal(CliStage.ResetCardsRead, actual.Stage);
            Check.Equal(CliErrorKind.Forbidden, actual.ErrorKind);
            Check.Equal(403, actual.Code);
        });
        foreach (var (status, error, expected) in new[]
        {
            ("NeedsLogin", "MissingCredentials", ReadStatus.NotSignedIn),
            ("NeedsLogin", "InvalidCredentials", ReadStatus.NotSignedIn),
            ("NeedsLogin", "ExpiredCredentials", ReadStatus.CredentialsExpired),
            ("NeedsLogin", "Unauthorized", ReadStatus.CredentialsExpired),
            ("UnsupportedAuth", "UnsupportedAuth", ReadStatus.UnsupportedAccount),
            ("UnsupportedAuth", "EphemeralCredentials", ReadStatus.CredentialsUnavailable),
            ("UnsupportedAuth", "UnsupportedConfiguration", ReadStatus.AuthConfigurationUnsupported),
            ("Unavailable", "CredentialReadFailed", ReadStatus.CredentialsUnavailable),
            ("Unavailable", "CredentialKeyMissing", ReadStatus.CredentialsUnavailable),
            ("Unavailable", "AccountChanged", ReadStatus.NotSignedIn),
            ("Unavailable", "RateLimited", ReadStatus.RateLimited),
            ("Unavailable", "Timeout", ReadStatus.Timeout),
            ("Unavailable", "NetworkFailure", ReadStatus.Unavailable),
            ("Unavailable", "DnsFailure", ReadStatus.Unavailable),
            ("Unavailable", "ConnectFailure", ReadStatus.Unavailable),
            ("Unavailable", "ProxyConfiguration", ReadStatus.Unavailable),
            ("Unavailable", "ProxyConnectFailure", ReadStatus.Unavailable),
            ("Unavailable", "ProxyAuthentication", ReadStatus.Unavailable),
            ("Unavailable", "TlsFailure", ReadStatus.Unavailable),
            ("Unavailable", "CertificateConfiguration", ReadStatus.Unavailable),
            ("Unavailable", "NetworkConfiguration", ReadStatus.Unavailable),
            ("Unavailable", "Forbidden", ReadStatus.Unavailable),
            ("Unavailable", "RedirectBlocked", ReadStatus.Unavailable),
            ("Unavailable", "InternalError", ReadStatus.Unavailable),
            ("InvalidResponse", "InvalidResponse", ReadStatus.ProtocolError),
            ("InvalidResponse", "ResponseTooLarge", ReadStatus.ProtocolError)
        }) test("embedded reader protocol: fixed status " + error, () =>
        {
            var actual = Parse(Envelope(status, "AuthLoad", error, result: null));
            Check.Equal(expected, actual.Read.Status);
            Check.Null(actual.Read.Quota);
            Check.Null(actual.Read.Selection);
            Check.Null(actual.Read.ResetCards);
        });
        foreach (var (label, value) in new[]
        {
            ("empty", ""), ("truncated", Envelope()[..^3]), ("array", "[]"),
            ("second document", Envelope() + Envelope()),
            ("unknown protocol", Envelope().Replace("\"protocolVersion\":1", "\"protocolVersion\":2")),
            ("fractional protocol", Envelope().Replace("\"protocolVersion\":1", "\"protocolVersion\":1.5")),
            ("future helper", Envelope().Replace("0.2.16", "0.2.17")),
            ("previous helper", Envelope().Replace("0.2.16", "0.2.15")),
            ("unknown stage", Envelope(stage: "private-path")),
            ("unknown status", Envelope(status: "private-account")),
            ("unknown error", Envelope(error: "private-token")),
            ("wrong error type", Envelope().Replace("\"errorKind\":null", "\"errorKind\":17")),
            ("negative code", Envelope(stage: "ResetCardsRead", error: "Forbidden", code: -1)),
            ("oversize code", Envelope(stage: "ResetCardsRead", error: "Forbidden", code: 65536)),
            ("string code", Envelope().Replace("\"code\":null", "\"code\":\"401\"")),
            ("unknown field", Envelope()[..^1] + ",\"private\":\"secret\"}"),
            ("duplicate field", Envelope()[..^1] + ",\"status\":\"Ready\"}"),
            ("missing field", Envelope().Replace(",\"code\":null", "")),
            ("ready without result", Envelope(result: null)),
            ("ready at auth stage", Envelope(stage: "AuthLoad")),
            ("ready error at quota stage", Envelope(error: "Forbidden")),
            ("ready code without error", Envelope(code: 200)),
            ("failure with result", Envelope("NeedsLogin", "AuthLoad", "MissingCredentials")),
            ("failure without error", Envelope("NeedsLogin", "AuthLoad", result: null)),
            ("mismatched failure", Envelope("NeedsLogin", "AuthLoad", "NetworkFailure", result: null))
        }) test("embedded reader protocol: rejects " + label, () => Reject(() => Parse(value)));
        test("embedded reader protocol: rejects oversized bytes and excessive depth", () =>
        {
            Reject(() => ReadOnlyQuotaProtocol.Parse(new byte[ReadOnlyQuotaProtocol.MaximumResponseBytes + 1]));
            Reject(() => Parse(Envelope(result: new string('[', 40) + "{}" + new string(']', 40))));
        });
        asyncTest("embedded reader protocol: stream bound applies before JSON parsing", async () =>
        {
            using var input = new MemoryStream(new byte[ReadOnlyQuotaProtocol.MaximumResponseBytes + 1]);
            await Check.ThrowsAsync<InvalidDataException>(ReadOnlyQuotaProtocol.ReadAsync(input, CancellationToken.None));
        });
        test("embedded reader invocation: sole fixed argument with no shell", () =>
        {
            var start = ReadOnlyQuotaClient.CreateStartInfo(Path.Combine(Path.GetTempPath(), "reader.exe"));
            Check.Equal("--read", start.Arguments);
            Check.False(start.UseShellExecute);
            Check.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
            Check.True(start.CreateNoWindow);
        });

        test("embedded reader image: bounded extraction verifies, reuses and holds payload", () =>
        {
            using var files = new TempDirectory();
            var payload = Encoding.UTF8.GetBytes("Synthetic image; never executed.");
            var gzip = Compress(payload);
            var hash = Hash(payload);
            string path;
            using (var lease = HelperExecutableLease.Extract(files.Path, () => new MemoryStream(gzip), hash, payload.Length, gzip.Length))
            {
                path = lease.ExecutablePath;
                Check.True(File.ReadAllBytes(path).SequenceEqual(payload));
                Check.True(path.EndsWith(Path.Combine("0.2.16-" + hash, "codex-quota-reader.exe"), StringComparison.Ordinal));
                using var same = HelperExecutableLease.Extract(files.Path,
                    () => throw new TestFailure("Existing verified image must not reload payload."), hash, payload.Length, gzip.Length);
                Check.Equal(path, same.ExecutablePath);
                if (OperatingSystem.IsWindows())
                    Reject<IOException>(() => { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            }
            File.WriteAllBytes(path, new byte[payload.Length]);
            Reject(() => HelperExecutableLease.Extract(files.Path, () => new MemoryStream(gzip), hash, payload.Length, gzip.Length));
            Check.True(File.ReadAllBytes(path).All(value => value == 0));
        });
        foreach (var mode in new[] { "hash", "compressed short", "compressed long", "expanded short", "expanded long", "bad gzip" })
            test("embedded reader image: fails closed on " + mode, () =>
            {
                using var files = new TempDirectory();
                var payload = Encoding.UTF8.GetBytes("Synthetic image; never executed.");
                var gzip = Compress(payload);
                var hash = mode == "hash" ? new string('0', 64) : Hash(payload);
                var compressedLength = gzip.Length + (mode == "compressed short" ? -1 : mode == "compressed long" ? 1 : 0);
                var executableLength = payload.Length + (mode == "expanded short" ? -1 : mode == "expanded long" ? 1 : 0);
                if (mode == "bad gzip") gzip = new byte[gzip.Length];
                Reject(() => HelperExecutableLease.Extract(files.Path, () => new MemoryStream(gzip), hash, executableLength, compressedLength));
                Check.Equal(0, Directory.GetFiles(files.Path, "*.exe", SearchOption.AllDirectories).Length);
                Check.Equal(0, Directory.GetFiles(files.Path, "*.tmp", SearchOption.AllDirectories).Length);
            });
        test("embedded reader image: rejects unbounded metadata before opening resource", () =>
        {
            using var files = new TempDirectory();
            foreach (var (hash, length, compressed) in new[]
            {
                ("../bad", 1L, 1L), (new string('0', 64), 0L, 1L),
                (new string('0', 64), HelperExecutableLease.MaximumExecutableBytes + 1, 1L),
                (new string('0', 64), 1L, HelperExecutableLease.MaximumCompressedBytes + 1)
            }) Reject(() => HelperExecutableLease.Extract(files.Path,
                () => throw new TestFailure("Invalid metadata must not open payload."), hash, length, compressed));
        });
        if (!OperatingSystem.IsWindows())
        {
            test("embedded reader image: refuses reparse directory", () =>
            {
                using var files = new TempDirectory();
                var target = Directory.CreateDirectory(Path.Combine(files.Path, "target")).FullName;
                Directory.CreateSymbolicLink(Path.Combine(files.Path, "CodexQuotaTray"), target);
                var payload = new byte[] { 1, 2, 3 };
                var gzip = Compress(payload);
                Reject(() => HelperExecutableLease.Extract(files.Path, () => new MemoryStream(gzip), Hash(payload), payload.Length, gzip.Length));
                Check.Equal(0, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
            });
            test("embedded reader image: refuses matching symlink image", () =>
            {
                using var files = new TempDirectory();
                var payload = new byte[] { 1, 2, 3 };
                var target = Path.Combine(files.Path, "image");
                File.WriteAllBytes(target, payload);
                var link = Path.Combine(files.Path, "link");
                File.CreateSymbolicLink(link, target);
                Reject(() => HelperExecutableLease.OpenVerified(link, Hash(payload), payload.Length));
            });
        }

        asyncTest("embedded reader client: cancellation before resolve does not touch files", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var client = new ReadOnlyQuotaClient(() => throw new TestFailure("Canceled read must not resolve."));
            await Check.ThrowsAsync<OperationCanceledException>(client.ReadAsync(cancellation.Token));
        });
        asyncTest("embedded reader client: integrity failure emits only fixed labels and no fallback", async () =>
        {
            var calls = 0;
            var diagnostics = new List<CliDiagnostic>();
            var client = new ReadOnlyQuotaClient(() => { calls++; throw new InvalidDataException("private auth path and secret"); }, diagnostics.Add);
            Check.Equal(ReadStatus.ProtocolError, (await client.ReadAsync(CancellationToken.None)).Status);
            Check.Equal(1, calls);
            Check.Equal(CliStage.HelperExtract, diagnostics.Single().Stage);
            Check.Equal(CliErrorKind.HelperIntegrityFailure, diagnostics.Single().ErrorKind);
            Check.False(diagnostics.Single().ToSafeLogCode().Contains("private", StringComparison.Ordinal));
        });
        foreach (var (scenario, expected) in new[]
        {
            ("reader-success", ReadStatus.Ready), ("reader-malformed", ReadStatus.ProtocolError),
            ("reader-stdout-flood", ReadStatus.ProtocolError), ("reader-stderr-flood", ReadStatus.ProtocolError),
            ("reader-hang", ReadStatus.Timeout)
        }) asyncTest("embedded reader client: owned synthetic process " + scenario, async () =>
        {
            using var environment = new FakeChildEnvironment(scenario);
            var diagnostics = new List<CliDiagnostic>();
            var bytes = File.ReadAllBytes(environment.Executable);
            var client = new ReadOnlyQuotaClient(() => HelperExecutableLease.OpenVerified(environment.Executable, Hash(bytes), bytes.Length),
                diagnostics.Add, TimeSpan.FromSeconds(scenario == "reader-hang" ? 1 : 5));
            var watch = Stopwatch.StartNew();
            var result = await client.ReadAsync(CancellationToken.None);
            Check.Equal(expected, result.Status);
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(8));
            Check.Equal(1, diagnostics.Count);
            Check.Equal(CliSource.EmbeddedReader, diagnostics[0].Source);
            if (scenario == "reader-success")
            {
                Check.Equal(NetworkRoute.EnvironmentProxy, diagnostics[0].Network!.Route);
                Check.Equal(10061, diagnostics[0].Network!.OsCode);
            }
            Check.False(diagnostics[0].ToSafeLogCode().Contains("private", StringComparison.Ordinal));
            await environment.AssertChildrenExitedAsync(1);
        });
        asyncTest("embedded reader client: user cancellation propagates and reaps owned process", async () =>
        {
            using var environment = new FakeChildEnvironment("reader-hang");
            using var cancellation = new CancellationTokenSource();
            var bytes = File.ReadAllBytes(environment.Executable);
            var client = new ReadOnlyQuotaClient(() => HelperExecutableLease.OpenVerified(environment.Executable, Hash(bytes), bytes.Length));
            var pending = client.ReadAsync(cancellation.Token);
            await environment.WaitForChildAsync();
            cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(pending);
            await environment.AssertChildrenExitedAsync(1);
        });
        asyncTest("embedded reader client: observer failure does not break successful quota", async () =>
        {
            using var environment = new FakeChildEnvironment("reader-success");
            var bytes = File.ReadAllBytes(environment.Executable);
            var client = new ReadOnlyQuotaClient(() => HelperExecutableLease.OpenVerified(environment.Executable, Hash(bytes), bytes.Length),
                _ => throw new IOException("private observer failure"));
            Check.Equal(ReadStatus.Ready, (await client.ReadAsync(CancellationToken.None)).Status);
            await environment.AssertChildrenExitedAsync(1);
        });
    }

    private static ReadOnlyQuotaProtocol.Response Parse(string value) => ReadOnlyQuotaProtocol.Parse(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
    private static void Reject(Action action) => Reject<InvalidDataException>(action);
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new TestFailure("Expected " + typeof(T).Name);
    }
}

internal static class FakeReadOnlyQuotaReader
{
    public static async Task<int> RunAsync()
    {
        var scenario = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_SCENARIO");
        var marker = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_PID_FILE");
        if (string.IsNullOrEmpty(marker) || scenario is not ("reader-success" or "reader-malformed" or "reader-stdout-flood" or "reader-stderr-flood" or "reader-hang")) return 64;
        File.AppendAllText(marker, Environment.ProcessId + Environment.NewLine);
        if (scenario == "reader-hang") { await Task.Delay(Timeout.Infinite); return 0; }
        if (scenario == "reader-malformed") { await Console.Out.WriteAsync("{private malformed"); return 0; }
        if (scenario is "reader-stdout-flood" or "reader-stderr-flood")
        {
            var writer = scenario == "reader-stdout-flood" ? Console.Out : Console.Error;
            var chunk = new string('x', 8192);
            for (var index = 0; index < 256; index++) await writer.WriteAsync(chunk);
            await writer.FlushAsync();
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        await Console.Out.WriteLineAsync(ReadOnlyQuotaReaderTests.WithNetwork(ReadOnlyQuotaReaderTests.Envelope()));
        return 0;
    }
}
