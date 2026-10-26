using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Security.Principal;
using System.Security.Cryptography;
using System.IO.Compression;
using CodexQuota.Tray;
using CodexQuota.Core;

namespace CodexQuota.FrameworkSmoke;

internal static class Bootstrap
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            var name = new AssemblyName(request.Name).Name;
            using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("Embedded." + name + ".dll");
            if (input is null) return null;
            using var bytes = new MemoryStream(); input.CopyTo(bytes);
            return Assembly.Load(bytes.ToArray());
        };
        return Run(args);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "app-server") return Tests.FakeServer();
        if (args.Length == 1 && args[0] == "--read") return Tests.FakeQuotaReader();
        if (args.Length == 1 && args[0] == "--fake-descendant") { Thread.Sleep(30000); return 0; }
        return Tests.RunAsync().GetAwaiter().GetResult();
    }
}

internal static class Tests
{
    public static int FakeQuotaReader()
    {
        // Synthetic child only: no credential source, network, installed CLI or registry access.
        Console.WriteLine("{\"protocolVersion\":1,\"helperVersion\":\"0.2.16\",\"status\":\"Ready\",\"stage\":\"RateLimitsRead\",\"code\":null,\"errorKind\":null,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"planType\":\"plus\",\"primary\":{\"usedPercent\":21,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":34,\"windowDurationMins\":10080}},\"rateLimitResetCredits\":{\"availableCount\":0,\"credits\":[]}}}");
        return 0;
    }
    private static int _passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException("FAIL " + name);
        _passed++; Console.WriteLine("PASS " + name);
    }
    public static async Task<int> RunAsync()
    {
        try
        {
            Console.WriteLine("CLR: " + Environment.Version);
            TrayVisibilityAbiSmoke.Run(Check);
            // Synthetic bytes only; verifies the actual embedded Desktop hash factory on Windows.
            // Cloud compiles this test but does not claim to execute Windows CNG.
            var hashFactoryType = typeof(QuotaState).Assembly.GetType("CodexQuota.Core.DesktopImageHasher", throwOnError: true)!;
            var createHash = hashFactoryType.GetMethod("CreateHash", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidDataException("Desktop hash factory unavailable.");
            using (var desktopHash = (HashAlgorithm)createHash.Invoke(null, null)!)
            {
                Check(desktopHash is SHA256Cng, "Desktop production factory selects Windows SHA256Cng");
                Check(BitConverter.ToString(desktopHash.ComputeHash(Array.Empty<byte>())).Replace("-", "").ToLowerInvariant() ==
                    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "Desktop CNG empty SHA256 vector");
                Check(BitConverter.ToString(desktopHash.ComputeHash(System.Text.Encoding.ASCII.GetBytes("abc"))).Replace("-", "").ToLowerInvariant() ==
                    "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Desktop CNG abc SHA256 vector");
            }
            // Exercise the exact production Windows identity boundary. Construction only:
            // no Enter, no StartServer, no mutex acquisition, no tray, registry or real CLI.
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var logon = LogonSessionIdentity.Read(identity);
                Check(logon.StartsWith("S-1-5-5-", StringComparison.Ordinal), "native TokenLogonSid reads current logon identity");
                Check(LogonSessionIdentity.Read(identity) == logon, "native logon identity read is stable");
                var constructor = typeof(SingleInstanceCoordinator).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, types: new[] { typeof(HandoffStartupStage).MakeByRefType() }, modifiers: null)
                    ?? throw new InvalidDataException("Production constructor unavailable.");
                var arguments = new object[] { HandoffStartupStage.Identity };
                using var coordinator = (SingleInstanceCoordinator?)constructor.Invoke(arguments);
                Check(coordinator is not null, "exact production single-instance constructor succeeds without starting app");
                var initialized = typeof(SingleInstanceCoordinator).GetField("_logonSid", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(coordinator) as string;
                Check(initialized == logon, "production constructor uses the actual native logon identity");
                // SID/token values are deliberately never printed or written to disk.
            }
            Console.WriteLine("Core loaded from embedded resource: " + string.IsNullOrEmpty(typeof(QuotaState).Assembly.Location));
            Check(string.IsNullOrEmpty(typeof(QuotaState).Assembly.Location), "Core has no external DLL dependency");
            Check(string.IsNullOrEmpty(typeof(JsonDocument).Assembly.Location), "JSON dependency is embedded");
            var helperTestRoot = Path.Combine(Path.GetTempPath(), "CodexQuotaReaderSmoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helperTestRoot);
            try
            {
                var syntheticImage = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
                using var sha = SHA256.Create();
                var hash = BitConverter.ToString(sha.ComputeHash(syntheticImage)).Replace("-", "").ToLowerInvariant();
                using var compressed = new MemoryStream();
                using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
                    gzip.Write(syntheticImage, 0, syntheticImage.Length);
                var payload = compressed.ToArray();
                var client = new ReadOnlyQuotaClient(() => HelperExecutableLease.Extract(helperTestRoot,
                    () => new MemoryStream(payload, writable: false), hash, syntheticImage.Length, payload.Length));
                var read = await client.ReadAsync(CancellationToken.None);
                Check(read.Status == ReadStatus.Ready && read.Quota?.RemainingPercent == 79 &&
                    read.Selection?.AvailableWindows.Count == 2,
                    "exact native reader extraction, image lease, job launch, mitigation and exit wait with synthetic child");
            }
            finally { Directory.Delete(helperTestRoot, recursive: true); }
            var speedParser = new RolloutSpeedParser("smoke-thread");
            speedParser.Feed("{\"timestamp\":\"2026-01-01T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"smoke-thread\"}}");
            speedParser.Feed("{\"timestamp\":\"2026-01-01T00:00:00Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"smoke-turn\",\"model\":\"synthetic-model\"}}");
            speedParser.Feed("{\"timestamp\":\"2026-01-01T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"thread_id\":\"smoke-thread\",\"turn_id\":\"smoke-turn\",\"item\":{\"id\":\"smoke-item\",\"type\":\"Reasoning\"},\"started_at_ms\":1767225601000,\"completed_at_ms\":1767225602000}}");
            speedParser.Feed("{\"timestamp\":\"2026-01-01T00:00:02.010Z\",\"type\":\"response_item\",\"payload\":{\"id\":\"smoke-item\",\"type\":\"reasoning\"}}");
            var speed = speedParser.Feed("{\"timestamp\":\"2026-01-01T00:00:02.020Z\",\"type\":\"token_usage_record\",\"payload\":{\"thread_id\":\"smoke-thread\",\"turn_id\":\"smoke-turn\",\"response_id\":\"smoke-response\",\"usage\":{\"output_tokens\":120,\"reasoning_output_tokens\":80}}}");
            Check(speed is not null && speed.TokensPerSecond is null && speed.OutputTokens == 120,
                "Framework parser retains reasoning-inclusive usage without inventing TPS from lifecycle timing");
            Check(speed!.CompletedAt == DateTimeOffset.Parse("2026-01-01T00:00:02Z"),
                "TPS completion excludes later usage timestamp");
            using var doc = JsonDocument.Parse("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"windowDurationMins\":10080,\"usedPercent\":32,\"resetsAt\":1800000000}}}}");
            Check(QuotaParser.ReadWeekly(doc.RootElement)?.DisplayPercent == 68, "weekly primary window parser");
            using var planDoc = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"windowDurationMins\":300,\"usedPercent\":32},\"secondary\":{\"windowDurationMins\":10080,\"usedPercent\":13}}}");
            foreach (var plan in new[] { "plus", "prolite", "pro", "promax" })
            {
                var selected = QuotaParser.ReadSelection(planDoc.RootElement, plan);
                var state = QuotaState.Initial.Apply(new(ReadStatus.Ready, selected.Active, selected), DateTimeOffset.UtcNow);
                var text = state.Tooltip(DateTimeOffset.UtcNow);
                Check(selected.Active?.WindowMinutes == (plan == "plus" ? 300 : 10080) &&
                    text.Contains("68%") && text.Contains("87%") &&
                    !text.Contains("更新") && !text.Contains("Codex") && !text.Contains("Pro") && !text.Contains("Plus") &&
                    text.Split('\n').Length == 4 && text.Length <= 127,
                    "plan selection and minimal tooltip visibility " + plan);
            }
            using (var cardsDoc = JsonDocument.Parse("{\"rateLimitResetCredits\":{\"availableCount\":3,\"credits\":[{\"id\":\"a\",\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1800000000},{\"id\":\"b\",\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":null}]}}"))
            {
                var cards = ResetCards.Parse(cardsDoc.RootElement);
                var lines = cards.TooltipLines(DateTimeOffset.FromUnixTimeSeconds(1700000000));
                Check(cards.Cards.Count == 2 && lines.Count == 3 && lines[1] == "重置卡：不过期" && lines[2] == "另1张日期未返回",
                    "reset-card details, no-expiry and missing details stay distinct");
                var noSummary = ResetCards.Parse(planDoc.RootElement);
                Check(noSummary.Status == ResetCardDataStatus.NotReturned && noSummary.TooltipLines(DateTimeOffset.UtcNow).Single() == "重置卡：未返回",
                    "older CLI missing reset-card field is not zero cards");
            }
            // Exact Windows GDI text path, offscreen only: no desktop input or actual account.
            var paintNow = DateTimeOffset.UtcNow;
            var paintWeek = new QuotaWindow(87, paintNow.AddDays(3), 10080);
            var paintFive = new QuotaWindow(100, paintNow.AddHours(3), 300);
            foreach (var dual in new[] { false, true })
            foreach (var darkMode in new[] { false, true })
            foreach (var dpi in new[] { 96, 120, 144, 192 })
            {
                var selection = new QuotaSelection(dual ? "plus" : "pro", PlanSource.RateLimits, dual ? 300 : 10080,
                    dual ? paintFive : paintWeek, dual ? new[] { paintFive, paintWeek } : new[] { paintWeek }, QuotaSelectionReason.Preferred);
                var sample = new QuotaState(selection.Active, paintNow, ReadStatus.Ready, selection,
                    new ResetCards(ResetCardDataStatus.Details, 3, Enumerable.Repeat(new ResetCard(ResetCardExpiryKind.Never, null), 3).ToArray()), paintNow);
                var model = HoverCardModel.Create(sample, paintNow, OutputSpeedView.Create(speed, paintNow));
                var scale = HoverCardLayout.Scale(dpi, 4000);
                var pixels = new Size(HoverCardLayout.PixelWidth(scale), HoverCardLayout.PixelHeight(model, scale, 3000));
                using var rendered = HoverCardRenderer.Render(model, pixels, scale, 0, textPainter: new NativeHoverTextPainter(), darkMode: darkMode);
                Check(rendered.Width == 300 * dpi / 96, "native text card pixel width once per DPI " + dpi + "/" + dual);
                foreach (var expiryText in new[] { "10月23日 14:30:59", "2028年1月01日 07:59:59", "2027年12月23日 22:30:59" })
                {
                    using var graphics = Graphics.FromImage(rendered);
                    using var expiryFont = new Font("Microsoft YaHei UI", (float)Math.Round(HoverCardLayout.TicketTextSize(expiryText) * scale), FontStyle.Regular, GraphicsUnit.Pixel);
                    var measured = new NativeHoverTextPainter().Measure(graphics, expiryText, expiryFont);
                    Check(measured.Width <= (int)Math.Round((HoverCardLayout.Width - 2 * HoverCardLayout.Padding - 12) * scale),
                        "exact expiry seconds fit native row " + dpi + "/" + dual + "/" + expiryText.Length);
                }
                var allOpaque = true;
                for (var y = (int)(32 * scale); y < rendered.Height - 32 * scale; y += 7)
                for (var x = (int)(32 * scale); x < rendered.Width - 32 * scale; x += 7)
                    if (rendered.GetPixel(x, y).A != 255) allOpaque = false;
                Check(allOpaque, "native GDI text has no alpha holes " + dpi + "/" + dual);
                var ink = false;
                var targetInk = HoverCardRenderer.ToneColor(model.Quotas[0].Tone, darkMode: darkMode);
                for (var y = (int)(80 * scale); y < 112 * scale; y += 2)
                for (var x = (int)(30 * scale); x < 112 * scale; x += 2)
                {
                    var pixel = rendered.GetPixel(x, y);
                    if (Math.Abs(pixel.R - targetInk.R) < 20 && Math.Abs(pixel.G - targetInk.G) < 20 && Math.Abs(pixel.B - targetInk.B) < 20) ink = true;
                }
                Check(ink, "native GDI quota digits are rendered " + dpi + "/" + dual);
            }
            foreach (int size in new[] { 16, 20, 24, 32 })
            {
                using var png = new MemoryStream(TrayIconRasterizer.EncodePng(TrayIconRasterizer.Render(size, 68)));
                using var bitmap = Image.FromStream(png);
                Check(bitmap.Width == size && bitmap.Height == size, "Framework PNG/zlib decoder " + size);
            }
            using (var bytes = new MemoryStream(TrayIconRasterizer.EncodeIco(100)))
            using (var icon = new Icon(bytes, 16, 16)) Check(icon.Handle != IntPtr.Zero, "Framework ICO decoder");
            using (var bytes = new MemoryStream(TrayIconRasterizer.EncodeIcoFrame(TrayIconRasterizer.RenderFrame(32, 97, 96.5, sweepPhase: 0.5))))
            using (var icon = new Icon(bytes, 32, 32)) Check(icon.Width == 32 && icon.Height == 32, "native-size antialiased animation ICO");
            var animation = new TrayAnimation();
            animation.Start(ReadStatus.Ready, 98, 97, false, 0, true);
            Check(animation.Sample(97, false, 100).IsActive && !animation.Sample(97, false, 420).IsActive, "animation always returns to idle");
            var hover = new HoverRefreshGate();
            Check(hover.Enter() && !hover.Enter(), "one refresh per hover entry");
            hover.Leave(); Check(hover.Enter(), "hover reentry rearms");
            var now = DateTimeOffset.UtcNow;
            var refresh = new RefreshPolicy();
            Check(refresh.TryBegin(now, RefreshTrigger.Manual) && !refresh.TryBegin(now, RefreshTrigger.Hover), "in-flight refresh coalescing");
            refresh.Complete(ReadStatus.RateLimited, now);
            Check(!refresh.TryBegin(now.AddSeconds(299), RefreshTrigger.Manual), "rate limit cooldown");
            var executable = Assembly.GetExecutingAssembly().Location;
            var savedMode = Environment.GetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MODE");
            var savedMarker = Environment.GetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MARKER");
            var marker = Path.Combine(Path.GetTempPath(), "cqt-framework-smoke-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                Environment.SetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MARKER", marker);
                Environment.SetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MODE", "success");
                var result = await new CodexQuotaClient(executable).ReadAsync(CancellationToken.None);
                Check(result.Status == ReadStatus.Ready && result.Quota?.DisplayPercent == 68, "native job/pipe protocol with fake server");
                await CheckExited(marker, "successful child cleanup");
                File.Delete(marker);
                Environment.SetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MODE", "hang");
                using var cancel = new CancellationTokenSource();
                var pending = new CodexQuotaClient(executable).ReadAsync(cancel.Token);
                var startupWait = Stopwatch.StartNew();
                while (!File.Exists(marker) && startupWait.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
                var elapsed = Stopwatch.StartNew();
                cancel.Cancel();
                var wasCancelled = false;
                try { await pending; }
                catch (OperationCanceledException) { wasCancelled = true; }
                Check(wasCancelled, "Framework pipe cancellation");
                Check(elapsed.Elapsed < TimeSpan.FromSeconds(8), "cancellation is bounded");
                await CheckExited(marker, "cancelled child cleanup");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MODE", savedMode);
                Environment.SetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MARKER", savedMarker);
                if (File.Exists(marker)) File.Delete(marker);
            }
            var invalidExe = Path.Combine(Path.GetTempPath(), "cqt-invalid-image-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllText(invalidExe, "Synthetic invalid executable for startup diagnostics.");
                var diagnostics = new List<CliDiagnostic>();
                var failed = await new CodexQuotaClient(invalidExe, diagnostics.Add).ReadAsync(CancellationToken.None);
                Check(failed.Status == ReadStatus.Unavailable && diagnostics.Count == 1, "native invalid-image failure does not retry");
                Check(diagnostics.All(item => item.Stage == CliStage.Start && item.StartOperation == CliStartOperation.CreateProcess &&
                    item.Code == 193 && item.CandidateIndex == 1), "native CreateProcess error193 keeps exact substage/code");
                Check(diagnostics[0].AttemptIndex == 1, "native single attempt retains candidate identity");
                Check(diagnostics.All(item => !item.ToSafeLogCode().Contains(invalidExe)), "native diagnostic does not expose temporary path");
            }
            finally { if (File.Exists(invalidExe)) File.Delete(invalidExe); }
            Console.WriteLine(_passed + " Framework smoke checks passed. No real Codex account or network was used.");
            return 0;
        }
        catch (Exception failure) { Console.Error.WriteLine(failure); return 1; }
    }
    private static async Task CheckExited(string marker, string name)
    {
        Check(File.Exists(marker), "fake child started");
        foreach (var value in File.ReadAllLines(marker))
        {
            var pid = int.Parse(value);
            var exited = false;
            for (var i = 0; i < 40; i++)
            {
                try { using var process = Process.GetProcessById(pid); if (process.HasExited) { exited = true; break; } }
                catch (ArgumentException) { exited = true; break; }
                await Task.Delay(50);
            }
            Check(exited, name + " PID " + pid);
        }
    }
    public static int FakeServer()
    {
        var marker = Environment.GetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MARKER");
        if (marker is null) return 2;
        using var descendant = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--fake-descendant")
        { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Cannot start fake descendant.");
        File.WriteAllText(marker, Process.GetCurrentProcess().Id + Environment.NewLine + descendant.Id);
        if (Environment.GetEnvironmentVariable("CQT_FRAMEWORK_FAKE_MODE") == "hang")
        { Thread.Sleep(30000); return 0; }
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (!message.TryGetProperty("id", out var id)) continue;
            var method = message.GetProperty("method").GetString();
            if (method is not ("initialize" or "account/rateLimits/read")) return 65;
            string result = method == "account/rateLimits/read"
                ? "{\"rateLimits\":{\"primary\":{\"windowDurationMins\":10080,\"usedPercent\":32}}}" : "{}";
            Console.WriteLine("{\"id\":" + id.GetInt64() + ",\"result\":" + result + "}");
            Console.Out.Flush();
            if (method == "account/rateLimits/read") return 0; // Root exits before its descendant: job still owns both.
        }
        return 0;
    }
}
