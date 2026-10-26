using System.Diagnostics;
using System.Runtime.InteropServices;
using CodexQuota.Core;

namespace CodexQuota.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NativeTrayIcon _tray;
    private readonly TrayContextMenuStrip _menu;
    private readonly ToolStripMenuItem _refresh = new("立即刷新");
    private readonly ToolStripMenuItem _sourceMenu = new("数据来源");
    private readonly ToolStripMenuItem _themeMenu = new("主题");
    private readonly ToolStripMenuItem _themeWarning = new("主题设置有提示…") { Visible = false };
    private readonly Dictionary<ThemePreference, ToolStripMenuItem> _themeItems = new();
    private readonly ThemePreferencesStore _themePreferences = new();
    private ThemePreferenceResult _themePreference;
    private readonly ToolStripMenuItem _sourceWarning = new("数据来源设置有提示…") { Visible = false };
    private readonly Dictionary<QuotaDataSource, ToolStripMenuItem> _sourceItems = new();
    private Task _visibilityTask = Task.CompletedTask;
    private bool _visibilityAttempted;
    private string _visibilityDiagnostic = "trayVisibility status=NotAttempted";
    private string _visibilityExplanation = "启动时仅尝试一次；未生效时可从托盘箭头中手动拖出。";
    private readonly ToolStripMenuItem _startup = new("开机启动") { CheckOnClick = false };
    private readonly ToolStripMenuItem _startupWarning = new("开机启动设置有提示…") { Visible = false };
    private readonly StartupManager _startupManager;
    private string? _startupError;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _speedTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _resetTimer = new() { Interval = 1000 };
    private readonly ResetFeedClient _resetClient = new();
    private readonly ResetFeedPollPolicy _resetPolicy = new();
    private readonly ResetNotificationState _resetNotifications = new();
    private readonly ToolStripMenuItem _resetLatestLink = new("上次重置原文") { Visible = false };
    private readonly ToolStripMenuItem _resetForecastLink = new("预计重置原文") { Visible = false };
    private ResetFeedReadResult? _resetRead;
    private ResetFeedPresentation _resetPresentation = ResetFeedPresentation.Hidden;
    private bool _resetPresentationDirty = true, _resetUnread;
    private Task _resetTask = Task.CompletedTask;
    private long _resetAttempt;
    private ResetFeedDiagnosticSnapshot _resetDiagnostics = ResetFeedDiagnosticSnapshot.Initial;
    private readonly RolloutSpeedReader _speedReader = new();
    private readonly RolloutPollSchedule _speedSchedule = new();
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = TrayAnimation.FrameIntervalMilliseconds };
    private readonly Stopwatch _animationClock = Stopwatch.StartNew();
    private readonly TrayAnimation _animation = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _cliPath;
    private readonly SourcePreferencesStore _sourcePreferences = new();
    private SourcePreferenceResult _sourcePreference;
    private readonly SourceRequestGate _requests;
    private AutoQuotaSourceClient _sourceRouter;
    private QuotaDataSource? _activeSource;
    private QuotaDataSource? _quotaSource;
    private readonly QuotaCleanupPresentation _cleanup = new();
    private readonly SourceReadDiagnostics _connectionDiagnostics;
    private volatile bool _winnerInvalidated;
    private readonly SafeLog _log = new();
    private QuotaState _state = QuotaState.Initial;
    private QuotaState? _hoverState;
    private DateTimeOffset? _nextHoverChange;
    private Task _refreshTask = Task.CompletedTask;
    private Task _speedTask = Task.CompletedTask;
    private Task _sourceSwitchTask = Task.CompletedTask;
    private RolloutSpeedReadResult? _speedResult;
    private bool _speedUnavailable;
    private OutputSpeedView _speedView = OutputSpeedView.Create(null, DateTimeOffset.UtcNow, pendingWork: true);
    private RefreshPolicy _refreshPolicy = new();
    private Icon? _icon;
    private (int?, bool, int, long, bool)? _rendered;
    private bool _exiting;
    private readonly AutoUpdateController _updates;
    internal bool IsTrayReady => _tray.TryGetVisibilityIdentity(out _, out _);

    public TrayContext(string? cliPath)
    {
        // Auto probes installed official sources in a bounded sequence; manual choices stay fixed.
        // --codex-path only affects the concrete CLI candidate, never Desktop or VSCode.
        _cliPath = cliPath;
        _sourcePreference = _sourcePreferences.Load();
        _themePreference = _themePreferences.Load();
        WindowsAppTheme.Select(_themePreference.Preference);
        _requests = new(_sourcePreference.Source);
        _connectionDiagnostics = new(_requests);
        _sourceRouter = new(_sourcePreference.Source);
        _log.Record(_sourcePreference);
        _startupManager = new();
        _menu = new TrayContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            ShowItemToolTips = true,
            BackColor = TrayMenuPainter.Background,
            ForeColor = TrayMenuPainter.Foreground,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Renderer = new TrayMenuRenderer(),
            DropShadowEnabled = true
        };
        _menu.Items.Add(_refresh);
        var exit = new ToolStripMenuItem("退出");
        exit.Click += async (_, _) => await CloseAsync();
        _startup.Click += (_, _) =>
        {
            var result = _startupManager.SetEnabled(!_startup.Checked);
            ApplyStartupState(result);
            if (result.Error is { } error) MessageBox.Show(error, "Codex 开机启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        _startupWarning.Click += (_, _) =>
        {
            if (_startupError is { } error) MessageBox.Show(error, "Codex 开机启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        _sourceMenu.DropDown = new TraySubmenuStrip(_menu);
        foreach (var source in new[] { QuotaDataSource.Auto, QuotaDataSource.Cli, QuotaDataSource.VsCode, QuotaDataSource.Desktop })
        {
            var choice = new ToolStripMenuItem(source.DisplayName()) { CheckOnClick = false };
            choice.Click += (_, _) => _sourceSwitchTask = SelectSourceAsync(source);
            _sourceItems.Add(source, choice);
            _sourceMenu.DropDownItems.Add(choice);
        }
        if (_sourceMenu.DropDown is ToolStripDropDownMenu sourceChoices)
        {
            sourceChoices.ShowImageMargin = false;
            sourceChoices.ShowCheckMargin = true;
            sourceChoices.Renderer = _menu.Renderer;
            sourceChoices.Font = _menu.Font;
        }
        _sourceWarning.Click += (_, _) => ShowConnectionDiagnostics();
        _menu.Items.Add(_sourceMenu);
        _menu.Items.Add(_sourceWarning);
        _themeMenu.DropDown = new TraySubmenuStrip(_menu);
        foreach (var preference in new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark })
        {
            var choice = new ToolStripMenuItem(preference.DisplayName()) { CheckOnClick = false };
            choice.Click += (_, _) => SelectTheme(preference);
            _themeItems.Add(preference, choice);
            _themeMenu.DropDownItems.Add(choice);
        }
        _themeWarning.Click += (_, _) => MessageBox.Show(_themePreference.Explanation,
            "Codex 主题设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
        _menu.Items.Add(_themeMenu);
        _menu.Items.Add(_themeWarning);
        _menu.Items.Add(_startup);
        _menu.Items.Add(_startupWarning);
        _menu.Items.Add(new ToolStripSeparator());
        var diagnostics = new ToolStripMenuItem("查看连接诊断…");
        diagnostics.Click += (_, _) => ShowConnectionDiagnostics();
        _menu.Items.Add(diagnostics);
        _resetLatestLink.Click += (_, _) => OpenResetSource(forecast: false);
        _resetForecastLink.Click += (_, _) => OpenResetSource(forecast: true);
        _menu.Items.Add(_resetLatestLink);
        _menu.Items.Add(_resetForecastLink);
        var about = new ToolStripMenuItem("关于 / 许可");
        about.Click += (_, _) => { using var dialog = new LicenseDialog(); dialog.ShowDialog(); };
        _menu.Items.Add(about);
        _updates = new AutoUpdateController(_menu.Items, _cliPath, CloseAsync);
        _menu.Items.Add(exit);
        ApplyStartupState(_startupManager.Initialize());
        _refresh.Click += (_, _) => BeginRefresh();
        _tray = new NativeTrayIcon { ContextMenuStrip = _menu, Visible = false };
        _tray.DoubleClick += (_, _) => BeginRefresh();
        _tray.Selected += (_, _) => AcknowledgeForecast();
        _tray.LinkActivated += OnHoverLinkActivated;
        _updates.StateChanged += () =>
        {
            if (_exiting) return;
            _hoverState = null;
            Render();
        };
        _tray.HoverEntered += (_, _) => BeginRefresh(RefreshTrigger.Hover);
        _tray.Diagnostic += _log.Record;
        _tray.RegistrationUnavailable += async (_, _) =>
        {
            if (_exiting) return;
            MessageBox.Show("Windows 暂时无法显示额度托盘图标，重试仍未成功。\n关闭此提示后程序会退出，请稍后重新打开。", "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            await CloseAsync();
        };
        _menu.Opening += (_, _) => { ApplyTheme(); Render(); ApplyStartupState(_startupManager.Inspect()); };
        ApplyTheme();
        Render();
        _tray.Visible = true;
        _visibilityTask = AttemptTrayVisibilityAsync();
        _clock.Tick += (_, _) =>
        {
            _clock.Stop(); // One-shot wake: no idle 1 Hz repaint/poll loop.
            Render();
            BeginRefresh(RefreshTrigger.Automatic);
            if (!_refreshPolicy.InFlight) ScheduleNextWake();
        };
        _tray.EnvironmentChanged += (_, _) => { ApplyTheme(); _hoverState = null; Render(); if (!_refreshPolicy.InFlight) ScheduleNextWake(); };
        _animationTimer.Tick += (_, _) => Render();
        _speedTimer.Tick += (_, _) =>
        {
            _speedTimer.Stop();
            if (!_exiting && _speedTask.IsCompleted) _speedTask = PollSpeedAsync();
        };
        _speedTimer.Start(); // Separate incremental local reader, never triggered by hover.
        _resetTimer.Tick += (_, _) =>
        {
            _resetTimer.Stop();
            if (_exiting) return;
            Render(); // Exact forecast expiry/relative time updates require no HTTP request.
            if (_resetTask.IsCompleted && _resetPolicy.TryBegin(DateTimeOffset.UtcNow))
                _resetTask = PollResetFeedAsync();
            else ScheduleResetWake();
        };
        _resetTimer.Start(); // Independent of quota, hover, TPS, cleanup, and source selection.
        _clock.Start(); // First refresh after the message loop starts; all UI stays on its own thread.
    }

    private async Task PollResetFeedAsync()
    {
        var attempt = new ResetFeedAttemptDiagnostic(++_resetAttempt, DateTimeOffset.UtcNow);
        var clock = Stopwatch.StartNew();
        _resetDiagnostics = _resetDiagnostics with { Current = attempt };
        ResetFeedReadResult result;
        try { result = await _resetClient.FetchAsync(_stop.Token); }
        catch (Exception error)
        {
            result = new(ResetFeedReadStatus.NetworkError, Diagnostic: ResetFeedDiagnostic.Unexpected(error, clock.ElapsedMilliseconds));
        }
        if (_exiting) return;
        _resetRead = result; // Fail closed for this optional feature; never retain visible stale API data.
        _resetPresentationDirty = true;
        _resetPolicy.Complete(result, DateTimeOffset.UtcNow);
        _resetDiagnostics = new(null, attempt with { CompletedAt = DateTimeOffset.UtcNow, Result = result },
            _resetPolicy.NextPollAt);
        _hoverState = null;
        Render();
        ScheduleResetWake();
    }

    private void ScheduleResetWake()
    {
        if (_exiting) return;
        var now = DateTimeOffset.UtcNow;
        var due = _resetPolicy.NextPollAt;
        if (_resetPresentation.NextChange is { } change && change < due) due = change;
        _resetTimer.Interval = (int)Math.Max(100, Math.Min(int.MaxValue, Math.Ceiling((due - now).TotalMilliseconds)));
        _resetTimer.Start();
    }

    private void AcknowledgeForecast()
    {
        if (_exiting) return;
        var current = ResetFeedPresentation.Create(_resetRead, DateTimeOffset.UtcNow);
        if (current.Forecast is null || !_resetNotifications.IsUnread(current.EventIdentity)) return;
        _resetNotifications.Acknowledge(current.EventIdentity);
        _resetUnread = false;
        Render();
    }

    private void OpenResetSource(bool forecast)
    {
        var current = ResetFeedPresentation.Create(_resetRead, DateTimeOffset.UtcNow);
        var row = forecast ? current.Forecast : current.Latest;
        if (row?.SourceUrl is not { } url) return;
        OpenResetLink(new(row.EventIdentity, url, row.Description, 0, 0, 0, 0, forecast));
    }

    private async void OnHoverLinkActivated(HoverLinkRegion link)
    {
        if (_exiting) return;
        if (link.Url.Length == 0 && link.Key.StartsWith("update-", StringComparison.Ordinal))
        {
            await _updates.InstallAsync(link.Key.Substring("update-".Length));
            return;
        }
        OpenResetLink(link);
    }

    private void OpenResetLink(HoverLinkRegion link)
    {
        if (_exiting) return;
        var current = ResetFeedPresentation.Create(_resetRead, DateTimeOffset.UtcNow);
        // Revalidate both the current data and URL at activation, including expiry during a click.
        var credit = link.Url == ResetFeedUrlPolicy.SiteUrl && link.Key.StartsWith("credit-", StringComparison.Ordinal);
        var row = link.IsForecast ? current.Forecast : current.Latest;
        if (!current.IsVisible || (credit
            ? !(current.Latest is not null && link.Key == "credit-" + current.Latest.Label ||
                current.Forecast is not null && link.Key == "credit-" + current.Forecast.Label)
            : row is null || row.EventIdentity != link.Key || row.SourceUrl != link.Url ||
                !ResetFeedUrlPolicy.IsAllowedSource(link.Url))) return;
        try
        {
            Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true });
            if (!credit && link.IsForecast) AcknowledgeForecast();
        }
        catch (Exception) { /* Optional external link failure never interrupts quota/TPS or creates dialogs. */ }
    }

    private async Task PollSpeedAsync()
    {
        var canCatchUp = false;
        try
        {
            var result = await _speedReader.PollAsync(_stop.Token);
            if (_exiting) return;
            canCatchUp = result.CanCatchUp;
            _speedResult = result;
            _speedUnavailable = false;
            Render();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // No raw rollout lines, paths, model text, or exception payloads are logged.
            if (!_exiting)
            {
                _speedUnavailable = true;
                _speedView = OutputSpeedView.Create(_speedResult?.Sample, DateTimeOffset.UtcNow, unavailable: true);
                _hoverState = null;
                Render();
            }
        }
        finally
        {
            if (!_exiting) { _speedTimer.Interval = _speedSchedule.NextDelay(canCatchUp); _speedTimer.Start(); }
        }
    }

    private void ApplyStartupState(StartupResult result)
    {
        _startup.Enabled = _startupManager.IsAvailable;
        _startup.Text = _startupManager.IsAvailable ? "开机启动" : "开机启动（管理员版不可用）";
        _startup.Checked = _startupManager.IsAvailable && result.IsEnabled;
        _startup.ToolTipText = result.Error ?? "登录 Windows 后自动启动";
        _startupError = result.Error;
        _startupWarning.Visible = result.Error is not null;
    }

    private async Task SelectSourceAsync(QuotaDataSource source)
    {
        if (_exiting) return;
        if (source == _requests.Source)
        {
            // Selecting the current item can explicitly repair a failed/invalid save,
            // but must not reset the same source's rate-limit cooldown.
            if (_sourcePreference.Status is not (SourcePreferenceStatus.Loaded or SourcePreferenceStatus.Saved or SourcePreferenceStatus.Migrated))
            {
                _sourcePreference = _sourcePreferences.Save(source);
                _log.Record(_sourcePreference);
                Render();
            }
            return;
        }

        var change = _requests.BeginSwitch(source); // Invalidate first, then cancel the owned read.
        _connectionDiagnostics.BeginSelection(change);
        _sourceRouter = new(source);
        _activeSource = null;
        _quotaSource = null;
        _winnerInvalidated = false;
        _clock.Stop();
        StopAnimation();
        _sourcePreference = _sourcePreferences.Save(source);
        _log.Record(_sourcePreference);
        _state = QuotaState.Initial; // Clears quota, resets, credits, plan and timestamps together.
        _hoverState = null;
        _nextHoverChange = null;
        _refreshPolicy = new();
        Render();
        await change.Cleanup; // No new component can start before old process cleanup completes.
        if (_exiting || !_requests.CompleteSwitch(change)) return;
        _connectionDiagnostics.CompleteSelection(change);
        BeginRefresh(); // Only the newest source selection reaches this immediate fresh read.
    }

    private void BeginRefresh(RefreshTrigger trigger = RefreshTrigger.Manual)
    {
        if (_exiting || _cleanup.RefreshPaused || _requests.ReadsBlocked || _requests.SwitchPending || !_refreshPolicy.CanRequest(DateTimeOffset.UtcNow) ||
            !_requests.TryBeginRead(out var read)) return;
        if (!_refreshPolicy.TryBegin(DateTimeOffset.UtcNow, trigger)) { read!.Dispose(); return; }
        _connectionDiagnostics.Begin(read!, DateTimeOffset.UtcNow);
        _cleanup.BeginRead(read!);
        _clock.Stop();
        StopAnimation();
        Render();
        _refreshTask = RefreshAsync(read!, _refreshPolicy, _sourceRouter);
    }

    private void Record(SourceReadLease request, CliDiagnostic diagnostic) => request.TryApply(() =>
    {
        if (_connectionDiagnostics.Record(request, diagnostic)) _log.Record(diagnostic);
    });

    private void Record(SourceReadLease request, SourceBackendDiagnostic diagnostic) => request.TryApply(() =>
    {
        if (_connectionDiagnostics.Record(request, diagnostic)) _log.Record(diagnostic);
    });

    private void Record(SourceReadLease request, AutoSourceDiagnostic diagnostic) => request.TryApply(() =>
    {
        if (!_connectionDiagnostics.Record(request, diagnostic)) return;
        if (SourceMenuPresentation.InvalidatesCurrentWinner(diagnostic, _activeSource))
        {
            _winnerInvalidated = true;
            // The policy callback may run off-thread. Repaint an already-open menu
            // on its UI thread; recheck the generation when the queued work runs.
            try
            {
                if (_menu.IsHandleCreated)
                    _menu.BeginInvoke(new Action(() => { if (!_exiting) request.TryApply(Render); }));
            }
            catch (InvalidOperationException) { /* Closing/disposed menu; no stale UI update. */ }
        }
        _log.Record(diagnostic);
    });

    private async Task RefreshAsync(SourceReadLease request, RefreshPolicy policy, AutoQuotaSourceClient router)
    {
        // Capture on the UI thread. Cleanup callbacks run on the backend worker and can
        // outlive this source selection, but never this read's ownership lifetime.
        var uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        try
        {
            var read = await router.ReadAsync((source, token) =>
                new CodexQuotaClient(source, source.CliOverridePath(_cliPath),
                    diagnostic => Record(request, diagnostic), diagnostic => Record(request, diagnostic),
                    cleanupProgress: progress => uiContext.Post(_ => ObserveCleanup(request, progress), null),
                    cleanupStop: _stop.Token).ReadAsync(token),
                request.Token, diagnostic => Record(request, diagnostic));
            request.TryApply(() =>
            {
                if (_exiting) return;
                var now = DateTimeOffset.UtcNow;
                var resultSource = read.Status == ReadStatus.Ready ? router.ActiveSource : router.LastAttemptedSource;
                // Never carry another installation's numbers or animation into a new backend result.
                if (_quotaSource != resultSource) _state = QuotaState.Initial;
                _quotaSource = resultSource;
                _activeSource = router.ActiveSource;
                _winnerInvalidated = read.Status != ReadStatus.Ready;
                // A plan/window switch is a different measurement, not consumption of the old window.
                var previous = _state.IsStale(now) || _state.Quota?.WindowMinutes != read.Quota?.WindowMinutes ||
                    _state.Selection?.PlanType != read.Selection?.PlanType
                    ? null : _state.Quota?.DisplayPercent;
                _state = _state.Apply(read, _cleanup.ResultReadAt(request, read, resultSource, now));
                CompleteDiagnostics(request, read.Status,
                    read.Status == ReadStatus.Ready ? SourceReadOutcome.Succeeded : SourceReadOutcome.Failed,
                    now, read.Status == ReadStatus.Ready ? router.ActiveSource : null);
                _animation.Start(read.Status, previous, _state.Quota?.DisplayPercent,
                    _state.IsStale(now), _animationClock.Elapsed.TotalMilliseconds,
                    read.Status == ReadStatus.Ready && ClientAnimationsEnabled());
                _log.Record(read.Status);
            });
        }
        catch (BackendCleanupUnconfirmedException)
        {
            // Safety lifetime evidence is global, even if this old source was superseded.
            if (_connectionDiagnostics.BlockFurtherReads(request, DateTimeOffset.UtcNow) &&
                _connectionDiagnostics.Capture().LastCompleted is { } completed) _log.Record(completed);
            _log.RecordCleanupUnconfirmed();
            _cleanup.Interrupt(request, router.LastAttemptedSource);
            _clock.Stop();
            // A failure to confirm local process cleanup cannot erase a valid quota read,
            // and an old source's failure must never mutate the selected generation.
            request.TryApply(() => _state = _state with { Status = ReadStatus.Unavailable });
            _hoverState = null;
            StopAnimation();
            if (!_exiting) Render();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            CompleteDiagnostics(request, ReadStatus.Unavailable, SourceReadOutcome.Cancelled, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            request.TryApply(() =>
            {
                // Host exceptions may precede discovery. Do not invent a QuotaRead/backend event.
                var now = DateTimeOffset.UtcNow;
                CompleteDiagnostics(request, ReadStatus.Unavailable, SourceReadOutcome.UnexpectedFailure,
                    now, failure: SourceReadFailureDiagnostic.FromException(error));
                _state = _state.Apply(new(ReadStatus.Unavailable), now);
                StopAnimation();
                _log.Record(ReadStatus.Unavailable);
            });
        }
        finally
        {
            try
            {
                _cleanup.CompleteRead(request);
                request.TryApply(() =>
                {
                    // The captured policy and all UI callbacks belong to this generation only.
                    policy.Complete(_state.Status, DateTimeOffset.UtcNow);
                    if (!_exiting) { Render(); ScheduleNextWake(); }
                });
            }
            finally { request.Dispose(); } // Releases waiters even if a UI repaint fails.
        }
    }

    private void ObserveCleanup(SourceReadLease request, BackendCleanupProgress progress)
    {
        if (_exiting || !_cleanup.Observe(request, progress)) return;
        if (progress.State == BackendCleanupState.Pending)
        {
            _clock.Stop();
            StopAnimation();
            request.TryApply(() =>
            {
                _state = _cleanup.ApplyPendingRead(_state);
                if (_cleanup.HasFreshQuota) _quotaSource = progress.Source;
                _winnerInvalidated = true;
            });
        }
        // Even a superseded request still owns its process until confirmed. Render its
        // cleanup origin beside the newly selected policy, without applying its quota.
        _hoverState = null;
        Render();
    }

    private void CompleteDiagnostics(SourceReadLease request, ReadStatus status, SourceReadOutcome outcome,
        DateTimeOffset now, QuotaDataSource? successfulSource = null, SourceReadFailureDiagnostic? failure = null)
    {
        if (_connectionDiagnostics.Complete(request, status, outcome, now, successfulSource, failure) &&
            _connectionDiagnostics.Capture().LastCompleted is { } completed) _log.Record(completed);
    }

    private async Task AttemptTrayVisibilityAsync()
    {
        if (_visibilityAttempted) return;
        _visibilityAttempted = true; // One startup attempt; never rearmed by refresh or Explorer restart.
        try
        {
            await Task.Delay(500, _stop.Token); // One bounded settling delay, no polling.
            if (_exiting) return;
            if (!_tray.TryGetVisibilityIdentity(out var window, out var iconId))
            {
                _visibilityDiagnostic = "trayVisibility status=IconUnavailable";
                return;
            }
            _visibilityDiagnostic = "trayVisibility status=Attempting";
            var result = await TrayVisibilityClient.TryMakeVisibleAsync(window, iconId, _stop.Token);
            if (_exiting) return;
            _visibilityDiagnostic = result.ToSafeLogCode();
            _visibilityExplanation = result.Explanation;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            // Silent failure: never expose native messages, paths or other icon metadata.
            if (!_exiting) _visibilityDiagnostic = "trayVisibility status=UnexpectedFailure";
        }
    }

    private void SelectTheme(ThemePreference preference)
    {
        if (_exiting) return;
        // Apply for this session even if persistence fails; a warning makes that distinction explicit.
        WindowsAppTheme.Select(preference);
        _themePreference = _themePreferences.Save(preference);
        ApplyTheme();
        _hoverState = null;
        Render();
    }

    private void ApplyTheme()
    {
        WindowsAppTheme.Refresh();
        _menu.BackColor = SystemInformation.HighContrast ? SystemColors.Menu : TrayMenuPainter.SurfaceColor(WindowsAppTheme.IsDark);
        _menu.ForeColor = SystemInformation.HighContrast ? SystemColors.MenuText : TrayMenuPainter.InkColor(WindowsAppTheme.IsDark);
        foreach (var submenu in new[] { _sourceMenu.DropDown, _themeMenu.DropDown })
        {
            submenu.BackColor = _menu.BackColor;
            submenu.ForeColor = _menu.ForeColor;
            submenu.Invalidate(true);
        }
        foreach (var choice in _themeItems) choice.Value.Checked = choice.Key == _themePreference.Preference;
        _themeMenu.Text = "主题：" + _themePreference.Preference.DisplayName();
        _themeMenu.ToolTipText = _themePreference.Explanation;
        _themeWarning.Visible = _themePreference.IsWarning;
        _themeWarning.Text = _themePreference.Status == ThemePreferenceStatus.SaveFailed
            ? "主题仅本次生效，未保存…" : "主题设置有提示…";
        _menu.Invalidate(true);
    }

    private void Render()
    {
        if (_exiting) return;
        var now = DateTimeOffset.UtcNow;
        if (_resetPresentationDirty || _resetPresentation.NextChange is { } resetChange && now >= resetChange)
        {
            var updated = ResetFeedPresentation.Create(_resetRead, now);
            if (_resetPresentation.Latest != updated.Latest || _resetPresentation.Forecast != updated.Forecast)
                _hoverState = null;
            _resetPresentation = updated;
            _resetPresentationDirty = false;
            _resetUnread = updated.Forecast is not null && _resetNotifications.IsUnread(updated.EventIdentity);
            foreach (var entry in new[] { (Item: _resetLatestLink, Row: updated.Latest),
                (Item: _resetForecastLink, Row: updated.Forecast) })
            {
                entry.Item.Visible = entry.Row is not null;
                entry.Item.Enabled = entry.Row?.SourceUrl is not null;
                entry.Item.ToolTipText = entry.Row?.Description ?? "";
                entry.Item.AccessibleDescription = entry.Row?.Description ?? "";
                entry.Item.Text = entry.Row is { } row ? row.Label + "原文" : "";
            }
        }
        var resetPresentation = _resetPresentation;
        if (_speedResult is { } speedResult)
        {
            var speedView = OutputSpeedView.Create(speedResult.Sample, now,
                unavailable: _speedUnavailable || speedResult.HasReadErrors || speedResult.Status == RolloutSpeedReadStatus.Unavailable,
                historyLimited: speedResult.IsHistoryLimited, pendingWork: speedResult.PendingWork,
                modelMetadataStatus: speedResult.ModelMetadataStatus);
            if (speedView != _speedView) { _speedView = speedView; _hoverState = null; }
        }
        var stale = _state.IsStale(now);
        var percent = _state.Quota?.DisplayPercent;
        var switching = _requests.SwitchPending;
        foreach (var item in _sourceItems)
        {
            var presentation = SourceMenuPresentation.Create(item.Key, _requests.Source, _activeSource,
                _state, now, switching, _cleanup.RefreshPaused || _requests.ReadsBlocked, _winnerInvalidated);
            item.Value.Checked = presentation.Checked;
            item.Value.Text = presentation.Text;
            item.Value.ToolTipText = presentation.Explanation;
            item.Value.AccessibleDescription = presentation.Explanation;
        }
        _sourceMenu.Text = "数据来源：" + SourceDisplayText();
        _sourceMenu.ToolTipText = _cleanup.SourceExplanation(_requests.Source) + "\n实际成功来源：" +
            (_activeSource?.DisplayName() ?? "尚未找到") +
            (_cleanup.RefreshPaused && _quotaSource is { } quotaSource ? "\n已显示额度来源：" + quotaSource.DisplayName() : "") +
            "\n" + _sourcePreference.Explanation;
        _sourceWarning.Visible = _sourcePreference.IsWarning || _sourcePreference.Status == SourcePreferenceStatus.Migrated;
        _sourceWarning.Text = _sourcePreference.Status == SourcePreferenceStatus.Migrated
            ? "旧版来源已升级为自动…" : "数据来源设置有提示…";
        _sourceWarning.ToolTipText = _sourcePreference.Explanation;
        _refresh.Enabled = !_cleanup.RefreshPaused && !_requests.ReadsBlocked && !switching && _refreshPolicy.CanRequest(now);
        _refresh.Text = _cleanup.RefreshPaused ? _cleanup.Status : _requests.ReadsBlocked ? "查询清理未确认，请退出后重开" : switching ? "正在切换数据来源…" : _refreshPolicy.InFlight ? "正在刷新…" : now < _refreshPolicy.RateLimitCooldownUntil
            ? $"限流冷却至 {_refreshPolicy.RateLimitCooldownUntil.ToLocalTime():HH:mm:ss}" : "立即刷新";
        _refresh.ToolTipText = _cleanup.RefreshPaused ? _cleanup.SourceExplanation(_requests.Source) +
            (_requests.ReadsBlocked ? "\n清理确认已中断，请退出后重开" : "\n持续确认本程序启动的查询进程退出；确认后自动恢复") : "";
        var size = GetTrayIconSize();
        var frame = _animation.Sample(percent, stale, _animationClock.Elapsed.TotalMilliseconds);
        if (frame.IsActive)
        {
            _animationTimer.Interval = frame.NextDelayMilliseconds;
            if (!_animationTimer.Enabled) _animationTimer.Start();
        }
        else _animationTimer.Stop();
        var notification = _resetUnread || _updates.HasAvailableUpdate;
        var key = (percent, stale, size, frame.Key, notification);
        if (_rendered != key)
        {
            var raster = TrayIconRasterizer.RenderFrame(size, percent, frame.RingPercent, stale, frame.SweepPhase, notification);
            using var data = new MemoryStream(TrayIconRasterizer.EncodeIcoFrame(raster));
            using var source = new Icon(data, new Size(size, size));
            var replacement = (Icon)source.Clone();
            _tray.Icon = replacement;
            var previous = _icon;
            _icon = replacement;
            previous?.Dispose();
            _rendered = key;
        }
        if (!ReferenceEquals(_hoverState, _state) || _nextHoverChange is { } change && now >= change)
        {
            _hoverState = _state;
            _nextHoverChange = HoverCardModel.NextChange(_state, now);
            var hover = HoverCardModel.Create(_state, now, _speedView,
                _cleanup.RefreshPaused ? _cleanup.Status : null);
            if (_updates.Notice is { } updateNotice)
                hover = hover with
                {
                    UpdateNotice = updateNotice,
                    AccessibleText = hover.AccessibleText + "\n" + updateNotice.Text + "，" + updateNotice.ActionText
                };
            if (resetPresentation.IsVisible)
                hover = hover with
                {
                    ResetFeed = resetPresentation,
                    AccessibleText = hover.AccessibleText + "\n" +
                        string.Join("\n", new[] { resetPresentation.Latest, resetPresentation.Forecast }
                            .Where(row => row is not null).Select(row => row!.Label + "：" + row.Detail + "。" + row.Description)) +
                        "\n数据来源：Codex Resets。可从右键菜单打开原文。"
                };
            if (_cleanup.RefreshPaused)
                hover = hover with { AccessibleText = hover.AccessibleText + "\n" + _cleanup.SourceExplanation(_requests.Source) +
                    (_quotaSource is { } displayedQuotaSource ? "\n已显示额度来源：" + displayedQuotaSource.DisplayName() : "") };
            else if (_state.Status != ReadStatus.Ready)
                hover = hover with
                {
                    Status = SourceDisplayText() + " · " + SourceStatusText(),
                    AccessibleText = hover.AccessibleText
                        .Replace("未找到 Codex CLI", "未找到" + _requests.Source.DisplayName() + "组件")
                        .Replace("协议不兼容，请更新 CLI", _requests.Source.DisplayName() + "协议不兼容")
                        .Replace("请先登录 Codex", "请先在" + _requests.Source.DisplayName() + "中登录")
                };
            _tray.SetHoverContent(hover);
        }
        _tray.EnsureVisible();
    }

    private string SourceDisplayText() => _cleanup.RefreshPaused ? _requests.Source.DisplayName() : _requests.Source == QuotaDataSource.Auto
        ? "自动" + (_activeSource is { } source ? (_state.Status == ReadStatus.Ready ? "（" : "（上次：") + source.DisplayName() + "）" : "（尚未找到）")
        : _requests.Source.DisplayName();

    private string SourceStatusText() => _cleanup.RefreshPaused ? _cleanup.Status : _requests.ReadsBlocked ? "查询清理未确认，已停止后续查询，请退出后重开" : _requests.Source == QuotaDataSource.Auto &&
        _state.Status is not (ReadStatus.Ready or ReadStatus.Connecting or ReadStatus.RateLimited)
        ? "自动选择未找到可用额度，请查看连接诊断" : _state.Status switch
    {
        ReadStatus.CliMissing => "未找到" + _requests.Source.DisplayName() + "组件，请查看连接诊断",
        ReadStatus.NotSignedIn => "请先在" + _requests.Source.DisplayName() + "中登录",
        ReadStatus.CredentialsExpired => "登录已过期，请打开" + _requests.Source.DisplayName() + "更新登录",
        _ => QuotaState.StatusText(_state.Status)
    };

    private void ShowConnectionDiagnostics()
    {
        // Capture once. All round/status/source evidence below belongs to this immutable snapshot.
        // Opening this window does not discover a component or perform an account/network read.
        var snapshot = _connectionDiagnostics.Capture();
        var resetSnapshot = _resetDiagnostics;
        var version = typeof(TrayContext).Assembly.GetName().Version;
        var text = $"CodexQuotaTray {version}\r\n" +
            $"设置状态：{_sourcePreference.Explanation}\r\n已保存来源：{_sourcePreference.PersistedSourceDescription}\r\n{_sourcePreference.ToSafeLogCode()}\r\n" +
            "主题设置：" + _themePreference.Explanation + "\r\n" + _themePreference.ToSafeLogCode() + "\r\n" +
            _visibilityDiagnostic + "\r\n" + _visibilityExplanation + "\r\n" +
            SourceReadDiagnosticText.Format(snapshot) +
            "\r\n\r\n" + ResetFeedDiagnosticText.Format(resetSnapshot, _resetPresentation, DateTimeOffset.UtcNow) +
            "\r\n最近本地响应元数据：\r\n" + OutputMetadataDiagnostic.Format(_speedResult, _speedUnavailable) +
            "\r\n\r\n当前轮与最近完成轮分别显示；generation 和 round 标明归属。未记录阶段不推断为已执行。自动模式允许顺序尝试；手动选择不回退。仅含固定标签、数字版本、阶段耗时和错误码；不含账号、地址、路径、令牌或服务原文。";
        using var dialog = new Form
        {
            Text = "Codex 连接诊断", StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(820, 620), MinimumSize = new Size(480, 320),
            ShowInTaskbar = true
        };
        var content = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Text = text, Font = new Font("Microsoft YaHei UI", 9.5f)
        };
        var close = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36, DialogResult = DialogResult.OK };
        dialog.Controls.Add(content);
        dialog.Controls.Add(close);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        content.Select(0, 0);
        dialog.ShowDialog(_menu);
    }

    internal Task RequestHandoffExitAsync() => CloseAsync();

    private async Task CloseAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _updates.Dispose();
        _clock.Stop();
        _speedTimer.Stop();
        _resetTimer.Stop();
        StopAnimation();
        _requests.Dispose();
        _stop.Cancel();
        _tray.Visible = false;
        await Task.WhenAll(_refreshTask, _speedTask, _resetTask, _sourceSwitchTask);
        // COM is bounded in the child. Native process creation itself is an OS call
        // that cannot be force-interrupted; never let that experiment block UI exit.
        // The background launcher does not keep this process alive, and its private
        // kill-on-close job is owned by this process, not inherited by the helper.
        await Task.WhenAny(_visibilityTask, Task.Delay(7000));
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _updates.Dispose();
            _requests.Dispose();
            _stop.Cancel();
            _clock.Dispose();
            _speedTimer.Dispose();
            _resetTimer.Dispose();
            _resetClient.Dispose();
            // Normal CloseAsync already awaited this task. An exceptional direct Dispose
            // still leaves enumerator cleanup off the UI while cancellation unwinds the poll.
            _ = _speedTask.ContinueWith(_ => _speedReader.Dispose(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            _animationTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icon?.Dispose();
            var font = _menu.Font;
            _menu.Dispose();
            font.Dispose();
            _stop.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ScheduleNextWake()
    {
        if (_cleanup.RefreshPaused || _requests.ReadsBlocked) { _clock.Stop(); return; }
        if (_exiting || _requests.SwitchPending || _refreshPolicy.InFlight) return;
        var now = DateTimeOffset.UtcNow;
        var due = _refreshPolicy.NextAutomaticRefresh;
        if (HoverCardModel.NextChange(_state, now) is { } change && change < due) due = change;
        var delay = Math.Max(100, Math.Min(int.MaxValue, Math.Ceiling((due - now).TotalMilliseconds)));
        _clock.Interval = (int)delay;
        _clock.Start();
    }

    private void StopAnimation()
    {
        _animation.Stop();
        _animationTimer.Stop();
    }

    private static bool ClientAnimationsEnabled() =>
        !SystemParametersInfo(0x1042, 0, out var enabled, 0) || enabled != 0;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    private static int GetTrayIconSize()
    {
        var shell = FindWindow("Shell_TrayWnd", null);
        var dpi = shell == IntPtr.Zero ? 96u : GetDpiForWindow(shell);
        if (dpi == 0) dpi = 96;
        var pixels = GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON: exact native small-icon metric.
        if (pixels <= 0) pixels = (int)Math.Round(16 * dpi / 96d);
        return Math.Max(16, Math.Min(64, pixels));
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

}
