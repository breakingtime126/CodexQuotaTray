using System.Text;
using System.Text.Json;
using CodexQuota.Core;

static class ReaderCatchupTests
{
    private const string Owner = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset Epoch = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    public static void Register(Action<string, Action> test, Action<string, Func<Task>> asyncTest)
    {
        test("reader catchup: fresh discovery defaults to three seconds", () =>
            Check.Equal(TimeSpan.FromSeconds(3), new RolloutSpeedReaderOptions().DiscoveryInterval));
        asyncTest("reader catchup: persistent discovery reaches an active old date beyond 64 directories", async () =>
        {
            foreach (var targetIndex in new[] { 0, 89 })
            {
            using var home = new Home();
            string? target = null;
            for (var i = 0; i < 90; i++)
            {
                var id = i == targetIndex ? Owner : Guid.NewGuid().ToString("D");
                var path = home.Write(Header(id) + (i == targetIndex ? Turn("old-date", 0) : ""), id,
                    new DateTime(2026, 6, 1).AddDays(i).ToString("yyyy/MM/dd"));
                File.SetLastWriteTimeUtc(path, Epoch.UtcDateTime);
                if (i == targetIndex) target = path;
            }
            File.SetLastWriteTimeUtc(target!, Epoch.AddDays(1).UtcDateTime);
            using var reader = home.Reader();
            var result = await reader.PollAsync();
            for (var i = 0; i < 12 && (result.DiscoveryPending || result.Sample?.ResponseId != "old-date"); i++)
            {
                Budget(result); Check.True(result.TrackedFileCount <= 24);
                if (result.DiscoveryPending) Check.True(result.CanCatchUp);
                result = await reader.PollAsync();
            }
            Check.Equal("old-date", result.Sample!.ResponseId);
            Check.True(result.DiscoveryEntriesVisited <= 4096 && result.DiscoveryDirectoriesVisited <= 128);
            }
        });
        asyncTest("reader catchup: cold tail reconstructs response start in bounded polls", async () =>
        {
            using var home = new Home();
            home.Write(Header() + Start("cold", 0) + string.Concat(Enumerable.Repeat(Tool(8000, 1), 90)) + Usage("cold", "cold", 10));
            using var reader = home.Reader();
            var result = await reader.PollAsync();
            Check.True(result.HistoryRecoveryPending);
            result = await Settle(reader, result);
            Check.Equal("cold", result.Sample!.ResponseId);
            Check.Equal(OutputSpeedTimingReliability.ResponseRateEstimate, result.Sample.TimingReliability);
            Check.True(result.Sample.TokensPerSecond.HasValue);
            var idle = await reader.PollAsync();
            Check.False(idle.CanCatchUp); Check.True(idle.BytesRead <= 1024);
        });
        asyncTest("reader catchup: oversized valid tool body preserves response and whole-turn evidence", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Turn("old", 0));
            using var reader = home.Reader(); await reader.PollAsync();
            File.AppendAllText(path, Start("new", 20) + Tool(140 * 1024, 21) + Usage("new", "new", 30) + Complete("new", 31));
            var result = await Settle(reader, await reader.PollAsync());
            Check.Equal("new", result.Sample!.ResponseId);
            Check.Equal(OutputSpeedTimingReliability.ResponseRateEstimate, result.Sample.TimingReliability);
        });
        asyncTest("reader catchup: malformed discarded body and unknown metadata remain fail closed", async () =>
        {
            foreach (var damaged in new[]
            {
                Tool(140 * 1024, 21).Replace("xxxx", "xxx\\q", StringComparison.Ordinal),
                Line(21, "unknown_record", new { output = new string('x', 140 * 1024) }),
                Line(21, "token_usage_record", new { thread_id = Owner, turn_id = "new", response_id = "broken", usage = new { output_tokens = new string('9', 140 * 1024) } }),
                Tool(140 * 1024, 21).Replace("xxxx", "xxx\\uD800", StringComparison.Ordinal)
            })
            {
                using var home = new Home(); var path = home.Write(Header() + Turn("old", 0));
                using var reader = home.Reader(); await reader.PollAsync();
                File.AppendAllText(path, Start("new", 20) + damaged + Usage("new", "new", 30));
                var result = await Settle(reader, await reader.PollAsync());
                Check.Equal("old", result.Sample!.ResponseId);
            }
        });
        asyncTest("reader catchup: partial huge line waits for flush without fast spinning", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Start("partial", 0));
            using var reader = home.Reader(); await reader.PollAsync();
            var record = Tool(600 * 1024, 1);
            File.AppendAllText(path, record.Substring(0, record.Length - 2));
            var result = await reader.PollAsync();
            Check.True(result.ForwardBacklogPending); Check.True(result.CanCatchUp);
            for (var i = 0; i < 10 && result.CanCatchUp; i++) result = await reader.PollAsync();
            Check.True(result.PendingWork); Check.False(result.CanCatchUp);
            File.AppendAllText(path, record.Substring(record.Length - 2) + Usage("partial", "partial", 10));
            Check.Equal("partial", (await Settle(reader, await reader.PollAsync())).Sample!.ResponseId);
        });
        asyncTest("reader catchup: backlog makes explicit forward progress within existing budgets", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Start("backlog", 0));
            using var reader = home.Reader(); await reader.PollAsync();
            File.AppendAllText(path, string.Concat(Enumerable.Repeat(Tool(8000, 1), 520)) + Usage("backlog", "backlog", 10));
            var result = await reader.PollAsync(); var polls = 1;
            Check.True(result.CanCatchUp && result.ForwardBacklogPending);
            while (result.CanCatchUp && polls++ < 30) { Budget(result); result = await reader.PollAsync(); }
            Check.Equal("backlog", result.Sample!.ResponseId); Check.True(polls <= 12);
        });
        asyncTest("reader catchup: same timestamp and clock rollback append outrank older observations", async () =>
        {
            foreach (var secondStart in new[] { 0, -20 })
            {
                using var home = new Home(); var path = home.Write(Header() + Turn("first", 0));
                using var reader = home.Reader(); await reader.PollAsync();
                File.AppendAllText(path, Turn("second", secondStart));
                var result = await reader.PollAsync(); Check.Equal("second", result.Sample!.ResponseId);
                home.Write(Header() + Turn("first", 0), Owner, archive: true);
                Check.Equal("second", (await reader.PollAsync()).Sample!.ResponseId);
            }
        });
        asyncTest("reader catchup: incomplete later activity retains the complete atomic sample", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Turn("old", 0));
            using var reader = home.Reader(); var first = await reader.PollAsync();
            File.AppendAllText(path, Start("next", 20));
            var next = await reader.PollAsync(); Check.Equal(first.Sample!.ResponseId, next.Sample!.ResponseId);
            Check.Equal(first.Sample.Model, next.Sample.Model); Check.True(next.Sample.IsRetainedTurnAverage);
        });
        asyncTest("reader catchup: truncation cancels cold history without reviving old response", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Start("old", 0) + string.Concat(Enumerable.Repeat(Tool(8000, 1), 250)) + Usage("old", "old", 10));
            using var reader = home.Reader(); Check.True((await reader.PollAsync()).HistoryRecoveryPending);
            File.WriteAllText(path, Header() + Turn("replacement", 20));
            var result = await Settle(reader, await reader.PollAsync()); Check.Equal("replacement", result.Sample!.ResponseId);
            Check.False(result.HistoryRecoveryPending);
        });
        asyncTest("reader catchup: shared-lock errors do not request busy catchup retries", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Start("old", 0) + string.Concat(Enumerable.Repeat(Tool(8000, 1), 250)) + Usage("old", "old", 10));
            using var reader = home.Reader(); Check.True((await reader.PollAsync()).HistoryRecoveryPending);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await reader.PollAsync(); Check.True(result.HasReadErrors); Check.False(result.CanCatchUp);
        });
        test("reader projection: body-first metadata retains order and rejects duplicate scope", () =>
        {
            var at = Epoch.ToString("O");
            var body = new string('x', 140 * 1024);
            var valid = "{\"payload\":{\"output\":\"" + body + "\",\"call_id\":\"tool\",\"type\":\"function_call_output\"},\"timestamp\":\"" + at + "\",\"type\":\"response_item\"}";
            Check.True(Project(valid) is not null);
            foreach (var damaged in new[]
            {
                valid.Replace("\"call_id\"", "\"type\":\"message\",\"call_id\"", StringComparison.Ordinal),
                valid.Substring(0, valid.Length - 1) + ",\"type\":\"event_msg\"}",
                valid.Substring(0, valid.Length - 1) + ",\"payload\":{}}"
            }) Check.Null(Project(damaged));
        });
        test("reader projection: nesting and property limits are strict and unknown body is discarded", () =>
        {
            var secret = "FIXTURE_BODY_MUST_NOT_SURVIVE";
            var text = Line(1, "response_item", new { type = "function_call_output", call_id = "tool", output = secret, future_extra = secret }).TrimEnd('\n');
            var projection = NewProjection(); FeedProjection(projection, Encoding.UTF8.GetBytes(text.Substring(0, text.Length - 1)));
            var metadata = (System.Collections.Generic.List<byte>)projection.GetType().GetField("projected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(projection)!;
            Check.False(Encoding.UTF8.GetString(metadata.ToArray()).Contains(secret, StringComparison.Ordinal));
            FeedProjection(projection, new byte[] { (byte)'}' }); Check.True(FinishProjection(projection) is not null);
            Check.Null(Project("{\"" + new string('k', 257) + "\":null}"));
            Check.Null(Project(new string('[', 65) + "0" + new string(']', 65)));
            Check.Null(Project("{\"n\":01}"));
        });
        test("reader projection: chunked UTF8 and surrogate pairs are validated without retaining bodies", () =>
        {
            var prefix = "{\"timestamp\":\"" + Epoch.ToString("O") + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":\"";
            var data = Encoding.UTF8.GetBytes(prefix + new string('x', 140 * 1024) + "é😀\\uD83D\\uDE00\"}}");
            var projection = NewProjection();
            foreach (var value in data) FeedProjection(projection, new[] { value });
            Check.True(FinishProjection(projection) is not null);
            var invalid = Encoding.UTF8.GetBytes(prefix).Concat(new byte[] { 0xc0, 0xaf }).Concat(Encoding.UTF8.GetBytes("\"}}")).ToArray();
            projection = NewProjection(); FeedProjection(projection, invalid); Check.Null(FinishProjection(projection));
        });
        test("reader projection: huge error preserves nonnull rejection semantics without body", () =>
        {
            var output = Project(Line(2, "event_msg", new { type = "task_complete", thread_id = Owner, turn_id = "error", error = new string('x', 140 * 1024) }).TrimEnd('\n'));
            Check.True(output is not null);
            using var document = JsonDocument.Parse(output!);
            Check.Equal(JsonValueKind.True, document.RootElement.GetProperty("payload").GetProperty("error").ValueKind);
            Check.True(output!.Length < 1024);
        });
        asyncTest("reader catchup: late replay and post-cancellation replay cannot revoke completed response", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Start("turn", 0) + Lifecycle("first-output", "turn", 10) + Usage("turn", "first", 10));
            using var reader = home.Reader(); Check.Equal("first", (await reader.PollAsync()).Sample!.ResponseId);
            File.AppendAllText(path, Lifecycle("next-output", "turn", 11) + Usage("turn", "first", 10));
            Check.Equal("first", (await reader.PollAsync()).Sample!.ResponseId);
            File.AppendAllText(path, Line(12, "event_msg", new { type = "turn_aborted", turn_id = "turn" }) + Usage("turn", "first", 10));
            Check.Equal("first", (await reader.PollAsync()).Sample!.ResponseId);
        });
        asyncTest("reader catchup: historical identity boundary cannot withdraw later forward sample", async () =>
        {
            using var home = new Home();
            home.Write(Header() + Header("22222222-2222-4222-8222-222222222222") +
                string.Concat(Enumerable.Repeat(Tool(8000, 1), 90)) + Header() + Turn("later", 20));
            using var reader = home.Reader(); var first = await reader.PollAsync(); Check.Equal("later", first.Sample!.ResponseId);
            Check.Equal("later", (await Settle(reader, first)).Sample!.ResponseId);
        });
        asyncTest("reader catchup: completion setting conflicts remain unknown on replay", async () =>
        {
            foreach (var priorityFirst in new[] { true, false })
            {
                using var home = new Home();
                string Records(bool priority) => Header() + Line(0, "event_msg", new { type = "thread_settings_applied", thread_id = Owner, thread_settings = new { service_tier = priority ? "priority" : "default" } }) + Turn("same", 1);
                var path = home.Write(Records(priorityFirst)); using var reader = home.Reader(); await reader.PollAsync();
                home.Write(Records(!priorityFirst), archive: true);
                Check.Equal(ThreadRequestedSetting.Unknown, (await reader.PollAsync()).Sample!.ThreadRequestedSetting);
                File.AppendAllText(path, Records(priorityFirst));
                Check.Equal(ThreadRequestedSetting.Unknown, (await reader.PollAsync()).Sample!.ThreadRequestedSetting);
            }
        });
        asyncTest("reader catchup: capped cold history searches the latest window and stops outside it", async () =>
        {
            foreach (var withinWindow in new[] { true, false })
            {
                using var home = new Home();
                var padding = string.Concat(Enumerable.Repeat(Tool(4000, 1), 24));
                var recent = string.Concat(Enumerable.Repeat(Tool(4000, 21), 5));
                home.Write(Header() + (withinWindow ? padding + Start("window", 20) : Start("window", 0) + padding) + recent + Usage("window", "window", 30));
                using var reader = new RolloutSpeedReader(home.Path, new() { InitialTailBytes = 4096, MaxModelHistoryBytes = 32768 });
                var result = await Settle(reader, await reader.PollAsync());
                if (withinWindow) Check.Equal("window", result.Sample!.ResponseId);
                else Check.Null(result.Sample?.TokensPerSecond);
                Check.False(result.CanCatchUp);
            }
        });
        asyncTest("reader projection: official agent message input and tool search argument bodies remain valid", async () =>
        {
            using var home = new Home(); var path = home.Write(Header() + Start("official", 0));
            using var reader = home.Reader(); await reader.PollAsync();
            File.AppendAllText(path, Line(1, "response_item", new { type = "agent_message", content = new[] { new { type = "input_text", text = new string('x', 140 * 1024) } } }) +
                Line(2, "response_item", new { type = "tool_search_call", id = "search", arguments = new { query = new string('x', 140 * 1024) } }) + Usage("official", "official", 11));
            var result = await Settle(reader, await reader.PollAsync());
            Check.Equal("official", result.Sample!.ResponseId); Check.Equal(10d, result.Sample.TokensPerSecond!.Value);
        });
        asyncTest("reader projection: body skip validates escape surrogate and UTF8 across read blocks", async () =>
        {
            foreach (var item in new[] { (Text: "\\uD83D\\uDE00", Valid: true, Boundary: 4),
                (Text: "\\uD83D\\u0041", Valid: false, Boundary: 4), (Text: "é😀", Valid: true, Boundary: 1) })
            {
                using var home = new Home(); var path = home.Write(Header() + Start("split", 0));
                using var reader = home.Reader(); await reader.PollAsync();
                var prefix = "{\"timestamp\":\"" + Epoch.AddSeconds(1).ToString("O") + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":\"";
                var padding = new string('x', 8192 - item.Boundary - Encoding.UTF8.GetByteCount(prefix));
                File.AppendAllText(path, prefix + padding + item.Text + "\"}}\n" + Usage("split", "split", 10));
                var result = await Settle(reader, await reader.PollAsync());
                if (item.Valid) Check.Equal("split", result.Sample!.ResponseId);
                else Check.Null(result.Sample?.TokensPerSecond);
            }
        });
        asyncTest("reader official schema: summary and instruction-rich context survive Reader to View", async () =>
        {
            foreach (var bytes in new[] { 4, 150 * 1024 })
            {
                using var home = new Home();
                home.Write(OfficialHeader() + OfficialStart(bytes) + OfficialUsage("official", "official", 10));
                using var reader = home.Reader(); var result = await Settle(reader, await reader.PollAsync());
                Check.Equal("gpt-6.1-sol", result.Sample!.Model);
                Check.Equal("medium", result.Sample.ReasoningEffort);
                Check.Equal(10d, result.Sample.TokensPerSecond!.Value);
                Check.Equal(ResponseRateReason.Ready, result.ResponseRateDiagnostic.Reason);
                Check.Contains("10.0", OutputSpeedView.Create(result.Sample, Epoch.AddSeconds(11)).Value);
            }
        });
        asyncTest("reader official schema: all persisted neutral root families retain exact response evidence", async () =>
        {
            var marker = "SYNTHETIC_PRIVATE_BODY_" + new string('x', 140 * 1024);
            var rows = new[]
            {
                Line(1, "compacted", new { message = marker, replacement_history = new[] { new { type = "message", role = "user", content = new[] { new { type = "input_text", text = marker } } } }, latest_token_usage_record = OfficialUsagePayload("official", "nested-not-a-record", 999999) }),
                Line(1, "world_state", new { full = true, state = new { note = marker } }),
                Line(1, "retained_context", new { type = "verified_answer", turn_id = "official", call_id = "question", questions = new[] { new { question = marker, answer = marker } }, acceptance_order = 1 }),
                Line(1, "security_risk_score", new { scores = new { synthetic = 0.1 }, call_id = "tool", action = new { text = marker } }),
                Line(1, "inter_agent_communication", new { author = "/parent", recipient = "/child", other_recipients = Array.Empty<string>(), content = marker, encrypted_content = (string?)null, trigger_turn = false }),
                Line(1, "inter_agent_communication_metadata", new { trigger_turn = false }),
                Line(1, "realtime_item", new { id = "realtime", realtime_session_id = "synthetic", type = "transcript_segment", role = "assistant", text = marker })
            };
            foreach (var row in rows)
            {
                var projection = Project(row.TrimEnd('\n')); Check.True(projection is not null, "Official root rejected: " + row.Substring(0, 100));
                Check.False(Encoding.UTF8.GetString(projection!).Contains("SYNTHETIC_PRIVATE_BODY_", StringComparison.Ordinal));
                Check.True(projection!.Length < 2048);
                using var home = new Home(); home.Write(OfficialHeader() + OfficialStart(4) + row + OfficialUsage("official", "official", 10));
                using var reader = home.Reader(); var result = await Settle(reader, await reader.PollAsync());
                Check.Equal("gpt-6.1-sol", result.Sample!.Model); Check.Equal(10d, result.Sample.TokensPerSecond!.Value);
                Check.Contains("10.0", OutputSpeedView.Create(result.Sample, Epoch.AddSeconds(11)).Value);
            }
        });
        asyncTest("reader official field matrix: persisted response and turn item bodies preserve parser semantics", async () =>
        {
            var body = "SYNTHETIC_PRIVATE_BODY_" + new string('x', 140 * 1024);
            // Official field spellings and required shape are mirrored here; this is
            // a Reader projection/parsing matrix, not a Rust deserialization test.
            var payloads = new List<(string Kind, object Payload)>
            {
                ("response_item", new { type = "reasoning", id = "reason", summary = new[] { new { type = "summary_text", text = body } }, content = (object?)null, encrypted_content = body }),
                ("response_item", new { type = "reasoning", id = "reason", summary = Array.Empty<object>(), content = new[] { new { type = "reasoning_text", text = body } }, encrypted_content = (string?)null }),
                ("response_item", new { type = "compaction", id = "compact", encrypted_content = body }),
                ("response_item", new { type = "compaction_summary", id = "compact", encrypted_content = body }),
                ("response_item", new { type = "context_compaction", id = "compact", encrypted_content = body }),
                ("response_item", new { type = "configuration_update", reasoning = new { effort = "medium" } }),
                ("response_item", new { type = "additional_tools", id = "tools", role = "developer", tools = new[] { new { description = body } } }),
                ("response_item", new { type = "message", id = "message", role = "assistant", content = new[] { new { type = "output_text", text = body } } }),
                ("response_item", new { type = "agent_message", id = "agent", author = "/parent", recipient = "/child", content = new[] { new { type = "input_text", text = body } } }),
                ("response_item", new { type = "local_shell_call", id = "shell", call_id = "tool", status = "completed", action = new { type = "exec", command = new[] { body }, timeout_ms = (long?)null, working_directory = (string?)null, env = (object?)null, user = (string?)null } }),
                ("response_item", new { type = "function_call", id = "call", name = "shell", call_id = "tool", arguments = body }),
                ("response_item", new { type = "function_call_output", id = "output", name = "shell", call_id = "tool", output = body }),
                ("response_item", new { type = "custom_tool_call", id = "call", name = "custom", call_id = "tool", input = body }),
                ("response_item", new { type = "custom_tool_call_output", id = "output", call_id = "tool", output = new[] { new { type = "input_text", text = body } } }),
                ("response_item", new { type = "tool_search_call", id = "search", call_id = "tool", execution = "client", arguments = new { query = body } }),
                ("response_item", new { type = "tool_search_output", id = "search", call_id = "tool", status = "completed", execution = "client", tools = new[] { new { description = body } } }),
                ("response_item", new { type = "web_search_call", id = "search", status = "completed", action = new { type = "search", query = body } }),
                ("response_item", new { type = "image_generation_call", id = "image", status = "completed", revised_prompt = body, result = body }),
                ("event_msg", new { type = "agent_reasoning_raw_content", text = body }),
                ("event_msg", new { type = "patch_apply_end", call_id = "patch", stdout = body, stderr = "", success = true, changes = new { synthetic = new { type = "add", content = body } }, status = "completed" })
            };
            var items = new object[]
            {
                new { type = "FunctionCallOutput", id = "output", name = "shell", output = body },
                new { type = "UserMessage", id = "user", content = new[] { new { type = "text", text = body, text_elements = Array.Empty<object>() } } },
                new { type = "Plan", id = "plan", text = body },
                new { type = "AgentMessage", id = "agent", content = new[] { new { type = "Text", text = body } } },
                new { type = "Reasoning", id = "reason", summary_text = new[] { body }, raw_content = new[] { body } },
                new { type = "HookPrompt", id = "hook", fragments = new[] { new { text = body, hookRunId = "hook" } } },
                new { type = "CommandExecution", id = "exec", command = new[] { "echo", body }, cwd = "/synthetic", parsed_cmd = Array.Empty<object>(), source = "agent", status = "completed", aggregated_output = body },
                new { type = "DynamicToolCall", id = "tool", tool = "synthetic", arguments = new { text = body }, status = "completed", content_items = new[] { new { type = "inputText", text = body } } },
                new { type = "CollabAgentToolCall", id = "collab", tool = "send_message", status = "completed", sender_thread_id = Owner, receiver_thread_ids = Array.Empty<string>(), prompt = body },
                new { type = "SubAgentActivity", id = "activity", kind = "completed", agent_thread_id = Owner, agent_path = "/child" },
                new { type = "WebSearch", id = "web", query = body, action = new { type = "search", query = body } },
                new { type = "ImageView", id = "image", path = body },
                new { type = "Extension", kind = "clock.sleep", id = "sleep", durationMs = 1 },
                new { type = "Extension", kind = "web.search", id = "web", query = body, action = new { type = "search", query = body, queries = (string[]?)null } },
                new { type = "Extension", kind = "image_gen.generation", id = "image", status = "completed", revisedPrompt = body, result = body },
                new { type = "ImageGeneration", id = "image", status = "completed", result = body },
                new { type = "EnteredReviewMode", id = "review", target = new { type = "custom", instructions = body }, user_facing_hint = body },
                new { type = "ExitedReviewMode", id = "review", review_output = new { findings = Array.Empty<object>(), overall_correctness = "patch is correct", overall_explanation = body, overall_confidence_score = 0.9 } },
                new { type = "FileChange", id = "patch", status = "completed", changes = new Dictionary<string, object> { ["/synthetic/file.txt"] = new { type = "add", content = body } } },
                new { type = "McpToolCall", id = "mcp", server = "synthetic", tool = "echo", arguments = new { text = body }, result = new { content = new[] { new { type = "text", text = body } } }, status = "completed" },
                new { type = "ContextCompaction", id = "compact" }
            };
            foreach (var item in items) payloads.Add(("event_msg", new { type = "item_completed", thread_id = Owner, turn_id = "official", item, started_at_ms = Epoch.ToUnixTimeMilliseconds(), completed_at_ms = Epoch.AddSeconds(1).ToUnixTimeMilliseconds() }));
            foreach (var (kind, payload) in payloads)
            {
                var row = Line(1, kind, payload); var projection = Project(row.TrimEnd('\n'));
                Check.True(projection is not null, "Official payload rejected: " + row.Substring(0, Math.Min(180, row.Length)));
                Check.False(Encoding.UTF8.GetString(projection!).Contains("SYNTHETIC_PRIVATE_BODY_", StringComparison.Ordinal));
                var records = OfficialHeader() + OfficialStart(4) + row + OfficialUsage("official", "official", 10);
                var baseline = new ResponseRateParser(Owner); OutputSpeedSample? expected = null;
                foreach (var line in records.Split('\n')) if (line.Length != 0) expected = baseline.Feed(line) ?? expected;
                using var home = new Home(); home.Write(records); using var reader = home.Reader();
                var result = await Settle(reader, await reader.PollAsync());
                Check.True(expected?.TokensPerSecond.HasValue == true, "Direct official parser fixture must have a valid response.");
                Check.Equal(expected!.Model, result.Sample!.Model); Check.Equal(expected.TokensPerSecond, result.Sample.TokensPerSecond);
                Check.Contains("tok/s", OutputSpeedView.Create(result.Sample, Epoch.AddSeconds(11)).Value);
            }
        });
        test("reader official event matrix: all 20 persisted kinds retain scope and discard body", () =>
        {
            // Complete required payloads from 0.162 rollout policy: 8 common and
            // 12 Legacy kinds. Terminal-event semantics remain covered by the
            // existing response/turn parser tests; acceptance is not a rate claim.
            const string matrix = """
            [
              {"name":"item_completed","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"item_completed","thread_id":"77777777-7777-4777-8777-777777777777","turn_id":"synthetic-official-turn","item":{"type":"UserMessage","id":"synthetic-user-item","client_id":"synthetic-client","content":[{"type":"text","text":"synthetic input","text_elements":[]}]},"started_at_ms":1791878400000,"completed_at_ms":1791878401000}}},
              {"name":"token_count","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":200,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":100,"reasoning_output_tokens":80,"total_tokens":300},"last_token_usage":{"input_tokens":200,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":100,"reasoning_output_tokens":80,"total_tokens":300},"model_context_window":200000},"rate_limits":null}}},
              {"name":"thread_goal_updated","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"thread_goal_updated","threadId":"77777777-7777-4777-8777-777777777777","turnId":"synthetic-official-turn","goal":{"threadId":"77777777-7777-4777-8777-777777777777","objective":"Synthetic schema verification","status":"active","tokenBudget":1000,"tokensUsed":100,"timeUsedSeconds":1,"createdAt":1791878400,"updatedAt":1791878401}}}},
              {"name":"thread_rolled_back","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"thread_rolled_back","num_turns":1}}},
              {"name":"turn_aborted","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"turn_aborted","root_turn_id":"synthetic-official-turn","turn_id":"synthetic-official-turn","reason":"interrupted","error":null,"started_at":1791878400,"completed_at":1791878401,"duration_ms":1000}}},
              {"name":"task_started","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"task_started","turn_attribution":null,"turn_id":"synthetic-official-turn","root_turn_id":"synthetic-official-turn","trace_id":"synthetic-trace","started_at":1791878400,"model_context_window":200000,"collaboration_mode_kind":"default"}}},
              {"name":"task_complete","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"task_complete","root_turn_id":"synthetic-official-turn","turn_id":"synthetic-official-turn","last_agent_message":"synthetic completed","error":null,"started_at":1791878400,"completed_at":1791878401,"duration_ms":1000,"time_to_first_token_ms":100}}},
              {"name":"thread_settings_applied","history_mode":"common","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_id":"77777777-7777-4777-8777-777777777777","thread_settings":{"model":"synthetic-model","model_provider_id":"synthetic-provider","service_tier":"fast","approval_policy":"never","approvals_reviewer":"user","permission_profile":{"type":"disabled"},"active_permission_profile":{"id":":danger-full-access"},"cwd":"/synthetic","runtime_workspace_roots":["/synthetic"],"reasoning_effort":"medium","reasoning_summary":"none","personality":"pragmatic","collaboration_mode":{"mode":"default","settings":{"model":"synthetic-model","reasoning_effort":"medium","developer_instructions":"synthetic developer instructions"}},"disabled_plugin_ids":[]}}}},
              {"name":"user_message","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"user_message","client_id":"synthetic-client","message":"synthetic user input","images":[],"image_details":[],"file_ids":[],"file_id_details":[],"image_order":[],"local_images":[],"local_image_details":[],"audio":[],"local_audio":[],"text_elements":[]}}},
              {"name":"agent_message","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"agent_message","message":"synthetic agent answer","phase":"final_answer","memory_citation":null,"delivery":null,"questions":[]}}},
              {"name":"agent_reasoning","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"agent_reasoning","text":"synthetic reasoning summary"}}},
              {"name":"agent_reasoning_raw_content","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"agent_reasoning_raw_content","text":"synthetic fixture body"}}},
              {"name":"entered_review_mode","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"entered_review_mode","target":{"type":"custom","instructions":"synthetic review request"},"user_facing_hint":"synthetic hint","turn_id":"synthetic-official-turn","item_id":"synthetic-review"}}},
              {"name":"exited_review_mode","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"exited_review_mode","turn_id":"synthetic-official-turn","item_id":"synthetic-review","review_output":{"findings":[{"title":"synthetic finding","body":"synthetic body","confidence_score":0.9,"priority":2,"code_location":{"absolute_file_path":"/synthetic/file.txt","line_range":{"start":1,"end":1}}}],"overall_correctness":"synthetic","overall_explanation":"synthetic explanation","overall_confidence_score":0.9}}}},
              {"name":"patch_apply_end","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"patch_apply_end","call_id":"synthetic-call","turn_id":"synthetic-official-turn","stdout":"synthetic patch output","stderr":"","success":true,"changes":{"/synthetic/file.txt":{"type":"add","content":"synthetic content\n"}},"status":"completed"}}},
              {"name":"context_compacted","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"context_compacted"}}},
              {"name":"mcp_tool_call_end","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"mcp_tool_call_end","call_id":"synthetic-call","turn_id":"synthetic-official-turn","invocation":{"server":"synthetic-server","tool":"synthetic-tool","arguments":{"query":"synthetic"}},"connector_id":"synthetic-connector","mcp_app_resource_uri":null,"mcp_app_ui":null,"link_id":null,"app_name":"synthetic app","action_name":"synthetic action","plugin_id":"synthetic-plugin","read_only_hint":true,"duration":{"secs":1,"nanos":0},"result":{"Ok":{"content":[{"type":"text","text":"synthetic result"}],"structuredContent":{"summary":"synthetic summary"},"isError":false,"_meta":{}}}}}},
              {"name":"web_search_end","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"web_search_end","call_id":"synthetic-call","query":"synthetic query","action":{"type":"search","query":"synthetic query","queries":["synthetic query"]},"results":[{"title":"Synthetic result","url":"https://example.invalid/synthetic"}]}}},
              {"name":"image_generation_end","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"image_generation_end","call_id":"synthetic-call","status":"completed","revised_prompt":"synthetic prompt","result":"c3ludGhldGlj","transparent_background":false,"failure":null,"saved_path":"/synthetic/image.png"}}},
              {"name":"sub_agent_activity","history_mode":"legacy","record":{"timestamp":"2026-10-13T08:00:01.000Z","type":"event_msg","payload":{"type":"sub_agent_activity","event_id":"synthetic-event","occurred_at_ms":1791878401000,"agent_thread_id":"88888888-8888-4888-8888-888888888888","agent_path":"/root/synthetic_agent","kind":"started"}}}
            ]
            """;
            using var matrixDocument = JsonDocument.Parse(matrix);
            Check.Equal(20, matrixDocument.RootElement.GetArrayLength());
            foreach (var row in matrixDocument.RootElement.EnumerateArray())
            {
                var record = row.GetProperty("record"); var projected = Project(record.GetRawText());
                Check.True(projected is not null, "Persisted event rejected: " + row.GetProperty("name").GetString());
                using var projectedDocument = JsonDocument.Parse(projected!);
                var output = projectedDocument.RootElement;
                Check.Equal(record.GetProperty("type").GetString(), output.GetProperty("type").GetString());
                Check.Equal(record.GetProperty("timestamp").GetString(), output.GetProperty("timestamp").GetString());
                var source = record.GetProperty("payload"); var target = output.GetProperty("payload");
                foreach (var field in new[] { "type", "thread_id", "turn_id", "duration_ms", "status" })
                    if (source.TryGetProperty(field, out var value)) Check.Equal(value.GetRawText(), target.GetProperty(field).GetRawText());
                if (source.TryGetProperty("item", out var item))
                    Check.Equal(item.GetProperty("type").GetString(), target.GetProperty("item").GetProperty("type").GetString());
                var retainedStrings = JsonStrings(output).ToArray();
                foreach (var body in JsonBodyStrings(record)) Check.False(retainedStrings.Contains(body, StringComparer.Ordinal), "Event body survived projection.");
            }
        });
        asyncTest("reader catchup: cancelled poll leaves progress resumable", async () =>
        {
            using var home = new Home(); home.Write(Header() + Start("cold", 0) + string.Concat(Enumerable.Repeat(Tool(8000, 1), 90)) + Usage("cold", "cold", 10));
            using var reader = home.Reader(); var first = await reader.PollAsync();
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(reader.PollAsync(cancellation.Token));
            Check.Equal("cold", (await Settle(reader, first)).Sample!.ResponseId);
        });
    }
    private static IEnumerable<string> JsonStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var text in JsonStrings(item)) yield return text;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) foreach (var text in JsonStrings(property.Value)) yield return text;
    }
    private static IEnumerable<string> JsonBodyStrings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is "message" or "text" or "content" or "last_agent_message" or "instructions" or
                    "developer_instructions" or "stdout" or "stderr" or "query" or "revised_prompt" or "user_facing_hint" or
                    "objective" or "overall_explanation" or "body")
                    foreach (var text in JsonStrings(property.Value).Where(text => text.Length > 0)) yield return text;
                else foreach (var text in JsonBodyStrings(property.Value)) yield return text;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var text in JsonBodyStrings(item)) yield return text;
    }
    private static object NewProjection() => Activator.CreateInstance(typeof(RolloutSpeedReader).Assembly.GetType("CodexQuota.Core.RolloutMetadataProjection")!, 128 * 1024)!;
    private static void FeedProjection(object projection, byte[] data)
    {
        var method = projection.GetType().GetMethod("Add")!;
        foreach (var value in data) method.Invoke(projection, new object[] { value });
    }
    private static byte[]? FinishProjection(object projection) => (byte[]?)projection.GetType().GetMethod("Finish")!.Invoke(projection, null);
    private static byte[]? Project(string text)
    {
        var projection = NewProjection(); FeedProjection(projection, Encoding.UTF8.GetBytes(text)); return FinishProjection(projection);
    }
    private static void Budget(RolloutSpeedReadResult result) => Check.True(result.BytesRead <= 2 * 1024 * 1024 && result.BytesRead >= 0);
    private static async Task<RolloutSpeedReadResult> Settle(RolloutSpeedReader reader, RolloutSpeedReadResult result)
    {
        for (var i = 0; i < 100 && result.CanCatchUp; i++) { Budget(result); result = await reader.PollAsync(); }
        Budget(result); Check.False(result.CanCatchUp, "Finite fixtures must settle."); return result;
    }
    private static string Line(int seconds, string type, object payload) => JsonSerializer.Serialize(new { timestamp = Epoch.AddSeconds(seconds).ToString("O"), type, payload }) + "\n";
    private static string Header(string owner = Owner) => Line(0, "session_meta", new { id = owner });
    private static string Start(string turn, int seconds) => Line(seconds, "event_msg", new { type = "task_started", thread_id = Owner, turn_id = turn }) +
        Line(seconds, "turn_context", new { thread_id = Owner, turn_id = turn, model = "gpt-6.1-sol", effort = "medium" });
    private static string OfficialHeader() => Line(0, "session_meta", new { id = Owner, session_id = Owner, timestamp = Epoch.ToString("O"), cwd = "/synthetic", originator = "codex_cli_rs", cli_version = "0.159.0", source = "cli", model_provider = "openai", base_instructions = new { text = "Synthetic private instructions" } });
    // Includes every TurnContextItem member from the official 0.162 serialization
    // fixture (protocol.rs turn_context_item_serializes_network_when_present), plus
    // scoped model/effort and a realistic large collaboration instruction body.
    private static string OfficialStart(int bytes) => Line(0, "event_msg", new { type = "task_started", turn_id = "official", model_context_window = (long?)null, collaboration_mode_kind = "default" }) +
        Line(0, "turn_context", new
        {
            turn_id = "official", root_turn_id = (string?)null, disabled_plugin_ids = (string[]?)null,
            cwd = "/synthetic", workspace_roots = new[] { "/synthetic" }, current_date = "2026-10-02", timezone = "Etc/UTC",
            approval_policy = "never", approvals_reviewer = (string?)null,
            sandbox_policy = new { type = "danger-full-access" }, permission_profile = (object?)null, active_permission_profile = (object?)null,
            network = new { allowed_domains = new[] { "api.example.com" }, denied_domains = new[] { "blocked.example.com" } },
            file_system_sandbox_policy = new { kind = "restricted", entries = new[] { new { path = new { type = "glob_pattern", pattern = "/synthetic/private/**/*.txt" }, access = "deny" } } },
            model = "gpt-6.1-sol", comp_hash = (string?)null, personality = (string?)null,
            collaboration_mode = new { mode = "default", settings = new { model = "gpt-6.1-sol", reasoning_effort = "medium", developer_instructions = new string('x', bytes) } },
            multi_agent_version = (string?)null, multi_agent_mode = (string?)null, realtime_active = (bool?)null,
            cyber_access_program = (object?)null, effort = "medium", summary = "auto"
        });
    private static object OfficialCounts(long output) => new { input_tokens = 100, cached_input_tokens = 20, cache_write_input_tokens = 0, output_tokens = output, reasoning_output_tokens = Math.Min(output, 50), total_tokens = output + 100 };
    private static object OfficialUsagePayload(string turn, string response, long output) => new
    {
        thread_id = Owner, turn_id = turn, session_id = Owner, root_turn_id = turn, response_id = response,
        usage = OfficialCounts(output), turn_token_usage = OfficialCounts(output), thread_token_usage = OfficialCounts(output)
    };
    private static string OfficialUsage(string turn, string response, int seconds) => Line(seconds, "token_usage_record", OfficialUsagePayload(turn, response, 100));
    private static string Lifecycle(string id, string turn, int seconds) => Line(seconds, "response_item", new { type = "message", role = "assistant", id }) +
        Line(seconds, "event_msg", new { type = "item_completed", thread_id = Owner, turn_id = turn, item = new { type = "AgentMessage", id },
            started_at_ms = Epoch.AddSeconds(seconds - 1).ToUnixTimeMilliseconds(), completed_at_ms = Epoch.AddSeconds(seconds).ToUnixTimeMilliseconds() });
    private static string Usage(string turn, string response, int seconds) => Line(seconds, "token_usage_record", new { thread_id = Owner, turn_id = turn, response_id = response, usage = new { output_tokens = 100 }, turn_token_usage = new { output_tokens = 100 } });
    private static string Complete(string turn, int seconds) => Line(seconds, "event_msg", new { type = "task_complete", thread_id = Owner, turn_id = turn, duration_ms = 10000 });
    private static string Turn(string turn, int seconds) => Start(turn, seconds) + Usage(turn, turn, seconds + 10) + Complete(turn, seconds + 11);
    private static string Tool(int count, int seconds) => Line(seconds, "response_item", new { type = "function_call_output", call_id = "tool", output = new string('x', count) });
    private sealed class Home : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reader-catchup-" + Guid.NewGuid().ToString("N"));
        public Home() => Directory.CreateDirectory(System.IO.Path.Combine(Path, "sessions"));
        public string Write(string content, string owner = Owner, string subdirectory = "", bool archive = false)
        {
            var directory = System.IO.Path.Combine(Path, archive ? "archived_sessions" : "sessions", subdirectory); Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "rollout-2026-10-02-" + owner + ".jsonl"); File.WriteAllText(path, content, new UTF8Encoding(false)); return path;
        }
        public RolloutSpeedReader Reader() => new(Path, new() { DiscoveryInterval = TimeSpan.Zero });
        public void Dispose() => Directory.Delete(Path, true);
    }
}
