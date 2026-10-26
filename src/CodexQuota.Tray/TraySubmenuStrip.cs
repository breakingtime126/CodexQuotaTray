using System.Runtime.InteropServices;

namespace CodexQuota.Tray;

/// <summary>
/// A regular WinForms submenu with the same monitor anchor as the tray menu.
/// Keep normal ownership, focus, scrolling and dismissal; only constrain pixels.
/// </summary>
internal sealed class TraySubmenuStrip : ToolStripDropDownMenu
{
    private readonly TrayContextMenuStrip? _parentMenu;
    private bool _positioning, _constraining, _styling;

    public TraySubmenuStrip(TrayContextMenuStrip parentMenu)
    {
        _parentMenu = parentMenu ?? throw new ArgumentNullException(nameof(parentMenu));
        Renderer = parentMenu.Renderer;
        Font = parentMenu.Font;
        ShowImageMargin = false;
        ShowCheckMargin = true;
        DropShadowEnabled = true;
        parentMenu.LocationChanged += OnParentBoundsChanged;
        parentMenu.Layout += OnParentLayout;
        // Explicit DropDown assignments are not auto-generated, so WinForms
        // does not dispose them with the owner item. Tie this one to its root.
        parentMenu.Disposed += OnParentDisposed;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var value = base.CreateParams;
            // Start the HWND on the owning screen as well, before Windows picks
            // its initial DPI. All supplied sizes are already device pixels.
            if (TryCalculateBounds(new Size(value.Width, value.Height), out var bounds))
            {
                value.X = bounds.X; value.Y = bounds.Y;
                value.Width = bounds.Width; value.Height = bounds.Height;
            }
            return value;
        }
    }

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        RefreshConstraints();
        ApplySpacing();
        base.OnOpening(e);
        Reposition();
        ApplyShape();
    }

    protected override void OnOpened(EventArgs e)
    {
        Reposition();
        base.OnOpened(e);
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        base.OnClosed(e);
        // Do not carry the previous screen's cap into a reopen on a larger one.
        if (!IsDisposed) MaximumSize = Size.Empty;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyShape();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyShape();
    }

    protected override Padding DefaultPadding
    {
        get
        {
            var value = base.DefaultPadding;
            var inset = (int)(7 * Math.Max(.5f, DeviceDpi / 96f));
            return new Padding(value.Left, Math.Max(value.Top, inset), value.Right, Math.Max(value.Bottom, inset));
        }
    }

    private void ApplySpacing()
    {
        var scale = Math.Max(.5f, DeviceDpi / 96f);
        foreach (ToolStripItem item in Items)
        {
            item.Margin = new Padding(0);
            item.Padding = item is ToolStripSeparator ? new Padding(0, (int)(3 * scale), 0, (int)(3 * scale))
                : new Padding((int)(4 * scale), (int)(5 * scale), (int)(11 * scale), (int)(5 * scale));
        }
    }

    private void ApplyShape()
    {
        if (_styling || IsDisposed || Width <= 0 || Height <= 0) return;
        _styling = true;
        try
        {
            using var path = HoverCardRenderer.Rounded(new RectangleF(0, 0, Width, Height),
                TrayMenuPainter.Radius(Math.Max(.5f, DeviceDpi / 96f), Size));
            var previous = Region;
            Region = new Region(path);
            previous?.Dispose();
        }
        finally { _styling = false; }
    }

    private bool TryCalculateBounds(Size size, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (_parentMenu is not { IsDisposed: false, IsHandleCreated: true } parent ||
            parent.AnchoredWorkArea is not { } workArea || OwnerItem is not { } item || item.Owner != parent)
            return false;

        var ownerRow = parent.RectangleToScreen(item.Bounds);
        bounds = TrayMenuPlacement.CalculateSubmenu(parent.Bounds, ownerRow, workArea, size);
        return true;
    }

    private void RefreshConstraints()
    {
        if (_constraining || IsDisposed) return;
        _constraining = true;
        try
        {
            var maximum = _parentMenu?.AnchoredWorkArea?.Size ?? Size.Empty;
            if (MaximumSize != maximum) MaximumSize = maximum;
        }
        finally { _constraining = false; }
    }

    private void Reposition()
    {
        if (_positioning || IsDisposed) return;
        _positioning = true;
        try
        {
            RefreshConstraints();
            if (TryCalculateBounds(Size, out var bounds))
                SetBoundsCore(bounds.X, bounds.Y, bounds.Width, bounds.Height, BoundsSpecified.All);
        }
        finally { _positioning = false; }
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (TryCalculateBounds(new Size(width, height), out var bounds))
        {
            x = bounds.X; y = bounds.Y; width = bounds.Width; height = bounds.Height;
            specified = BoundsSpecified.All;
        }
        // ToolStripDropDown recomputes x/y from OwnerItem.DropDownLocation even
        // when supplied a bounded rectangle. The native proposal below is the
        // final guard, after that calculation and before anything is painted.
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x0046 && message.LParam != IntPtr.Zero && !IsDisposed) // WM_WINDOWPOSCHANGING
        {
            var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
            const uint noSize = 0x0001, noMove = 0x0002, hideWindow = 0x0080;
            if ((position.Flags & hideWindow) != 0) return;
            var size = (position.Flags & noSize) != 0 ? Size : new Size(position.Width, position.Height);
            if (!TryCalculateBounds(size, out var bounds)) return;

            var location = (position.Flags & noMove) != 0 ? Location : new Point(position.X, position.Y);
            if (location != bounds.Location)
            {
                position.X = bounds.X; position.Y = bounds.Y;
                position.Flags &= ~noMove;
            }
            if (size != bounds.Size)
            {
                position.Width = bounds.Width; position.Height = bounds.Height;
                position.Flags &= ~noSize;
            }
            Marshal.StructureToPtr(position, message.LParam, false);
        }
        else if (message.Msg == 0x02E0 && !IsDisposed) // WM_DPICHANGED
        {
            // Framework may scale MaximumSize or apply a suggested rectangle.
            // Reapply the parent's physical-pixel cap after it has finished.
            ApplySpacing();
            RefreshConstraints();
            Reposition();
            ApplyShape();
        }
    }

    private void OnParentBoundsChanged(object? sender, EventArgs e) { if (Visible) Reposition(); }
    private void OnParentLayout(object? sender, LayoutEventArgs e) { if (Visible) Reposition(); }
    private void OnParentDisposed(object? sender, EventArgs e) => Dispose();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _parentMenu is { } parent)
        {
            parent.LocationChanged -= OnParentBoundsChanged;
            parent.Layout -= OnParentLayout;
            parent.Disposed -= OnParentDisposed;
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
}
