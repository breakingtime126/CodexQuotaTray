namespace CodexQuota.Tray;

/// <summary>
/// Pins this popup to the tray icon's work area through Opening, autosize and DPI
/// changes. A Show direction alone is insufficient: WinForms can pick the next
/// monitor before Opening updates the items or the popup acquires its final DPI.
/// </summary>
internal sealed class TrayContextMenuStrip : ContextMenuStrip
{
    private Rectangle _iconBounds, _workArea;
    private bool _anchored, _styling;

    // Children inherit this exact tray-selected screen, not Screen.FromControl
    // on a submenu whose default location may already be on the next monitor.
    public Rectangle? AnchoredWorkArea => _anchored ? _workArea : null;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Region owns rounding on both Windows versions. The normal ToolStrip
        // DropShadowEnabled requests the system menu shadow; availability is OS-dependent.
        ApplyShape();
    }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ApplyShape(); }
    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        ApplySpacing();
        base.OnOpening(e);
        ApplyShape();
    }
    protected override Padding DefaultPadding
    {
        get
        {
            // ToolStripDropDownMenu reapplies DefaultPadding during layout. Preserve
            // its native check/image gutter rather than overwriting Padding ad hoc.
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

    public void AnchorTo(Rectangle iconBounds, Rectangle workArea)
    {
        _iconBounds = iconBounds;
        _workArea = workArea;
        _anchored = workArea.Width > 0 && workArea.Height > 0;
        // Set before creating/showing the HWND, including on reopen on a new screen.
        MaximumSize = _anchored ? workArea.Size : Size.Empty;
        Reposition();
    }

    public void Reposition()
    {
        if (_anchored && !IsDisposed)
            SetBoundsCore(Left, Top, Width, Height, BoundsSpecified.All);
    }

    public void ReleaseAnchor()
    {
        _anchored = false;
        if (!IsDisposed) MaximumSize = Size.Empty;
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        if (_anchored)
        {
            var bounds = TrayMenuPlacement.Calculate(_iconBounds, _workArea, new Size(width, height));
            x = bounds.X; y = bounds.Y; width = bounds.Width; height = bounds.Height;
            specified = BoundsSpecified.All;
        }
        // The complete rectangle is already on the intended screen. WinForms'
        // own GetDropDownBounds therefore cannot choose the adjacent monitor.
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x02E0 && _anchored && !IsDisposed) // WM_DPICHANGED
        {
            // Reapply in screen pixels after any Framework scaling/suggested rect.
            ApplySpacing();
            MaximumSize = _workArea.Size;
            Reposition();
            ApplyShape();
        }
    }
}
