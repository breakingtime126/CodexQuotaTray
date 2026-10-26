using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexQuota.Core;

namespace CodexQuota.Tray;

/// <summary>
/// Owns the notification icon identity, so hover boundaries can use Shell_NotifyIconGetRect
/// (including Explorer's overflow popup) without reflection, global hooks, or injected code.
/// </summary>
internal sealed class NativeTrayIcon : NativeWindow, IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint DismissMenuMessage = 0x8002;
    private const uint MessageFlag = 0x01, IconFlag = 0x02, TipFlag = 0x04, GuidFlag = 0x20, ShowTipFlag = 0x80;
    private const uint AllFields = MessageFlag | IconFlag | TipFlag;
    private static readonly Guid IconGuid = new("c0da730b-8089-4679-9ce1-e44b70296f4b");
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _registrationTimer = new();
    private readonly TrayIconRegistration _registration = new();
    private readonly TrayRegistrationRetry _registrationRetry = new();
    private readonly HoverRefreshGate _hover = new();
    private readonly TrayHoverSession _hoverSession = new();
    private readonly Stopwatch _hoverClock = Stopwatch.StartNew();
    private readonly TrayPopupSession _popup = new();
    private bool _visible, _disposed, _menuHadForeground;
    private Icon? _icon;
    private string _text = "";
    private HoverCardModel? _hoverContent;
    private TrayHoverPopup? _hoverPopup;
    private IntPtr _hoverForeground;
    private TrayContextMenuStrip? _menu;
    public TrayContextMenuStrip? ContextMenuStrip
    {
        get => _menu;
        set
        {
            if (ReferenceEquals(_menu, value)) return;
            CloseMenu();
            if (_menu is { } previous)
            {
                previous.Closed -= OnMenuClosed;
                previous.Disposed -= OnMenuDisposed;
            }
            _menu = value;
            if (_menu is { } next)
            {
                next.Closed += OnMenuClosed;
                next.Disposed += OnMenuDisposed;
            }
        }
    }
    public event EventHandler? DoubleClick;
    public event EventHandler? HoverEntered;
    public event EventHandler? EnvironmentChanged;
    public event Action<TrayDiagnostic>? Diagnostic;
    public event EventHandler? RegistrationUnavailable;
    public event EventHandler? Selected;
    public event Action<HoverLinkRegion>? LinkActivated;

    public NativeTrayIcon()
    {
        CreateHandle(new CreateParams { Caption = "CodexQuotaTray.MessageWindow" });
        _hoverTimer.Tick += (_, _) => CheckPointer();
        _registrationTimer.Tick += (_, _) =>
        {
            _registrationTimer.Stop();
            _registrationRetry.TimerElapsed();
            Update();
        };
    }
    public Icon? Icon { get => _icon; set { _icon = value; Update(IconFlag); } }
    public void SetHoverContent(HoverCardModel content)
    {
        if (ReferenceEquals(_hoverContent, content)) return;
        _hoverContent = content;
        _text = content.AccessibleText;
        Update(TipFlag);
        if (_hover.IsInside) ShowHoverPopup();
    }
    public bool Visible
    {
        get => _visible;
        set
        {
            if (value && !_visible) ResetRegistrationRetry();
            _visible = value;
            if (value) Update();
            else Remove();
        }
    }

    public bool TryGetVisibilityIdentity(out IntPtr window, out uint iconId)
    {
        window = IntPtr.Zero; iconId = 0;
        if (_disposed || !_visible || !_registration.IsAdded || Handle == IntPtr.Zero) return false;
        window = Handle; iconId = 1;
        return true;
    }

    private NotifyIconData Data(TrayIconIdentity identity, uint fields = AllFields) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = Handle, Id = 1,
        Flags = fields | (identity == TrayIconIdentity.Guid ? GuidFlag : 0) |
            (TrayHoverPolicy.RequiresPopup(_text) ? 0 : ShowTipFlag),
        Callback = CallbackMessage, Icon = _icon?.Handle ?? IntPtr.Zero,
        Tip = TrayHoverPolicy.NativeText(_text), Info = "", InfoTitle = "", Guid = identity == TrayIconIdentity.Guid ? IconGuid : Guid.Empty
    };
    private void Update(uint fields = AllFields)
    {
        if (_disposed || !_visible || _icon is null) return;
        if (!_registration.IsAdded && !_registrationRetry.CanAttempt) return;
        // Animation changes HICON only. Do not restart/rewrite the Shell tooltip or callback
        // every frame; the successfully registered identity is retained for all calls.
        var result = _registration.Update((operation, identity) => Notify(operation, identity, fields));
        if (result == TrayRegistrationResult.Unchanged) return;
        if (result != TrayRegistrationResult.Failed)
        {
            ResetRegistrationRetry();
            Diagnostic?.Invoke(result == TrayRegistrationResult.RegisteredGuid
                ? TrayDiagnostic.TrayRegisteredGuid : TrayDiagnostic.TrayRegisteredWindowId);
            if (!_registration.Version4) Diagnostic?.Invoke(TrayDiagnostic.TrayVersion4Unavailable);
            return;
        }

        CloseMenu();
        EndHover();
        Diagnostic?.Invoke(TrayDiagnostic.TrayRegistrationFailed);
        if (_registrationRetry.Failed() is { } delay)
        {
            _registrationTimer.Interval = delay;
            _registrationTimer.Start();
        }
        else
        {
            _visible = false; // No new attempts during the failure dialog's nested message loop.
            Diagnostic?.Invoke(TrayDiagnostic.TrayRegistrationRetryExhausted);
            RegistrationUnavailable?.Invoke(this, EventArgs.Empty);
        }
    }
    private bool Notify(TrayShellOperation operation, TrayIconIdentity identity, uint modifiedFields = AllFields)
    {
        var fields = operation == TrayShellOperation.Add ? AllFields
            : operation == TrayShellOperation.Modify ? modifiedFields : 0;
        var data = Data(identity, fields);
        if (operation == TrayShellOperation.SetVersion) data.Version = 4;
        return ShellNotifyIcon((uint)operation, ref data);
    }
    private void ResetRegistrationRetry()
    {
        _registrationTimer.Stop();
        _registrationRetry.Reset();
    }
    public void EnsureVisible() { if (!_registration.IsAdded) Update(); }
    public void ReturnFocus()
    {
        if (!_registration.IsAdded || _disposed) return;
        Notify(TrayShellOperation.SetFocus, _registration.Identity);
    }
    private void Remove()
    {
        CloseMenu();
        EndHover();
        ResetRegistrationRetry();
        _registration.Remove((operation, identity) => Notify(operation, identity));
    }
    private void OnHover(bool fromShell = false)
    {
        if (_disposed || !_visible || !_registration.IsAdded) return;
        var entered = _hover.Enter();
        if (entered) _hoverSession.Reset();
        _hoverSession.Open(fromShell, !fromShell || (TryGetBounds(out var bounds) && bounds.Contains(Cursor.Position)));
        if (entered) HoverEntered?.Invoke(this, EventArgs.Empty);
        if (_popup.IsActive) _hoverSession.Suppress();
        ShowHoverPopup();
        _hoverTimer.Start();
    }
    private void CheckPointer()
    {
        if (_disposed || !_visible || !_registration.IsAdded || !TryGetBounds(out var bounds))
        {
            EndHover(); // Explorer removed/hidden the icon; never leave an orphan popup.
            return;
        }
        var position = Cursor.Position;
        _hoverPopup?.ObserveDrag(position, Control.MouseButtons);
        var dragging = _hoverPopup is { IsDisposed: false, Visible: true, IsDraggingScrollbar: true };
        var overPopup = dragging || _hoverPopup is { IsDisposed: false, Visible: true } hover && hover.Bounds.Contains(position);
        if (_hoverPopup is { Visible: true } &&
            (((GetAsyncKeyState(0x1B) & 0x8000) != 0) || // Escape, only sampled during hover.
             (Control.MouseButtons != MouseButtons.None && !overPopup) ||
             GetForegroundWindow() != _hoverForeground))
            CloseHoverPopup(suppress: true);

        var inside = _hoverSession.ObservePointer(bounds.Contains(position), overPopup,
            _hoverPopup is { Visible: true }, _hoverClock.ElapsedMilliseconds);
        _hover.ObservePointer(inside);
        if (!_hover.IsInside) EndHover();
    }
    private void ShowHoverPopup()
    {
        if (_disposed || !_visible || !_registration.IsAdded || _popup.IsActive || _hoverSession.Suppressed ||
            _hoverContent is null || !TrayHoverPolicy.RequiresPopup(_text) || !TryGetBounds(out var bounds)) return;
        var position = Cursor.Position;
        // A refresh can complete while the pointer is crossing taskbar padding.
        // Update an existing popup then too; the hover timer still owns departure.
        if (_hoverPopup is null && !_hoverSession.ShellOwnsLifetime && !bounds.Contains(position)) return;
        if (_hoverPopup is null)
        {
            _hoverForeground = GetForegroundWindow();
            _hoverPopup = new TrayHoverPopup(bounds, Screen.FromRectangle(bounds).WorkingArea);
            _hoverPopup.ContextMenuRequested += (_, _) => ShowMenu();
            _hoverPopup.LinkActivated += link =>
            {
                // Dispose and suppress before handing control to an external browser.
                CloseHoverPopup(suppress: true);
                LinkActivated?.Invoke(link);
            };
        }
        _hoverPopup.SetContent(_hoverContent!);
        if (!_hoverPopup.Visible) _hoverPopup.Show(); // Modeless and nonactivating; refresh remains asynchronous.
    }
    private void CloseHoverPopup(bool suppress = false)
    {
        if (suppress) _hoverSession.Suppress();
        var hover = _hoverPopup;
        _hoverPopup = null;
        hover?.Dispose();
    }
    private void EndHover()
    {
        CloseHoverPopup();
        _hover.Leave();
        _hoverSession.Reset();
        _hoverTimer.Stop();
    }
    private bool TryGetBounds(out Rectangle rectangle)
    {
        var identity = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = Handle, Id = 1,
            Guid = _registration.Identity == TrayIconIdentity.Guid ? IconGuid : Guid.Empty
        };
        var result = ShellNotifyIconGetRect(ref identity, out var rect);
        rectangle = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return result == 0 && rectangle.Width > 0 && rectangle.Height > 0;
    }
    private void ShowMenu()
    {
        if (_disposed || _menu is not { IsDisposed: false } menu) return;
        CloseHoverPopup(suppress: true);
        _popup.Begin(); // Repeated activation also invalidates an already queued dismissal.
        _menuHadForeground = false;
        var generation = _popup.Generation;
        try
        {
            menu.AutoClose = true;
            // Always select the screen from this icon, not from free virtual-desktop
            // space or a seam coordinate. This also handles keyboard/overflow menus.
            // WM_CONTEXTMENU's packed wParam is not a reliable icon location.
            var iconBounds = TryGetBounds(out var bounds) ? bounds : new Rectangle(Cursor.Position, new Size(1, 1));
            menu.AnchorTo(iconBounds, Screen.FromRectangle(iconBounds).WorkingArea);
            // Create the real popup HWND before foreground ownership is established. The
            // first open must follow the same focus path as later opens with a cached HWND.
            _ = menu.Handle;
            if (!menu.Visible)
            {
                SetForegroundWindow(Handle);
                menu.Show(menu.Location);
                menu.Reposition(); // Opening can change text, visibility and final size.
            }
            if (_popup.IsCurrent(generation) && !menu.IsDisposed && menu.Visible)
            {
                // ToolStripDropDown shows without activation. Our callback window is
                // hidden, so do not rely on it alone to receive the first outside click
                // or Escape. Activate the now-visible popup, then let WinForms handle
                // normal menu navigation, outside clicks, and Escape through AutoClose.
                SetForegroundWindow(menu.Handle);
                menu.Focus();
                var foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out var foregroundProcess);
                _menuHadForeground = foreground != IntPtr.Zero && foregroundProcess == GetCurrentProcessId();
                // Windows can deny foreground activation for a synthetic/background request.
                // Never leave an orphan popup that cannot observe outside-click deactivation.
                if (!_menuHadForeground) menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
            }
        }
        finally
        {
            // Opening can be cancelled without ever raising Closed.
            if (_popup.IsCurrent(generation) && (menu.IsDisposed || !menu.Visible)) CompleteMenu();
        }
    }
    private void OnMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e) => CompleteMenu(
        e.CloseReason == ToolStripDropDownCloseReason.Keyboard || e.CloseReason == ToolStripDropDownCloseReason.ItemClicked);
    private void OnMenuDisposed(object? sender, EventArgs e) => CompleteMenu();
    private void CompleteMenu(bool returnShellFocus = false)
    {
        if (!_popup.Complete() || _disposed) return;
        _menu?.ReleaseAnchor();
        var restoreFocus = returnShellFocus && _menuHadForeground;
        _menuHadForeground = false;
        // Unlike TrackPopupMenu, ContextMenuStrip.Show returns before dismissal. Post
        // the Shell's benign task-switch message after Closed, not immediately after Show.
        PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL
        // Outside clicks keep focus in the window the user selected. Escape/item selection
        // can return focus to the tray; denied activation never steals someone else's focus.
        if (restoreFocus) ReturnFocus();
    }
    private void CloseMenu()
    {
        if (_menu is { IsDisposed: false, Visible: true } menu) menu.Close();
        CompleteMenu();
    }
    private void QueueMenuDismissal()
    {
        if (_popup.IsActive)
            PostMessage(Handle, DismissMenuMessage, new IntPtr(_popup.Generation), IntPtr.Zero);
    }
    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == DismissMenuMessage)
        {
            if (_popup.IsCurrent(message.WParam.ToInt32()) && _menu is { IsDisposed: false, Visible: true } menu)
                menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
        }
        else if ((message.Msg == 0x001C && message.WParam == IntPtr.Zero) || message.Msg == 0x001F)
        {
            // WM_ACTIVATEAPP(false) / WM_CANCELMODE: an event-driven safety net for
            // focus loss. Defer until Show/Close finishes and reject stale generations.
            QueueMenuDismissal();
            CloseHoverPopup(suppress: true);
        }
        else if (message.Msg is 0x007E or 0x001A or 0x02E0 or 0x001E or 0x031A)
        {
            // Display/DPI/settings/time/theme changes refresh native metrics without idle polling.
            if (message.Msg != 0x001E)
            {
                CloseMenu(); // Reopen against current screen geometry.
                EndHover();
            }
            EnvironmentChanged?.Invoke(this, EventArgs.Empty);
        }
        else if ((uint)message.Msg == _taskbarCreated)
        {
            CloseMenu();
            EndHover();
            _registration.Forget();
            ResetRegistrationRetry();
            Update();
        }
        else if ((uint)message.Msg == CallbackMessage)
        {
            var notification = (uint)message.LParam.ToInt64() & 0xFFFF;
            switch (notification)
            {
                case 0x0200: // WM_MOUSEMOVE: first entry triggers immediately.
                    OnHover();
                    break;
                case 0x0406: // NIN_POPUPOPEN may also be generated by keyboard selection.
                    OnHover(fromShell: true);
                    break;
                case 0x0407: // NIN_POPUPCLOSE: Explorer also reports tooltip departure.
                    if (_hoverSession.CloseFromShell(_hoverPopup is { Visible: true } openCard && openCard.Bounds.Contains(Cursor.Position))) EndHover();
                    else CheckPointer();
                    break;
                case 0x0203: // WM_LBUTTONDBLCLK
                    CloseHoverPopup(suppress: true);
                    DoubleClick?.Invoke(this, EventArgs.Empty);
                    break;
                case 0x0202: // WM_LBUTTONUP
                case 0x0400: // NIN_SELECT
                case 0x0401: // NIN_KEYSELECT
                    Selected?.Invoke(this, EventArgs.Empty);
                    break;
                case 0x0201: // WM_LBUTTONDOWN
                case 0x0204: // WM_RBUTTONDOWN
                case 0x0207: // WM_MBUTTONDOWN
                case 0x020B: // WM_XBUTTONDOWN
                    CloseHoverPopup(suppress: true);
                    break;
                case 0x007B: // WM_CONTEXTMENU, supports keyboard activation too.
                    if (ContextMenuStrip is not null)
                        ShowMenu();
                    break;
                case 0x0205 when !_registration.Version4: // WM_RBUTTONUP if Shell rejected version 4.
                    if (ContextMenuStrip is not null) ShowMenu();
                    break;
            }
        }
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Remove();
        ContextMenuStrip = null;
        _disposed = true;
        _hoverTimer.Dispose();
        _registrationTimer.Dispose();
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identity, out Rect rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
