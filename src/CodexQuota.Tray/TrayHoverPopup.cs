using System.Runtime.InteropServices;
using CodexQuota.Core;

namespace CodexQuota.Tray;

/// <summary>Read-only, nonactivating layered card. Only the viewport is rasterized; long lists scroll in place.</summary>
internal sealed class TrayHoverPopup : Form
{
    private static readonly IHoverTextPainter TextPainter = new NativeHoverTextPainter();
    private readonly Rectangle _iconBounds, _workArea;
    private HoverCardModel? _model;
    private Bitmap? _frame;
    private float _scale = 1, _scroll;
    private bool _layingOut, _dragging, _layered = true;
    private readonly List<HoverLinkRegion> _links = new();
    private readonly ToolTip _linkTooltip = new() { ShowAlways = true, InitialDelay = 300, ReshowDelay = 100 };
    private HoverLinkRegion? _pressedLink, _pointedLink;
    private Point _pressPoint;
    public event EventHandler? ContextMenuRequested;
    public event Action<HoverLinkRegion>? LinkActivated;
    public bool IsDraggingScrollbar => _dragging;
    public void ObserveDrag(Point screenPointer, MouseButtons buttons)
    {
        if (!_dragging) return;
        if ((buttons & MouseButtons.Left) == 0) { _dragging = false; Capture = false; return; }
        ScrollThumb(PointToClient(screenPointer).Y);
    }

