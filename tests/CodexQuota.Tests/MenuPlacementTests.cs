using System.Drawing;
using CodexQuota.Tray;

internal static class MenuPlacementTests
{
    // These execute production geometry, not a fake menu. Native HWND/WinForms
    // integration and foreground behavior still require Windows acceptance.
    public static void Register(Action<string, Action> test)
    {
        test("theme submenu: three choices stay on owner through repeated opens and DPI changes", () =>
        {
            foreach (var work in new[] { new Rectangle(0, 0, 1840, 1080), new Rectangle(2000, 0, 1840, 1080), new Rectangle(-1920, -1080, 1840, 1040) })
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
            for (var reopen = 0; reopen < 3; reopen++)
            {
                var parent = new Rectangle(work.Right - (int)(320 * scale), work.Bottom - (int)(280 * scale), (int)(320 * scale), (int)(280 * scale));
                var row = new Rectangle(parent.Left, parent.Top + (int)(69 * scale), parent.Width, (int)(31 * scale));
                var result = TrayMenuPlacement.CalculateSubmenu(parent, row, work, new Size((int)(190 * scale), (int)(107 * scale)));
                Check.True(work.Contains(result));
            }
        });

        test("menu placement: right taskbar at dual-monitor seam opens into owner on left", () =>
        {
            var icon = new Rectangle(1872, 900, 32, 32);
            var work = new Rectangle(0, 0, 1840, 1080);
            var menu = TrayMenuPlacement.Calculate(icon, work, new Size(420, 360));
            Check.Equal(new Rectangle(1420, 720, 420, 360), menu);
            Check.True(menu.Right <= work.Right);
        });

        test("menu placement: left taskbar on right-hand monitor opens right", () =>
        {
            var icon = new Rectangle(1928, 850, 32, 32);
            var work = new Rectangle(2000, 0, 1840, 1080);
            Check.Equal(new Rectangle(2000, 720, 420, 360), Place(icon, work));
        });

        test("menu placement: bottom taskbar opens above into owner", () =>
        {
            var icon = new Rectangle(1800, 1040, 32, 32);
            var work = new Rectangle(0, 0, 1920, 1040);
            Check.Equal(new Rectangle(1412, 680, 420, 360), Place(icon, work));
        });

        test("menu placement: top taskbar opens below into owner", () =>
        {
            var icon = new Rectangle(1800, 8, 32, 32);
            var work = new Rectangle(0, 48, 1920, 1032);
            Check.Equal(new Rectangle(1412, 48, 420, 360), Place(icon, work));
        });

        test("menu placement: negative-X owner never spills onto primary monitor", () =>
        {
            var icon = new Rectangle(-48, 900, 32, 32);
            var work = new Rectangle(-1920, 0, 1840, 1080);
            Check.Equal(new Rectangle(-500, 720, 420, 360), Place(icon, work));
        });

        test("menu placement: upper monitor negative Y stays on upper monitor", () =>
        {
            var icon = new Rectangle(1800, -40, 32, 32);
            var work = new Rectangle(0, -1080, 1920, 1040);
            Check.Equal(new Rectangle(1412, -400, 420, 360), Place(icon, work));
        });

        test("menu placement: negative X and Y retain their signed coordinates", () =>
        {
            var icon = new Rectangle(-1912, -900, 32, 32);
            var work = new Rectangle(-1840, -1080, 1840, 1080);
            Check.Equal(new Rectangle(-1840, -900, 420, 360), Place(icon, work));
        });

        test("menu placement: right-edge autohide opens left without reserved strip", () =>
        {
            var icon = new Rectangle(1880, 500, 32, 32);
            var work = new Rectangle(0, 0, 1920, 1080);
            Check.Equal(new Rectangle(1460, 500, 420, 360), Place(icon, work));
        });

        test("menu placement: left-edge autohide opens right without reserved strip", () =>
        {
            var icon = new Rectangle(8, 500, 32, 32);
            var work = new Rectangle(0, 0, 1920, 1080);
            Check.Equal(new Rectangle(40, 500, 420, 360), Place(icon, work));
        });

        test("menu placement: bottom overflow opens upward within same work area", () =>
        {
            var icon = new Rectangle(1700, 960, 32, 32);
            var work = new Rectangle(0, 0, 1920, 1040);
            Check.Equal(new Rectangle(1312, 600, 420, 360), Place(icon, work));
        });

        test("menu placement: right overflow opens inward without taskbar overlap", () =>
        {
            var icon = new Rectangle(1780, 400, 32, 32);
            var work = new Rectangle(0, 0, 1840, 1080);
            Check.Equal(new Rectangle(1360, 400, 420, 360), Place(icon, work));
        });

        test("menu placement: bottom icon on left half is left aligned", () =>
        {
            var icon = new Rectangle(100, 1040, 32, 32);
            var work = new Rectangle(0, 0, 1920, 1040);
            Check.Equal(new Rectangle(100, 680, 420, 360), Place(icon, work));
        });

        test("menu placement: portrait owner and nonaligned monitor origins", () =>
        {
            var icon = new Rectangle(-48, -100, 32, 32);
            var work = new Rectangle(-1080, -600, 1000, 1920);
            Check.Equal(new Rectangle(-500, -100, 420, 360), Place(icon, work));
        });

        test("menu placement: keyboard uses icon even if pointer is on another monitor", () =>
        {
            // The placement API deliberately has no pointer input when icon bounds exist.
            var icon = new Rectangle(-48, 100, 32, 32);
            var work = new Rectangle(-1920, 0, 1840, 1080);
            Check.Equal(new Rectangle(-500, 100, 420, 360), Place(icon, work));
        });

        test("menu placement: cursor fallback at seam remains within selected owner", () =>
        {
            var work = new Rectangle(1920, 0, 1920, 1040);
            Check.True(work.Contains(Place(new Rectangle(1920, 500, 1, 1), work)));
        });

        test("menu placement: oversized menus are capped to owner work area", () =>
        {
            var work = new Rectangle(-320, -200, 320, 200);
            Check.Equal(work, TrayMenuPlacement.Calculate(new Rectangle(-20, -20, 16, 16), work, new Size(1000, 800)));
        });

        test("menu placement: empty provisional menu size has valid bounded rectangle", () =>
        {
            var work = new Rectangle(0, 0, 320, 200);
            var result = TrayMenuPlacement.Calculate(new Rectangle(310, 190, 8, 8), work, Size.Empty);
            Check.Equal(new Size(1, 1), result.Size);
            Check.True(work.Contains(result));
        });

        test("menu placement: size changes during Opening retain inward edge", () =>
        {
            var icon = new Rectangle(1872, 1000, 32, 32);
            var work = new Rectangle(0, 0, 1840, 1080);
            foreach (var size in new[] { new Size(150, 80), new Size(420, 360), new Size(650, 480), new Size(210, 180) })
            {
                var result = TrayMenuPlacement.Calculate(icon, work, size);
                Check.Equal(work.Right, result.Right);
                Check.Equal(work.Bottom, result.Bottom);
                Check.True(work.Contains(result));
            }
        });

        foreach (var dpi in new[] { 96, 120, 144, 192, 240, 288 })
            test($"menu placement: actual menu pixels at {dpi} DPI remain on owner", () =>
            {
                var icon = new Rectangle(-64, 1300, 32, 32);
                var work = new Rectangle(-2560, 0, 2464, 1440);
                var size = new Size(300 * dpi / 96, 260 * dpi / 96);
                var result = TrayMenuPlacement.Calculate(icon, work, size);
                Check.Equal(size, result.Size); // No double DPI multiplication.
                Check.Equal(work.Right, result.Right);
                Check.True(work.Contains(result));
            });

        test("menu placement: repeated cross-screen reopens do not reuse old monitor bounds", () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var left = new Rectangle(-1920, 0, 1840, 1080);
                var right = new Rectangle(80, -300, 2480, 1440);
                Check.True(left.Contains(Place(new Rectangle(-48, 900, 32, 32), left)));
                Check.True(right.Contains(Place(new Rectangle(8, -200, 32, 32), right)));
            }
        });

        test("menu placement: seeded layouts always remain entirely inside owner", () =>
        {
            var random = new Random(20261008);
            for (var i = 0; i < 4000; i++)
            {
                var work = new Rectangle(random.Next(-8000, 8000), random.Next(-4000, 4000),
                    random.Next(160, 5000), random.Next(120, 3000));
                var icon = new Rectangle(random.Next(work.Left - 100, work.Right + 100),
                    random.Next(work.Top - 100, work.Bottom + 100), random.Next(16, 65), random.Next(16, 65));
                var result = TrayMenuPlacement.Calculate(icon, work, new Size(random.Next(1, 6000), random.Next(1, 4000)));
                Check.True(work.Contains(result));
                Check.True(result.Width > 0 && result.Height > 0);
            }
        });
    }

    private static Rectangle Place(Rectangle icon, Rectangle work) => TrayMenuPlacement.Calculate(icon, work, new Size(420, 360));
}
