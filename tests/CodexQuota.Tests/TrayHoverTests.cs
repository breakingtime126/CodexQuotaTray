using System.Drawing;
using CodexQuota.Tray;

internal static class TrayHoverTests
{
    // These exercise portable presentation/lifetime rules. Real Shell input,
    // nonactivation, scrolling, and DPI remain Windows acceptance checks.
    public static void Register(Action<string, Action> test)
    {
        foreach (var length in new[] { 0, 1, 126, 127, 128, 1000, 100000 })
            test($"hover text: UTF-16 boundary {length} chooses one presentation", () =>
            {
                var text = new string('重', length);
                Check.Equal(length > 0, TrayHoverPolicy.RequiresPopup(text));
                Check.Equal("", TrayHoverPolicy.NativeText(text));
            });

        test("hover text: supplementary characters count as two UTF-16 units", () =>
        {
            var text = new string('a', 125) + "\U0001F4C5";
            Check.True(TrayHoverPolicy.RequiresPopup(text));
            Check.Equal("", TrayHoverPolicy.NativeText(text));
            Check.True(TrayHoverPolicy.RequiresPopup(text + "a"));
            Check.Equal("", TrayHoverPolicy.NativeText(text + "a"));
        });

        foreach (var count in new[] { 0, 1, 2, 3, 20, 1000, 10000 })
            test($"hover text: {count} card rows preserve every original line", () =>
            {
                var rows = new[] { "5 小时额度剩余 40%", "重置：10-08 13:22", "周额度剩余 80%", "重置：10-10 19:01" }
                    .Concat(Enumerable.Range(1, count).Select(index => $"重置卡 {index} 到期：10-18 10:22")).ToArray();
                var text = string.Join("\n", rows);
                var rendered = TrayHoverPolicy.Lines(text);
                Check.Equal(count + 4, rendered.Length);
                Check.Equal(text, string.Join("\n", rendered));
                for (var index = 0; index < rows.Length; index++) Check.Equal(rows[index], rendered[index]);
            });

        test("hover text: CRLF and empty lines retain line boundaries", () =>
            Check.Equal("first\n\nlast\n", string.Join("\n", TrayHoverPolicy.Lines("first\r\n\r\nlast\r\n"))));

        foreach (var rows in new[] { 1, 4, 10, 100, 10000 })
            test($"hover layout: {rows} rows expand until work-area height then scroll", () =>
            {
                var work = new Rectangle(-1920, -200, 1840, 1080);
                var icon = new Rectangle(-64, 780, 32, 32);
                var result = TrayHoverPolicy.Place(icon, work, 420, rows * 20, 8);
                Check.Equal(Math.Min(work.Height, rows * 20 + 16), result.Height);
                Check.Equal(work.Right, result.Right);
                Check.True(work.Contains(result));
            });

        test("hover layout: huge measured content cannot overflow the height arithmetic", () =>
        {
            var work = new Rectangle(50, 50, 120, 70);
            var result = TrayHoverPolicy.Place(new Rectangle(10, 60, 24, 24), work, 420, int.MaxValue, 8);
            Check.Equal(work, result);
        });

        test("hover lifecycle: ordinary native tooltip has no custom departure grace", () =>
        {
            var session = new TrayHoverSession();
            Check.True(session.ObservePointer(true, false, false, 0));
            Check.False(session.ObservePointer(false, false, false, 1));
        });

        test("hover lifecycle: crossing taskbar padding into popup retains one hover entry", () =>
        {
            var session = new TrayHoverSession();
            Check.True(session.ObservePointer(true, false, true, 0));
            Check.True(session.ObservePointer(false, false, true, 10));
            Check.True(session.ObservePointer(false, true, true, 199));
            Check.True(session.ObservePointer(false, true, true, 60000));
            Check.True(session.ObservePointer(true, false, true, 60050));
        });

        test("hover lifecycle: departure ends polling after bounded crossing grace", () =>
        {
            var session = new TrayHoverSession();
            Check.True(session.ObservePointer(false, false, true, 500));
            Check.True(session.ObservePointer(false, false, true, 699));
            Check.False(session.ObservePointer(false, false, true, 700));
        });

        test("hover lifecycle: Escape, click or menu dismissal suppress until departure", () =>
        {
            var session = new TrayHoverSession();
            session.Suppress();
            Check.True(session.Suppressed);
            Check.True(session.ObservePointer(true, false, false, 0));
            Check.True(session.Suppressed);
            Check.False(session.ObservePointer(false, false, false, 50));
            Check.False(session.Suppressed);
        });

        test("hover lifecycle: hidden popup rectangle cannot retain a hover session", () =>
        {
            var session = new TrayHoverSession();
            Check.False(session.ObservePointer(false, true, false, 0));
        });

        test("hover lifecycle: keyboard Shell open survives an unrelated pointer until Shell close", () =>
        {
            var session = new TrayHoverSession();
            session.Open(fromShell: true, pointerOverIcon: false);
            Check.True(session.ShellOwnsLifetime);
            Check.True(session.ObservePointer(false, false, true, 0));
            Check.True(session.ObservePointer(false, false, true, 60000));
            Check.True(session.CloseFromShell());
            Check.False(session.ShellOwnsLifetime);
            Check.False(session.ObservePointer(false, false, false, 60001));
            Check.False(session.CloseFromShell());
        });

        test("hover lifecycle: Shell selection survives refresh from native to long readout", () =>
        {
            var session = new TrayHoverSession();
            session.Open(fromShell: true, pointerOverIcon: false);
            Check.True(session.ObservePointer(false, false, false, 0));
            Check.True(session.ObservePointer(false, false, true, 1000));
            Check.True(session.CloseFromShell());
        });

        test("hover lifecycle: pointer Shell open retains crossing grace after Shell close", () =>
        {
            var session = new TrayHoverSession();
            session.Open(fromShell: true, pointerOverIcon: true);
            Check.False(session.ShellOwnsLifetime);
            Check.False(session.CloseFromShell());
            Check.True(session.ObservePointer(false, false, true, 0));
            Check.True(session.ObservePointer(false, true, true, 100));
            Check.True(session.ObservePointer(false, false, true, 500));
            Check.False(session.ObservePointer(false, false, true, 700));
        });

        test("hover lifecycle: mouse entry takes over a keyboard Shell session", () =>
        {
            var session = new TrayHoverSession();
            session.Open(fromShell: true, pointerOverIcon: false);
            session.Open(fromShell: false, pointerOverIcon: true);
            Check.False(session.ShellOwnsLifetime);
            Check.True(session.ObservePointer(true, false, true, 0));
            Check.True(session.ObservePointer(false, false, true, 50));
            Check.False(session.ObservePointer(false, false, true, 250));
        });

        test("hover lifecycle: keyboard Escape, click, menu and focus loss release Shell lifetime", () =>
        {
            var session = new TrayHoverSession();
            session.Open(fromShell: true, pointerOverIcon: false);
            session.Suppress();
            Check.False(session.ShellOwnsLifetime);
            Check.True(session.Suppressed);
            Check.False(session.ObservePointer(false, false, false, 0));
            session.Open(fromShell: true, pointerOverIcon: false);
            Check.True(session.ShellOwnsLifetime);
            session.Reset();
            Check.False(session.ShellOwnsLifetime);
        });

        test("hover lifecycle: removal, Explorer restart and disposal reset suppression", () =>
        {
            var session = new TrayHoverSession();
            for (var index = 0; index < 20; index++)
            {
                session.Suppress();
                session.Reset();
                Check.False(session.Suppressed);
                Check.True(session.ObservePointer(true, false, true, index * 100));
            }
        });
    }
}
