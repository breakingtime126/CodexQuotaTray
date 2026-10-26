using CodexQuota.Core;

internal static class ResetNotificationUiTests
{
    public static void Register(Action<string, Action> test)
    {
        test("reset UI: hidden optional data adds no layout space", () =>
        {
            var model = HoverCardModel.Create(QuotaState.Initial, DateTimeOffset.UtcNow);
            var height = HoverCardLayout.ContentHeight(model);
            Check.Equal(height, HoverCardLayout.ContentHeight(model with { ResetFeed = ResetFeedPresentation.Hidden }));
            var row = new ResetFeedRow("上次重置", "30分钟前", null, "latest", "公告时间，不是个人重置");
            var latest = model with { ResetFeed = new(true, row, null, null, null) };
            Check.Equal(height + HoverCardLayout.ResetFeedRowHeight, HoverCardLayout.ContentHeight(latest));
            var both = model with { ResetFeed = new(true, row, row with { Label = "预计重置" }, "forecast", null) };
            Check.Equal(height + 2 * HoverCardLayout.ResetFeedRowHeight, HoverCardLayout.ContentHeight(both));
        });
        test("reset UI: link hit targets honor DPI scroll clipping and scrollbar", () =>
        {
            var link = new HoverLinkRegion("forecast", "https://x.com/thsottiaux/status/123", "预测",
                180, 200, 92, 24, true);
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                Check.Equal(link, HoverLinkRegion.HitTest(new[] { link }, 200 * scale, 160 * scale,
                    scale, 50, 300 * scale, 300 * scale, false));
                Check.Null(HoverLinkRegion.HitTest(new[] { link }, 150 * scale, 160 * scale,
                    scale, 50, 300 * scale, 300 * scale, false));
                Check.Null(HoverLinkRegion.HitTest(new[] { link }, 200 * scale, 224 * scale,
                    scale, 0, 300 * scale, 300 * scale, false));
                Check.Null(HoverLinkRegion.HitTest(new[] { link }, 200 * scale, 5 * scale,
                    scale, 200, 300 * scale, 300 * scale, false));
                Check.Null(HoverLinkRegion.HitTest(new[] { link }, 271 * scale, 210 * scale,
                    scale, 0, 300 * scale, 300 * scale, true));
            }
        });
        test("reset UI: event acknowledgment survives polls restarts and temporary failures", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "cqt-reset-ack-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "state.json");
            try
            {
                var state = new ResetNotificationState(path);
                Check.False(state.IsUnread(null));
                Check.True(state.IsUnread("forecast-1"));
                state.Acknowledge("forecast-1");
                Check.False(state.IsUnread("forecast-1"));
                Check.True(state.IsUnread("forecast-2"));
                state.Acknowledge("forecast-2");
                Check.False(state.IsUnread("forecast-1"));
                var restarted = new ResetNotificationState(path);
                Check.False(restarted.IsUnread("forecast-1"));
                Check.False(restarted.IsUnread("forecast-2"));
                Check.True(restarted.IsUnread("forecast-3"));
                var text = File.ReadAllText(path);
                Check.False(text.Contains("forecast"));
                for (var i = 0; i < 100; i++) restarted.Acknowledge("bounded-" + i);
                Check.True(new FileInfo(path).Length < 4096);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        test("reset UI: acknowledgment save failure remains session-only and silent", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "cqt-reset-denied-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, "not a directory");
            try
            {
                var state = new ResetNotificationState(Path.Combine(path, "state.json"));
                state.Acknowledge("forecast");
                Check.False(state.IsUnread("forecast"));
            }
            finally { File.Delete(path); }
        });
        test("reset UI: static corner badge keeps percent digits and every other icon pixel", () =>
        {
            foreach (var size in new[] { 16, 20, 24, 32, 48, 64 })
            foreach (var percent in new int?[] { null, 0, 9, 30, 88, 100 })
            foreach (var stale in new[] { false, true })
            {
                var plain = TrayIconRasterizer.RenderFrame(size, percent, percent, stale);
                var badge = TrayIconRasterizer.RenderFrame(size, percent, percent, stale, notification: true);
                var changed = 0;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var i = (y * size + x) * 4;
                    var same = Enumerable.Range(0, 4).All(c => plain.Pixels[i + c] == badge.Pixels[i + c]);
                    if (same) continue;
                    changed++;
                    Check.True(x >= size * .69 && y < size * .32, "Badge changed non-corner pixels");
                    Check.False(plain.Pixels[i] > 230 && plain.Pixels[i + 1] > 230 && plain.Pixels[i + 2] > 230,
                        "Badge changed white quota digit");
                }
                Check.True(changed > 0);
            }
        });
    }
}
