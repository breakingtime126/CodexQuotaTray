using System.Text;
using System.Text.Json;
using CodexQuota.Core;

internal static class InstanceHandoffTests
{
    private static readonly Version Current = new(0, 2, 0, 0);
    private static readonly Version Newer = new(0, 3, 0, 0);
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string User = "S-1-5-21-123-456-789-1001";
    private const string Logon = "S-1-5-5-123-456";

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("handoff: identity failure never claims an existing process or tells user to close one", () =>
        {
            var message = HandoffStartupFailure.Message(HandoffStartupStage.Identity);
            Check.Contains("身份检查", message);
            Check.False(message.Contains("已有"));
            Check.False(message.Contains("旧版"));
            Check.False(message.Contains("退出"));
        });
        test("handoff: coordination failure is distinct from a verified occupied instance lock", () =>
        {
            var message = HandoffStartupFailure.Message(HandoffStartupStage.Coordination);
            Check.Contains("单实例启动检查", message);
            Check.False(message.Contains("旧版"));
            Check.False(message.Contains("正在运行"));
        });
        test("handoff: unavailable peer gives conditional manual exit guidance without asserting a running old EXE", () =>
        {
            var message = HandoffStartupFailure.Message(HandoffStartupStage.ExistingInstance);
            Check.Contains("如果旧版仍在运行", message);
            Check.False(message.Contains("已有额度程序正在运行"));
        });
        test("handoff: only numerically newer verified release prepares", () =>
        {
            foreach (var version in new[] { new Version(0, 2, 0, 1), Newer, new Version(0, 10, 0, 0), new Version(1, 0, 0, 0) })
                Check.Equal(HandoffDisposition.Prepare, InstanceHandoffProtocol.Evaluate(Current, version, Hello(version)));
        });
        test("handoff: equal and older releases keep the existing instance", () =>
        {
            foreach (var version in new[] { Current, new Version(0, 1, 0, 0), new Version(0, 0, 99, 99) })
                Check.Equal(HandoffDisposition.KeepExisting, InstanceHandoffProtocol.Evaluate(Current, version, Hello(version)));
        });
        test("handoff: release version is strict four-part metadata, not a filename or timestamp", () =>
        {
            foreach (var value in new string?[] { null, "", "v6", "0.3.0", "0.3.0.0+hash", "0.03.0.0", "0.3.0.-1", "0.65536.0.0", "20261008", " 0.3.0.0" })
                Check.False(InstanceHandoffProtocol.TryVersion(value, out _), value ?? "null");
            Check.True(InstanceHandoffProtocol.TryVersion("0.10.0.0", out var version));
            Check.True(version > new Version(0, 9, 0, 0));
        });
        test("handoff: peer cannot claim a version different from its verified executable", () =>
            Check.Equal(HandoffDisposition.Reject, InstanceHandoffProtocol.Evaluate(Current, Current, Hello(Newer))));
        test("handoff: unknown product, protocol and command fail closed", () =>
        {
            foreach (var message in new[] { Hello(Newer) with { Product = "other" }, Hello(Newer) with { Protocol = 99 },
                Hello(Newer) with { Kind = "exit" }, Hello(Newer) with { Nonce = "short" }, Hello(Newer) with { CliPath = "injected" } })
                Check.Equal(HandoffDisposition.Reject, InstanceHandoffProtocol.Evaluate(Current, Newer, message));
        });
        test("handoff: different user, logon or session is rejected", () =>
        {
            foreach (var peer in new[] { Identity() with { UserSid = "other" }, Identity() with { LogonSid = "other" },
                Identity() with { Session = 2 }, Identity() with { UserSid = null }, Identity() with { LogonSid = null } })
                Check.False(InstanceHandoffProtocol.VerifyIdentity(peer, User, Logon, 1, out _));
        });
        test("handoff: unrelated product and invalid metadata are rejected", () =>
        {
            foreach (var peer in new[] { Identity() with { ProductName = "Codex" }, Identity() with { OriginalFilename = "other.exe" },
                Identity() with { FileVersion = "v7" }, Identity() with { FileVersion = null } })
                Check.False(InstanceHandoffProtocol.VerifyIdentity(peer, User, Logon, 1, out _));
        });
        test("handoff: both Framework EXE and apphost DLL metadata are supported without same-path restriction", () =>
        {
            foreach (var original in new[] { "CodexQuotaTray.exe", "CodexQuotaTray.dll" })
            {
                Check.True(InstanceHandoffProtocol.VerifyIdentity(Identity() with { OriginalFilename = original }, User, Logon, 1, out var version));
                Check.Equal(Newer, version);
            }
        });
        test("handoff: prepare alone and disconnected peers never authorize exit", () =>
        {
            var gate = new HandoffCommitGate();
            Check.True(gate.Prepare(Current, Newer, Hello(Newer)));
            Check.False(gate.IsCommitted);
            Check.False(new HandoffCommitGate().Commit(Commit()));
        });
        test("handoff: commit must match nonce, version, protocol and exact command", () =>
        {
            foreach (var message in new[] { Commit() with { Nonce = Guid.NewGuid().ToString("N") }, Commit() with { Version = Current.ToString(4) },
                Commit() with { Protocol = 2 }, Commit() with { Product = "other" }, Commit() with { Kind = "hello" }, Commit() with { CliPath = "injected" } })
            {
                var gate = new HandoffCommitGate();
                Check.True(gate.Prepare(Current, Newer, Hello(Newer)));
                Check.False(gate.Commit(message));
                Check.False(gate.IsCommitted);
            }
        });
        test("handoff: only one matching commit can authorize shutdown", () =>
        {
            var gate = new HandoffCommitGate();
            Check.True(gate.Prepare(Current, Newer, Hello(Newer)));
            Check.False(gate.Prepare(Current, Newer, Hello(Newer) with { Nonce = Guid.NewGuid().ToString("N") }));
            Check.True(gate.Commit(Commit()));
            Check.True(gate.IsCommitted);
            Check.False(gate.Commit(Commit()));
            Check.False(gate.Prepare(Current, Newer, Hello(Newer)));
        });
        test("handoff: older or equal launch cannot reserve shutdown", () =>
        {
            foreach (var peer in new[] { Current, new Version(0, 1, 0, 0) })
                Check.False(new HandoffCommitGate().Prepare(Current, peer, Hello(peer)));
        });
        test("handoff: explicit CLI choice wins; ordinary double-click inherits existing CLI", () =>
        {
            Check.Equal(@"C:\existing\codex.cmd", InstanceHandoffProtocol.SelectCliPath(null, @"C:\existing\codex.cmd"));
            Check.Equal(@"D:\explicit\codex.js", InstanceHandoffProtocol.SelectCliPath(@"D:\explicit\codex.js", @"C:\existing\codex.cmd"));
            Check.Null(InstanceHandoffProtocol.SelectCliPath(null, null));
        });
        test("handoff: overlong or control-character CLI payload is rejected", () =>
        {
            foreach (var value in new[] { "", new string('x', 4097), "a\0b", "a\rb", "a\nb" })
                Check.False(InstanceHandoffProtocol.IsValid(Hello(Newer) with { CliPath = value }));
        });
        asyncTest("handoff: bounded frames round-trip Unicode CLI choices", async () =>
        {
            using var stream = new MemoryStream();
            var message = InstanceHandoffProtocol.Message("ready", Current, Nonce, @"C:\工具 & 配额\codex.cmd");
            await InstanceHandoffProtocol.WriteAsync(stream, message, CancellationToken.None);
            stream.Position = 0;
            Check.Equal(message, await InstanceHandoffProtocol.ReadAsync(stream, CancellationToken.None));
        });
        asyncTest("handoff: partial stream reads preserve frame boundaries", async () =>
        {
            using var output = new MemoryStream();
            await InstanceHandoffProtocol.WriteAsync(output, Hello(Newer), CancellationToken.None);
            using var input = new FragmentedStream(output.ToArray());
            Check.Equal(Hello(Newer), await InstanceHandoffProtocol.ReadAsync(input, CancellationToken.None));
        });
        asyncTest("handoff: zero, negative and oversized frame lengths fail before payload allocation", async () =>
        {
            foreach (var length in new[] { 0, -1, 8193, int.MaxValue })
            {
                using var stream = new MemoryStream(BitConverter.GetBytes(length));
                await Check.ThrowsAsync<InvalidDataException>(InstanceHandoffProtocol.ReadAsync(stream, CancellationToken.None));
            }
        });
        asyncTest("handoff: dropped connection during header or payload cannot commit", async () =>
        {
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 1, 0 }, new byte[] { 10, 0, 0, 0, 123 } })
            {
                using var stream = new MemoryStream(bytes);
                await Check.ThrowsAsync<EndOfStreamException>(InstanceHandoffProtocol.ReadAsync(stream, CancellationToken.None));
            }
        });
        asyncTest("handoff: malformed and null JSON are rejected", async () =>
        {
            foreach (var json in new[] { "{", "null", "{}" })
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                using var stream = new MemoryStream(BitConverter.GetBytes(bytes.Length).Concat(bytes).ToArray());
                var pending = InstanceHandoffProtocol.ReadAsync(stream, CancellationToken.None);
                if (json == "{") await Check.ThrowsAsync<JsonException>(pending);
                else await Check.ThrowsAsync<InvalidDataException>(pending);
            }
        });
        asyncTest("handoff: stalled read observes cancellation without authorizing exit", async () =>
        {
            using var stream = new StalledStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var gate = new HandoffCommitGate();
            Check.True(gate.Prepare(Current, Newer, Hello(Newer)));
            await Check.ThrowsAsync<OperationCanceledException>(InstanceHandoffProtocol.ReadAsync(stream, timeout.Token));
            Check.False(gate.IsCommitted);
        });
        asyncTest("handoff: oversized serialized payload is rejected before writing", async () =>
        {
            using var stream = new MemoryStream();
            await Check.ThrowsAsync<InvalidDataException>(InstanceHandoffProtocol.WriteAsync(stream,
                InstanceHandoffProtocol.Message("ready", Current, Nonce, new string('中', 4096)), CancellationToken.None));
            Check.Equal(0L, stream.Length);
        });
    }

    private static HandoffMessage Hello(Version version) => InstanceHandoffProtocol.Message("hello", version, Nonce);
    private static HandoffMessage Commit() => InstanceHandoffProtocol.Message("commit", Newer, Nonce);
    private static HandoffPeerIdentity Identity() => new(User, Logon, 1, "CodexQuotaTray", "CodexQuotaTray.exe", Newer.ToString(4));

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);
    }
    private sealed class StalledStream : MemoryStream
    {
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