    public TrayHoverPopup(Rectangle iconBounds, Rectangle workArea)
    {
        _iconBounds = iconBounds;
        _workArea = workArea;
        AutoScaleMode = AutoScaleMode.None; // All geometry and fonts are painted in monitor-scaled logical units.
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        TabStop = false;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : WindowsAppTheme.IsDark ? Color.FromArgb(21, 35, 45) : Color.FromArgb(250, 252, 253);
        AccessibleRole = AccessibleRole.ToolTip;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Bounds = TrayMenuPlacement.Calculate(iconBounds, workArea, new Size(1, 1));
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            if (_layered) parameters.ExStyle |= 0x00080000; // WS_EX_LAYERED: per-pixel alpha and soft shadow.
            return parameters;
        }
    }

    public void SetContent(HoverCardModel model)
    {
        if (ReferenceEquals(_model, model)) return;
        _pressedLink = null; // A refreshed/expired target cannot complete an older mouse-down.
        _pointedLink = null;
        _linkTooltip.SetToolTip(this, "");
        Cursor = Cursors.Default;
        _model = model;
        AccessibleName = "Codex 额度";
        AccessibleDescription = model.AccessibleText;
        _ = Handle;
        LayoutContent();
    }

    private float MaxScroll => _model is null ? 0 : HoverCardLayout.MaxScroll(_model, ClientSize.Height, _scale);

    private void LayoutContent()
    {
        if (_layingOut || IsDisposed || _model is null) return;
        _layingOut = true;
        try
        {
            // Very small work areas shrink uniformly rather than clipping dates horizontally.
            _scale = HoverCardLayout.Scale(DeviceDpi, _workArea.Width);
            var width = HoverCardLayout.PixelWidth(_scale);
            var height = HoverCardLayout.PixelHeight(_model, _scale, _workArea.Height);
            Bounds = TrayMenuPlacement.Calculate(_iconBounds, _workArea, new Size(width, height));
            _scroll = Math.Max(0, Math.Min(_scroll, MaxScroll));
            DrawFrame();
        }
        finally { _layingOut = false; }
    }

    private void DrawFrame()
    {
        if (_model is null || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        BackColor = SystemInformation.HighContrast ? SystemColors.Window : WindowsAppTheme.IsDark
            ? Color.FromArgb(21, 35, 45) : Color.FromArgb(250, 252, 253);
        _links.Clear();
        var next = HoverCardRenderer.Render(_model, ClientSize, _scale, _scroll, SystemInformation.HighContrast,
            textPainter: TextPainter, darkMode: WindowsAppTheme.IsDark, links: _links);
        var previous = _frame;
        _frame = next;
        previous?.Dispose();
        if (_layered && !Present(next))
        {
            // Remote sessions/compositing failures retain the same native readout as an opaque rounded form.
            _layered = false;
            RecreateHandle();
        }
        if (!_layered)
        {
            using var path = HoverCardRenderer.Rounded(new RectangleF(0, 0, Width, Height), HoverCardLayout.CornerRadius * _scale);
            var oldRegion = Region;
            Region = new Region(path);
            oldRegion?.Dispose();
        }
        if (!_layered) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_layered && _frame is not null) e.Graphics.DrawImageUnscaled(_frame, 0, 0);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0) return;
        var step = lines < 0 ? ClientSize.Height / _scale * .85f : Math.Max(1, lines) * 20f;
        ScrollTo(_scroll - e.Delta / 120f * step);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressedLink = null;
        if (e.Button == MouseButtons.Left && MaxScroll > 0 && e.X / _scale >= ClientSize.Width / _scale - HoverCardLayout.Shadow - 20)
        {
            _dragging = true;
            Capture = true;
            ScrollThumb(e.Y);
        }
        else if (e.Button == MouseButtons.Left)
        {
            _pressedLink = HitLink(e.Location);
            _pressPoint = e.Location;
            if (_pressedLink is not null) Capture = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) { ScrollThumb(e.Y); return; }
        if (_pressedLink is not null && (Math.Abs(e.X - _pressPoint.X) > SystemInformation.DragSize.Width / 2 ||
            Math.Abs(e.Y - _pressPoint.Y) > SystemInformation.DragSize.Height / 2)) _pressedLink = null;
        var link = HitLink(e.Location);
        Cursor = link is null ? Cursors.Default : Cursors.Hand;
        if (link != _pointedLink)
        {
            _pointedLink = link;
            _linkTooltip.SetToolTip(this, link?.Description ?? "");
        }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var wasDragging = _dragging;
        var pressed = _pressedLink;
        _pressedLink = null;
        _dragging = false;
        Capture = false;
        if (!wasDragging && e.Button == MouseButtons.Left && pressed is not null && pressed == HitLink(e.Location))
            LinkActivated?.Invoke(pressed);
        if (e.Button == MouseButtons.Right) ContextMenuRequested?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) { _dragging = false; _pressedLink = null; }
    }
    private HoverLinkRegion? HitLink(Point point) => HoverLinkRegion.HitTest(_links,
        point.X, point.Y, _scale, _scroll, ClientSize.Width, ClientSize.Height, MaxScroll > 0);
    private void ScrollThumb(int y)
    {
        if (_model is null) return;
        var viewport = ClientSize.Height / _scale - 2 * HoverCardLayout.Shadow;
        var track = Math.Max(1, viewport - 36);
        var thumb = Math.Min(track, Math.Max(28, track * viewport / HoverCardLayout.ContentHeight(_model)));
        ScrollTo((y / _scale - HoverCardLayout.Shadow - 18 - thumb / 2) / Math.Max(1, track - thumb) * MaxScroll);
    }
    private void ScrollTo(float value)
    {
        value = Math.Max(0, Math.Min(value, MaxScroll));
        if (Math.Abs(value - _scroll) < .1f) return;
        _pressedLink = null;
        _pointedLink = null;
        _linkTooltip.SetToolTip(this, "");
        _scroll = value;
        DrawFrame();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE, dispatch input.
        base.WndProc(ref message);
        if (message.Msg == 0x02E0) LayoutContent();
    }

    private bool Present(Bitmap bitmap)
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;
        var memory = CreateCompatibleDC(screen);
        IntPtr nativeBitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            if (memory == IntPtr.Zero) return false;
            nativeBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memory, nativeBitmap);
            var destination = new NativePoint(Left, Top);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(bitmap.Width, bitmap.Height);
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            return UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, 2);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (nativeBitmap != IntPtr.Zero) DeleteObject(nativeBitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _linkTooltip.Dispose(); _frame?.Dispose(); _frame = null; }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize(int width, int height) { public int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint destination,
        ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
}
