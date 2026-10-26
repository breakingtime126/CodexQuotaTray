using System.Drawing;
using CodexQuota.Tray;

internal static class SubmenuPlacementTests
{
    // Production physical-pixel geometry only. These do not claim to exercise
    // native WinForms HWND creation, monitor DPI changes, or input routing.
    public static void Register(Action<string, Action> test)
    {
        test("submenu placement: compact action menu and dotted source rows stay on the owning screen at every scale", () =>
        {
            foreach (var scale in new[] { 1d, 1.25d, 1.5d, 1.75d, 2d, 3d })
            foreach (var work in new[] { new Rectangle(0, 0, 1840, 1080), new Rectangle(-1920, -200, 1840, 1240), new Rectangle(2000, 0, 1840, 1080) })
            foreach (var rightEdge in new[] { false, true })
            {
                var icon = new Rectangle(rightEdge ? work.Right + 8 : work.Left - 40, work.Bottom - 48, 32, 32);
                var parent = TrayMenuPlacement.Calculate(icon, work, new Size((int)(320 * scale), (int)(214 * scale)));
                var child = TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, (int)(38 * scale)), work,
                    new Size((int)(190 * scale), (int)(154 * scale)));
                Check.True(work.Contains(parent));
                Check.True(work.Contains(child));
            }
        });

        test("submenu placement: shared right taskbar seam opens left on parent screen", () =>
        {
            var work = new Rectangle(0, 0, 1840, 1080);
            var parent = TrayMenuPlacement.Calculate(new Rectangle(1872, 900, 32, 32), work, new Size(420, 360));
            var row = Row(parent, 230);
            var child = TrayMenuPlacement.CalculateSubmenu(parent, row, work, new Size(260, 160));
            Check.Equal(new Rectangle(1160, 920, 260, 160), child);
            Check.Equal(parent.Left, child.Right);
            Check.True(work.Contains(child));
        });

        test("submenu placement: shared left taskbar seam opens right on parent screen", () =>
        {
            var work = new Rectangle(2000, 0, 1840, 1080);
            var parent = TrayMenuPlacement.Calculate(new Rectangle(1928, 850, 32, 32), work, new Size(420, 360));
            var child = TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, 200), work, new Size(260, 160));
            Check.Equal(new Rectangle(2420, 920, 260, 160), child);
            Check.Equal(parent.Right, child.Left);
            Check.True(work.Contains(child));
        });

        test("submenu placement: right preferred whenever both sides fit", () =>
        {
            var work = new Rectangle(0, 0, 1920, 1040);
            var parent = new Rectangle(800, 200, 420, 360);
            Check.Equal(new Rectangle(1220, 280, 260, 160), Place(parent, Row(parent, 80), work));
        });

        test("submenu placement: exact right fit does not flip left", () =>
        {
            var work = new Rectangle(-1920, 0, 1840, 1080);
            var parent = new Rectangle(-760, 200, 420, 360);
            var child = Place(parent, Row(parent, 80), work);
            Check.Equal(parent.Right, child.Left);
            Check.Equal(work.Right, child.Right);
        });

        test("submenu placement: one pixel beyond right edge flips inward", () =>
        {
            var work = new Rectangle(-1920, 0, 1840, 1080);
            var parent = new Rectangle(-759, 200, 420, 360);
            var child = Place(parent, Row(parent, 80), work);
            Check.Equal(parent.Left, child.Right);
            Check.True(work.Contains(child));
        });

        test("submenu placement: right overflow and autohide parents retain inward choice", () =>
        {
            foreach (var icon in new[] { new Rectangle(1880, 500, 32, 32), new Rectangle(1780, 400, 32, 32) })
            {
                var work = new Rectangle(0, 0, 1920, 1080);
                var parent = TrayMenuPlacement.Calculate(icon, work, new Size(420, 360));
                var child = Place(parent, Row(parent, 80), work);
                Check.Equal(parent.Left, child.Right);
                Check.True(work.Contains(child));
            }
        });

        test("submenu placement: bottom row clamps vertically without crossing lower screen", () =>
        {
            var work = new Rectangle(0, -1080, 1920, 1040);
            var parent = new Rectangle(1400, -400, 420, 360);
            var child = Place(parent, Row(parent, 320), work);
            Check.Equal(new Rectangle(1140, -200, 260, 160), child);
            Check.Equal(work.Bottom, child.Bottom);
        });

        test("submenu placement: top edge remains on upper negative-coordinate monitor", () =>
        {
            var work = new Rectangle(-2560, -1440, 2480, 1400);
            var parent = new Rectangle(-560, -1440, 420, 360);
            var child = Place(parent, Row(parent, 0), work);
            Check.Equal(new Rectangle(-820, -1440, 260, 160), child);
        });

        test("submenu placement: portrait monitor with offset origin", () =>
        {
            var work = new Rectangle(-1080, -600, 1000, 1920);
            var parent = new Rectangle(-500, 500, 420, 600);
            var child = Place(parent, Row(parent, 200), work);
            Check.Equal(new Rectangle(-760, 700, 260, 160), child);
        });

        test("submenu placement: neither side fits so roomier left is clamped", () =>
        {
            var work = new Rectangle(0, 0, 600, 500);
            var parent = new Rectangle(300, 100, 220, 350);
            var child = TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, 100), work, new Size(400, 200));
            Check.Equal(new Rectangle(0, 200, 400, 200), child);
        });

        test("submenu placement: neither side fits so roomier right is clamped", () =>
        {
            var work = new Rectangle(0, 0, 600, 500);
            var parent = new Rectangle(80, 100, 220, 350);
            var child = TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, 100), work, new Size(400, 200));
            Check.Equal(new Rectangle(200, 200, 400, 200), child);
        });

        test("submenu placement: oversized child is capped to original work area", () =>
        {
            var work = new Rectangle(-320, -200, 320, 200);
            var parent = new Rectangle(-160, -200, 160, 200);
            Check.Equal(work, TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, 100), work, new Size(1500, 900)));
        });

        test("submenu placement: invalid provisional dimensions are positive and bounded", () =>
        {
            var work = new Rectangle(0, 0, 320, 200);
            var parent = new Rectangle(160, 0, 160, 200);
            foreach (var size in new[] { Size.Empty, new Size(-20, -40) })
            {
                var child = TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, 190), work, size);
                Check.Equal(new Size(1, 1), child.Size);
                Check.True(work.Contains(child));
            }
        });

        test("submenu placement: rejects empty work area instead of selecting another screen", () =>
        {
            foreach (var work in new[] { Rectangle.Empty, new Rectangle(0, 0, -1, 200), new Rectangle(0, 0, 300, 0) })
                Check.Throws<ArgumentOutOfRangeException>(() => Place(new Rectangle(0, 0, 100, 100), Rectangle.Empty, work));
        });

        test("submenu placement: Opening text changes can change direction without stale state", () =>
        {
            var work = new Rectangle(0, 0, 1920, 1080);
            var parent = new Rectangle(1200, 500, 420, 360);
            foreach (var width in new[] { 150, 260, 301, 700, 150 })
            {
                var child = TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, 100), work, new Size(width, 180));
                if (width <= 300) Check.Equal(parent.Right, child.Left);
                else Check.Equal(parent.Left, child.Right);
                Check.True(work.Contains(child));
            }
        });

        foreach (var dpi in new[] { 96, 120, 144, 192, 240, 288 })
            test($"submenu placement: final pixels at {dpi} DPI are not scaled twice", () =>
            {
                var work = new Rectangle(-3840, -100, 3744, 2160);
                var parentSize = new Size(420 * dpi / 96, 360 * dpi / 96);
                var parent = new Rectangle(work.Right - parentSize.Width, 0, parentSize.Width, parentSize.Height);
                var childSize = new Size(260 * dpi / 96, 160 * dpi / 96);
                var child = TrayMenuPlacement.CalculateSubmenu(parent, Row(parent, 100 * dpi / 96), work, childSize);
                Check.Equal(childSize, child.Size);
                Check.Equal(parent.Left, child.Right);
                Check.True(work.Contains(child));
            });

        test("submenu placement: repeated cross-screen reopen uses current parent anchor", () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var left = new Rectangle(-1920, 0, 1840, 1080);
                var leftParent = new Rectangle(-500, 600, 420, 360);
                var leftChild = Place(leftParent, Row(leftParent, 100), left);
                Check.Equal(leftParent.Left, leftChild.Right);
                Check.True(left.Contains(leftChild));

                var right = new Rectangle(80, -300, 2480, 1440);
                var rightParent = new Rectangle(80, 0, 420, 360);
                var rightChild = Place(rightParent, Row(rightParent, 100), right);
                Check.Equal(rightParent.Right, rightChild.Left);
                Check.True(right.Contains(rightChild));
            }
        });

        test("submenu placement: parent resize and row movement recalculate live attachment", () =>
        {
            var work = new Rectangle(0, 0, 1840, 1080);
            foreach (var width in new[] { 250, 420, 800, 300 })
            foreach (var offset in new[] { 0, 100, 300 })
            {
                var parent = new Rectangle(work.Right - width, 200, width, 500);
                var child = Place(parent, Row(parent, offset), work);
                Check.Equal(parent.Left, child.Right);
                Check.Equal(parent.Top + offset, child.Top);
                Check.True(work.Contains(child));
            }
        });

        test("submenu placement: seeded layouts remain fully on anchored screen", () =>
        {
            var random = new Random(20261013);
            for (var i = 0; i < 5000; i++)
            {
                var work = new Rectangle(random.Next(-8000, 8000), random.Next(-4000, 4000),
                    random.Next(160, 5000), random.Next(120, 3000));
                var parentWidth = random.Next(1, work.Width + 1);
                var parentHeight = random.Next(1, work.Height + 1);
                var parent = new Rectangle(random.Next(work.Left, work.Right - parentWidth + 1),
                    random.Next(work.Top, work.Bottom - parentHeight + 1), parentWidth, parentHeight);
                var row = Row(parent, random.Next(0, parent.Height));
                var requested = new Size(random.Next(1, 6000), random.Next(1, 4000));
                var child = TrayMenuPlacement.CalculateSubmenu(parent, row, work, requested);
                Check.True(work.Contains(child));
                Check.Equal(Math.Min(requested.Width, work.Width), child.Width);
                Check.Equal(Math.Min(requested.Height, work.Height), child.Height);
                if (work.Right - parent.Right >= child.Width) Check.Equal(parent.Right, child.Left);
                else if (parent.Left - work.Left >= child.Width) Check.Equal(parent.Left, child.Right);
            }
        });
    }

    private static Rectangle Row(Rectangle parent, int offset) => new(parent.Left + 4, parent.Top + offset, parent.Width - 8, 30);
    private static Rectangle Place(Rectangle parent, Rectangle row, Rectangle work) =>
        TrayMenuPlacement.CalculateSubmenu(parent, row, work, new Size(260, 160));
}
