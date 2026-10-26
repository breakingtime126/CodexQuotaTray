using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class ReaderPerformanceTests
{
    private const string Owner = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        asyncTest("reader idle: unchanged files only verify prefix and prior anchor", async () =>
        {
            using var home = new Home();
            for (var index = 0; index < 24; index++)
            {
                var id = index == 0 ? Owner : Guid.NewGuid().ToString("D");
                home.Write(id, Header(id) + Turn(id, "sample", 0));
            }
            using var reader = home.Reader();
            var first = await reader.PollAsync();
            Check.Equal(24, first.TrackedFileCount);
            Check.False(first.CanCatchUp);
            for (var index = 0; index < 3; index++)
            {
                var idle = await reader.PollAsync();
                Check.Equal(first.Sample, idle.Sample);
                Check.False(idle.CanCatchUp);
                Check.Equal(24 * 512, idle.BytesRead);
            }
        });

        asyncTest("reader idle: same-size same-time replacement still invalidates the cursor", async () =>
        {
            using var home = new Home();
            var path = home.Write(Owner, Header(Owner) + Turn(Owner, "first", 0));
            using var reader = home.Reader();
            Check.Equal("first", (await reader.PollAsync()).Sample!.ResponseId);
            var original = new FileInfo(path);
            var length = original.Length;
            var modified = original.LastWriteTimeUtc;
            File.WriteAllText(path, Header(Owner) + Turn(Owner, "other", 0), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, modified);
            Check.Equal(length, new FileInfo(path).Length);
            var replacement = await reader.PollAsync();
            Check.Equal("other", replacement.Sample!.ResponseId);
            Check.True(replacement.BytesRead > 512);
            Check.Equal(512, (await reader.PollAsync()).BytesRead);
        });

        asyncTest("reader idle: incomplete line waits without catchup and resumes after append", async () =>
        {
            using var home = new Home();
            var path = home.Write(Owner, Header(Owner) + Start(Owner, "partial", 0));
            using var reader = home.Reader();
            await reader.PollAsync();
            var body = Line(1, "response_item", new { type = "function_call_output", call_id = "tool", output = new string('x', 4096) });
            File.AppendAllText(path, body.Substring(0, body.Length - 1));
            var partial = await reader.PollAsync();
            Check.True(partial.PendingWork);
            Check.False(partial.CanCatchUp);
            var idle = await reader.PollAsync();
            Check.True(idle.PendingWork);
            Check.False(idle.CanCatchUp);
            Check.Equal(512, idle.BytesRead);
            File.AppendAllText(path, "\n" + End(Owner, "partial", 10));
            var complete = await reader.PollAsync();
            Check.Equal("partial", complete.Sample!.ResponseId);
            Check.False(complete.PendingWork);
        });

        asyncTest("reader idle: pending cold history is never mistaken for an unchanged complete file", async () =>
        {
            using var home = new Home();
            home.Write(Owner, Header(Owner) + Start(Owner, "history", 0) +
                string.Concat(Enumerable.Repeat(Line(1, "response_item", new { type = "function_call_output", call_id = "tool", output = new string('x', 8000) }), 100)) +
                End(Owner, "history", 10));
            using var reader = home.Reader();
            var result = await reader.PollAsync();
            Check.True(result.HistoryRecoveryPending);
            for (var index = 0; index < 20 && result.CanCatchUp; index++) result = await reader.PollAsync();
            Check.False(result.CanCatchUp);
            Check.Equal("history", result.Sample!.ResponseId);
            Check.Equal(512, (await reader.PollAsync()).BytesRead);
        });

        asyncTest("reader discovery: cached oldest selection is invalidated by tracked write-time changes", async () =>
        {
            using var home = new Home();
            var paths = new List<string>();
            for (var index = 0; index < 40; index++)
            {
                var id = index.ToString("x8") + "-1111-4111-8111-111111111111";
                var path = home.Write(id, Header(id));
                File.SetLastWriteTimeUtc(path, Epoch.AddSeconds(index).UtcDateTime);
                paths.Add(path);
            }
            using var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero });
            await reader.PollAsync();
            VerifyTracked(reader, paths);
            // Promote the previous oldest and admit a new candidate after it.
            // An older untracked entry may already have populated the cache.
            File.SetLastWriteTimeUtc(paths[16], Epoch.AddSeconds(100).UtcDateTime);
            var freshId = "00000040-1111-4111-8111-111111111111";
            var fresh = home.Write(freshId, Header(freshId));
            File.SetLastWriteTimeUtc(fresh, Epoch.AddSeconds(90).UtcDateTime);
            paths.Add(fresh);
            await reader.PollAsync();
            VerifyTracked(reader, paths);
            // A tracked member can also become the oldest after clock rollback.
            File.SetLastWriteTimeUtc(paths[39], Epoch.AddSeconds(-100).UtcDateTime);
            await reader.PollAsync();
            await reader.PollAsync(); // An earlier rejected candidate is reconsidered next scan.
            VerifyTracked(reader, paths);
        });

        asyncTest("reader discovery: cached oldest preserves deterministic equal-time path ordering", async () =>
        {
            using var home = new Home();
            var paths = new List<string>();
            for (var index = 40; index >= 1; index--)
            {
                var id = index.ToString("x8") + "-1111-4111-8111-111111111111";
                var path = home.Write(id, Header(id));
                File.SetLastWriteTimeUtc(path, Epoch.UtcDateTime);
                paths.Add(path);
            }
            using var reader = new RolloutSpeedReader(home.Path, new() { DiscoveryInterval = TimeSpan.Zero });
            await reader.PollAsync();
            VerifyTracked(reader, paths);
            var firstId = "00000000-1111-4111-8111-111111111111";
            var first = home.Write(firstId, Header(firstId));
            File.SetLastWriteTimeUtc(first, Epoch.UtcDateTime);
            paths.Add(first);
            await reader.PollAsync();
            VerifyTracked(reader, paths);
        });
    }

    private static void VerifyTracked(RolloutSpeedReader reader, IEnumerable<string> paths)
    {
        var comparer = System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expected = paths.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, comparer).Take(24).OrderBy(path => path, comparer);
        var tracked = (System.Collections.IDictionary)typeof(RolloutSpeedReader).GetField("files",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(reader)!;
        var actual = tracked.Keys.Cast<string>().OrderBy(path => path, comparer);
        Check.Equal(string.Join("\n", expected), string.Join("\n", actual));
    }

    private static string Header(string id) => Line(0, "session_meta", new { id });
    private static string Start(string id, string turn, int second) =>
        Line(second, "event_msg", new { type = "task_started", thread_id = id, turn_id = turn }) +
        Line(second, "turn_context", new { turn_id = turn, model = "gpt-6.1-sol", effort = "medium" });
    private static string End(string id, string turn, int second) =>
        Line(second, "token_usage_record", new { thread_id = id, turn_id = turn, response_id = turn, model = "gpt-6.1-sol", usage = new { output_tokens = 100 } }) +
        Line(second + 1, "event_msg", new { type = "task_complete", thread_id = id, turn_id = turn });
    private static string Turn(string id, string turn, int second) => Start(id, turn, second) + End(id, turn, second + 10);
    private static string Line(int second, string type, object payload) =>
        JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(second), type, payload }) + "\n";

    private sealed class Home : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cqt-perf-test-" + Guid.NewGuid().ToString("N"));
        public Home() => Directory.CreateDirectory(System.IO.Path.Combine(Path, "sessions"));
        public string Write(string id, string content)
        {
            var path = System.IO.Path.Combine(Path, "sessions", "rollout-" + id + ".jsonl");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        public RolloutSpeedReader Reader() => new(Path, new() { DiscoveryInterval = TimeSpan.FromHours(1) });
        public void Dispose() => Directory.Delete(Path, true);
    }
}
