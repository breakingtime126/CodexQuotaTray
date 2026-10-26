using System.Drawing;

namespace CodexQuota.Tray;

/// <summary>
/// Screen-coordinate geometry only: every result belongs to the icon's monitor,
/// even when another monitor has more room beyond a shared taskbar edge.
/// The caller supplies actual menu pixels after layout/DPI scaling, not DIPs.
/// </summary>
internal static class TrayMenuPlacement
{
    /// <summary>
    /// Positions a child beside its parent on the parent's already selected work
    /// area. The owner row and both menus are in physical screen pixels. Never
    /// infer a monitor from a provisional child rectangle across a screen seam.
    /// </summary>
    public static Rectangle CalculateSubmenu(Rectangle parent, Rectangle ownerRow, Rectangle workArea, Size menuSize)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));

        var width = Math.Min(Math.Max(1, menuSize.Width), workArea.Width);
        var height = Math.Min(Math.Max(1, menuSize.Height), workArea.Height);
        var rightSpace = (long)workArea.Right - parent.Right;
        var leftSpace = (long)parent.Left - workArea.Left;
        // Normal menus open right when the entire child fits. At a right-hand
        // taskbar/seam, open inward to the left. If neither side fits, use the
        // roomier side and clamp; overlap is preferable to another monitor.
        var openRight = rightSpace >= width || (leftSpace < width && rightSpace >= leftSpace);
        long x = openRight ? parent.Right : (long)parent.Left - width;
        long y = ownerRow.Top;
        x = Math.Max(workArea.Left, Math.Min(x, (long)workArea.Right - width));
        y = Math.Max(workArea.Top, Math.Min(y, (long)workArea.Bottom - height));
        return new Rectangle((int)x, (int)y, width, height);
    }

    public static Rectangle Calculate(Rectangle icon, Rectangle workArea, Size menuSize)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));

        // Extremely small work areas or large fonts must not leak onto a neighbor.
        // ToolStripDropDownMenu provides vertical scrolling for constrained height.
        var width = Math.Min(Math.Max(1, menuSize.Width), workArea.Width);
        var height = Math.Min(Math.Max(1, menuSize.Height), workArea.Height);
        var centerX = (long)icon.Left + icon.Width / 2;
        var centerY = (long)icon.Top + icon.Height / 2;
        var edge = InwardEdge(icon, workArea, centerX, centerY);
        long x, y;
        if (edge is Edge.Left or Edge.Right)
        {
            x = edge == Edge.Left ? Math.Max(icon.Right, workArea.Left)
                : (long)Math.Min(icon.Left, workArea.Right) - width;
            y = icon.Top;
        }
        else
        {
            x = centerX < (long)workArea.Left + workArea.Width / 2 ? icon.Left : (long)icon.Right - width;
            y = edge == Edge.Top ? Math.Max(icon.Bottom, workArea.Top)
                : (long)Math.Min(icon.Top, workArea.Bottom) - height;
        }

        // Signed virtual-screen coordinates are intentional (left/upper monitors).
        x = Math.Max(workArea.Left, Math.Min(x, (long)workArea.Right - width));
        y = Math.Max(workArea.Top, Math.Min(y, (long)workArea.Bottom - height));
        return new Rectangle((int)x, (int)y, width, height);
    }

    private static Edge InwardEdge(Rectangle icon, Rectangle work, long centerX, long centerY)
    {
        // Prefer the reserved strip actually containing this icon, including the
        // common vertical taskbar at the seam between two side-by-side monitors.
        if (centerX < work.Left) return Edge.Left;
        if (centerX >= work.Right) return Edge.Right;
        if (centerY < work.Top) return Edge.Top;
        if (centerY >= work.Bottom) return Edge.Bottom;

        // Overflow trays and auto-hide bars can be inside the work area. The
        // nearest edge gives an inward default without hardcoding a direction.
        var edge = Edge.Bottom;
        long distance = Math.Abs((long)work.Bottom - icon.Bottom);
        Prefer(Edge.Top, Math.Abs((long)icon.Top - work.Top));
        Prefer(Edge.Left, Math.Abs((long)icon.Left - work.Left));
        Prefer(Edge.Right, Math.Abs((long)work.Right - icon.Right));
        return edge;

        void Prefer(Edge candidate, long value)
        {
            if (value >= distance) return;
            edge = candidate;
            distance = value;
        }
    }

    private enum Edge { Left, Right, Top, Bottom }
}
