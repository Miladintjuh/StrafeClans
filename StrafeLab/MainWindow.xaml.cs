using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Data;
using StrafeLab.Models;
using StrafeLab.Services;

namespace StrafeLab;

public partial class MainWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE_SESSION = 9001;
    private const int HOTKEY_REMOVE_LAST_ATTEMPT = 9002;
    private const int HOTKEY_ARM_PEEK_ATTEMPT = 9003;
    private const uint MOD_NOREPEAT = 0x4000;
    private readonly PreferencesStore _preferencesStore = new();
    private readonly AppPreferences _preferences;
    private readonly SupabaseApiClient _supabase = new();

    private readonly AnalysisSettings _settings = new();
    private readonly HighResolutionClock _clock = new();
    private readonly StrafeAnalyzer _analyzer;
    private readonly SessionStore _store = new();
    private readonly ProgressStore _progressStore = new();
    private RawInputListener? _listener;
    private readonly ICollectionView _attemptsView;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _replayTimer;
    private bool _active;
    private bool _uiReady;
    private StrafeAttempt? _selectedAttempt;
    private StrafeAttempt? _replayAttempt;
    private double _traceZoom = 1.0;
    private double _tracePanX;
    private double _tracePanY;
    private double _traceLastBaseScale = 1.0;
    private bool _traceIsPanning;
    private Point _traceLastPanPoint;
    private double _replayStartedAtMs;
    private double _replaySlowdown = 1.0;
    private double _replayWindowStartMs;
    private double _replayWindowEndMs;
    private double _sessionStartMs;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastEndedAt;
    private HwndSource? _hotkeySource;
    private bool _stopSaveInProgress;
    private bool _hasSavedCurrentSession;
    private PeekModeView? _peekModeView;

    public MainWindow()
    {
        _preferences = _preferencesStore.Load();
        InitializeComponent();
        ApplyPreferencesToSettings();

        _analyzer = new StrafeAnalyzer(_settings);
        _attemptsView = CollectionViewSource.GetDefaultView(_analyzer.RecentAttempts);
        _attemptsView.Filter = AttemptViewFilter;
        AttemptsGrid.ItemsSource = _attemptsView;
        _uiReady = true;
        ApplyPreferencesToUi(registerHotkeys: false);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => RefreshStats();
        _timer.Start();

        _replayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _replayTimer.Tick += ReplayTimer_Tick;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        RefreshStats();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _listener = new RawInputListener(_clock);
        _listener.Attach(hwnd);
        _listener.InputEdge += OnInputEdge;
        RegisterGlobalHotkeys(hwnd);
        StatusText.Text = "Ready";
        _ = InitializeCloudAsync();
        RefreshStats();
    }

    private async Task InitializeCloudAsync()
    {
        try
        {
            await _supabase.TryRefreshOrClearAsync();
            if (_supabase.IsSignedIn)
            {
                try { await _supabase.RefreshAdminAccessAsync(); } catch { }
            }
            UpdateCloudButtons();
        }
        catch
        {
            UpdateCloudButtons();
        }

        if (!_supabase.IsSignedIn)
        {
            await Dispatcher.InvokeAsync(() => ShowLoginView(startup: true));
        }
    }

    private void OnInputEdge(InputEventRecord raw)
    {
        if (ModeHostBorder?.Visibility == Visibility.Visible && ModeHost.Content == _peekModeView) _peekModeView?.ReceiveRawInput(raw);

        if (!_active) return;

        var e = new InputEventRecord
        {
            WallTime = raw.WallTime,
            SessionTimeMs = raw.SessionTimeMs - _sessionStartMs,
            Code = raw.Code,
            Kind = raw.Kind,
            DeltaX = raw.DeltaX,
            DeltaY = raw.DeltaY
        };

        _analyzer.Add(e);
        if (e.Kind != InputKind.MouseMove)
        {
            TipText.Text = _analyzer.GetCoachingTip();
            AttemptsGrid.Items.Refresh();
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPeekModeVisible)
        {
            _peekModeView?.StartContinuousFromHeader();
            UpdatePrimarySessionButtons();
            return;
        }

        StartSession();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPeekModeVisible)
        {
            _peekModeView?.StopContinuousFromHeader();
            UpdatePrimarySessionButtons();
            return;
        }

        await StopAndSaveSessionAsync();
    }

    private void StartSession()
    {
        if (_stopSaveInProgress) return;

        if (ModeHostBorder.Visibility == Visibility.Visible && ModeHost.Content != _peekModeView)
        {
            HideModeHost("Returned to live trainer.");
        }

        ApplySettingsFromUi();
        _analyzer.Reset();
        _sessionStartMs = _clock.NowMs();
        _startedAt = DateTimeOffset.Now;
        _lastEndedAt = default;
        _active = true;
        _hasSavedCurrentSession = false;
        _selectedAttempt = null;
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();
        UpdateHeaderActionStates();
        UpdatePrimarySessionButtons();
        StatusText.Text = "Live";
        TipText.Text = "Session running. Use A/D, counter-strafe with the opposite key, move/correct your mouse, then click.";
        RefreshStats();
    }

    private async Task StopAndSaveSessionAsync()
    {
        if (_stopSaveInProgress) return;
        if (!_active && _analyzer.Events.Count == 0) return;

        _stopSaveInProgress = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;

        try
        {
            _active = false;
            StatusText.Text = "Saving";

            ApplySettingsFromUi();
            _analyzer.ApplyAutoJiggleTags(Math.Max(0, _clock.NowMs() - _sessionStartMs));
            var endedAt = DateTimeOffset.Now;
            _lastEndedAt = endedAt;
            var summary = _analyzer.BuildSummary(_startedAt == default ? endedAt : _startedAt, endedAt);
            var progress = _progressStore.UpdateFromSession(_analyzer.Attempts);
            summary.BestCleanStreak = progress.BestCleanStreak;
            await _store.SaveAsync(summary, _analyzer.Events, _analyzer.Attempts);
            _hasSavedCurrentSession = true;

            string syncSuffix = string.Empty;
            if (_supabase.IsSignedIn)
            {
                try
                {
                    await _supabase.UploadSessionSummaryAsync(summary);
                    syncSuffix = " and uploaded to Supabase";
                }
                catch (Exception uploadEx)
                {
                    syncSuffix = $". Cloud upload failed: {uploadEx.Message}";
                }
            }

            StatusText.Text = "Saved";
            TipText.Text = $"Saved session {summary.SessionId} to {_store.Root}{syncSuffix}";
            UpdateHeaderActionStates();
        }
        finally
        {
            _stopSaveInProgress = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            UpdatePrimarySessionButtons();
            RefreshStats();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        UnregisterGlobalHotkeys();
        _listener?.Dispose();
    }

    private void RegisterGlobalHotkeys(IntPtr hwnd)
    {
        _hotkeySource = HwndSource.FromHwnd(hwnd);
        _hotkeySource?.AddHook(HotkeyWndProc);

        uint f8Vk = HotkeyToVirtualKey(_preferences.PeekArmHotkey, 0x77);
        uint f9Vk = HotkeyToVirtualKey(_preferences.StartStopHotkey, 0x78);
        uint removeVk = HotkeyToVirtualKey(_preferences.RemoveLastHotkey, 0x51);

        bool f8Registered = RegisterHotKey(hwnd, HOTKEY_ARM_PEEK_ATTEMPT, MOD_NOREPEAT, f8Vk);
        bool f9Registered = RegisterHotKey(hwnd, HOTKEY_TOGGLE_SESSION, MOD_NOREPEAT, f9Vk);
        bool removeRegistered = RegisterHotKey(hwnd, HOTKEY_REMOVE_LAST_ATTEMPT, MOD_NOREPEAT, removeVk);

        if (!f8Registered || !f9Registered || !removeRegistered)
        {
            var failedHotkeys = new List<string>();
            if (!f8Registered) failedHotkeys.Add(_preferences.PeekArmHotkey);
            if (!f9Registered) failedHotkeys.Add(_preferences.StartStopHotkey);
            if (!removeRegistered) failedHotkeys.Add(_preferences.RemoveLastHotkey);
            string failed = string.Join(", ", failedHotkeys.Distinct());
            TipText.Text = $"Hotkey registration failed for {failed}. Another app may already be using it. Buttons still work.";
        }
    }

    private void UnregisterGlobalHotkeys()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_ARM_PEEK_ATTEMPT);
            UnregisterHotKey(hwnd, HOTKEY_TOGGLE_SESSION);
            UnregisterHotKey(hwnd, HOTKEY_REMOVE_LAST_ATTEMPT);
        }
        catch
        {
            // Ignore shutdown-time handle errors.
        }

        if (_hotkeySource != null)
        {
            _hotkeySource.RemoveHook(HotkeyWndProc);
            _hotkeySource = null;
        }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (id == HOTKEY_TOGGLE_SESSION)
        {
            handled = true;
            _ = ToggleSessionFromHotkeyAsync();
        }
        else if (id == HOTKEY_REMOVE_LAST_ATTEMPT)
        {
            handled = true;
            _ = RemoveLastAttemptFromHotkeyAsync();
        }
        else if (id == HOTKEY_ARM_PEEK_ATTEMPT)
        {
            handled = true;
            _ = ArmPeekAttemptFromHotkeyAsync();
        }

        return IntPtr.Zero;
    }


    private async Task ArmPeekAttemptFromHotkeyAsync()
    {
        if (!IsPeekModeVisible && !await EnsureCanSwitchModeAsync("Peek mode")) return;
        ShowPeekMode();
        _peekModeView?.ArmSingleAttemptFromHotkey();
        TipText.Text = $"{_preferences.PeekArmHotkey} armed one Peek mode rep.";
    }

    private async Task ToggleSessionFromHotkeyAsync()
    {
        if (_peekModeView != null && ModeHostBorder.Visibility == Visibility.Visible && ModeHost.Content == _peekModeView)
        {
            _peekModeView.ToggleContinuousSessionFromHotkey();
            TipText.Text = $"{_preferences.StartStopHotkey} toggled Peek mode continuous session.";
            return;
        }

        if (_active)
        {
            if (_preferences.PlayHotkeySounds) SystemSounds.Exclamation.Play();
            await StopAndSaveSessionAsync();
        }
        else
        {
            if (_preferences.PlayHotkeySounds) SystemSounds.Asterisk.Play();
            StartSession();
        }
    }

    private async Task RemoveLastAttemptFromHotkeyAsync()
    {
        var removed = _analyzer.RemoveLatestAttempt();
        if (removed == null)
        {
            if (_preferences.PlayHotkeySounds) SystemSounds.Beep.Play();
            TipText.Text = $"{_preferences.RemoveLastHotkey} pressed: no attempts to remove.";
            return;
        }

        if (_selectedAttempt == removed) _selectedAttempt = null;
        AttemptsGrid.SelectedItem = null;
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();
        if (_preferences.PlayHotkeySounds) SystemSounds.Hand.Play();
        TipText.Text = $"Removed latest attempt #{removed.Index} ({removed.Direction}).";

        if (!_active && _hasSavedCurrentSession)
        {
            ApplySettingsFromUi();
            var endedAt = _lastEndedAt == default ? DateTimeOffset.Now : _lastEndedAt;
            var startedAt = _startedAt == default ? endedAt : _startedAt;
            var summary = _analyzer.BuildSummary(startedAt, endedAt);
            await _store.SaveAsync(summary, _analyzer.Events, _analyzer.Attempts);
            TipText.Text = $"Removed latest attempt #{removed.Index} and updated saved session {summary.SessionId}.";
        }

        RefreshStats();
    }


    private async void PeekModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPeekModeVisible)
        {
            if (!await EnsureCanSwitchModeAsync("main trainer")) return;
            HidePeekMode();
            return;
        }

        if (!await EnsureCanSwitchModeAsync("Peek mode")) return;
        ShowPeekMode();
    }

    private void ShowPeekMode()
    {
        if (_peekModeView == null)
        {
            _peekModeView = new PeekModeView(_preferences);
            _peekModeView.RequestLiveTrainerView += async () =>
            {
                if (await EnsureCanSwitchModeAsync("main trainer")) HidePeekMode();
            };
            _peekModeView.StateChanged += () => Dispatcher.Invoke(UpdatePrimarySessionButtons);
        }
        else
        {
            _peekModeView.ApplyPreferences(_preferences);
        }

        ShowModeHost(_peekModeView);
        UpdatePrimarySessionButtons();
        TipText.Text = $"Peek mode is open. {_preferences.StartStopHotkey} starts/stops continuous peek recording; {_preferences.PeekArmHotkey} arms one single rep.";
    }

    private void HidePeekMode() => HideModeHost("Returned to the live counter-strafe trainer.");

    private bool HasRunningSession => _active || (_peekModeView?.IsActive == true);

    private async Task<bool> EnsureCanSwitchModeAsync(string selectedMode)
    {
        if (!HasRunningSession) return true;

        string choice = _preferences.ModeSwitchSessionChoice ?? "Ask";
        if (string.Equals(choice, "AutoEnd", StringComparison.OrdinalIgnoreCase))
        {
            await EndCurrentSessionForModeSwitchAsync();
            return true;
        }

        if (string.Equals(choice, "Stay", StringComparison.OrdinalIgnoreCase))
        {
            TipText.Text = "Session is running. Mode switch cancelled by remembered choice.";
            return false;
        }

        var decision = ShowModeSwitchPrompt(selectedMode);
        if (decision.Remember)
        {
            _preferences.ModeSwitchSessionChoice = decision.EndSession ? "AutoEnd" : "Stay";
            _preferencesStore.Save(_preferences);
        }

        if (!decision.EndSession)
        {
            TipText.Text = "Session is running. Finish or stop it before switching modes.";
            return false;
        }

        await EndCurrentSessionForModeSwitchAsync();
        return true;
    }

    private async Task EndCurrentSessionForModeSwitchAsync()
    {
        if (_peekModeView?.IsActive == true)
        {
            _peekModeView.StopContinuousFromHeader();
        }

        if (_active)
        {
            await StopAndSaveSessionAsync();
        }

        UpdatePrimarySessionButtons();
    }

    private (bool EndSession, bool Remember) ShowModeSwitchPrompt(string selectedMode)
    {
        var dialog = new Window
        {
            Title = "StrafeLab",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (Brush)FindResource("WindowBgBrush")
        };

        var remember = new CheckBox
        {
            Content = "Remember choice",
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 12, 0, 0)
        };

        bool endSession = false;
        var yes = new Button { Content = "Yes", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "No", MinWidth = 84, Background = (Brush)FindResource("SoftBrush") };
        yes.Click += (_, _) => { endSession = true; dialog.DialogResult = true; };
        no.Click += (_, _) => { endSession = false; dialog.DialogResult = false; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        var panel = new StackPanel { Margin = new Thickness(22), Width = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Session is running. Do you want to end the session and open {selectedMode}?",
            Foreground = (Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "This prevents the main trainer and Peek mode from recording at the same time.",
            Foreground = (Brush)FindResource("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        panel.Children.Add(remember);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        bool? result = dialog.ShowDialog();
        return (result == true && endSession, remember.IsChecked == true);
    }

    private void ShowModeHost(object content, bool fullWindow = false)
    {
        SetStartupWelcomeChromeHidden(fullWindow);
        Grid.SetRow(ModeHostBorder, fullWindow ? 0 : 1);
        Grid.SetRowSpan(ModeHostBorder, fullWindow ? 3 : 2);
        ModeHostBorder.Background = (Brush)FindResource(fullWindow ? "WindowBgBrush" : "BgBrush");
        ModeHost.Content = content;
        ModeHostBorder.Visibility = Visibility.Visible;
        Panel.SetZIndex(ModeHostBorder, fullWindow ? 100 : 10);
    }

    private void HideModeHost(string? message = null)
    {
        ModeHostBorder.Visibility = Visibility.Collapsed;
        ModeHost.Content = null;
        Grid.SetRow(ModeHostBorder, 1);
        Grid.SetRowSpan(ModeHostBorder, 2);
        Panel.SetZIndex(ModeHostBorder, 10);
        SetStartupWelcomeChromeHidden(false);
        UpdatePrimarySessionButtons();
        if (!string.IsNullOrWhiteSpace(message)) TipText.Text = message;
    }

    private void SetStartupWelcomeChromeHidden(bool hidden)
    {
        var visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        HeaderChrome.Visibility = visibility;
        MetricsChrome.Visibility = visibility;
        MainChrome.Visibility = visibility;
    }

    private bool IsPeekModeVisible => _peekModeView != null && ModeHostBorder.Visibility == Visibility.Visible && ModeHost.Content == _peekModeView;

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_supabase.IsSignedIn)
        {
            ShowProfileView();
            return;
        }

        ShowLoginView(startup: false);
    }

    private void ShowLoginView(bool startup)
    {
        var view = new LoginView(_supabase, startupWelcome: startup);
        view.BackRequested += () => HideModeHost(startup ? "Using StrafeLab locally." : "Returned from account.");
        view.LocalOnlyRequested += () =>
        {
            UpdateCloudButtons();
            UpdateHeaderActionStates();
            HideModeHost("Local-only mode is active for this run. You can still sign in later from Account.");
        };
        view.AuthChanged += async () =>
        {
            if (_supabase.IsSignedIn)
            {
                try { await _supabase.RefreshAdminAccessAsync(); } catch { }
            }
            UpdateCloudButtons();
            UpdateHeaderActionStates();
            if (_supabase.IsSignedIn)
            {
                if (startup)
                {
                    await Dispatcher.InvokeAsync(() => HideModeHost("Ready. Start a session when you are set."));
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => ShowProfileView());
                }
            }
        };
        ShowModeHost(view, fullWindow: startup);
        TipText.Text = startup
            ? "Choose how you want to use StrafeLab."
            : "Sign in, create an account, or use StrafeLab locally.";
    }

    private void ShowProfileView(string? username = null)
    {
        var view = new ProfileView(_supabase, username);
        view.BackRequested += () => HideModeHost("Returned from profile.");
        view.AuthChanged += async () =>
        {
            if (_supabase.IsSignedIn)
            {
                try { await _supabase.RefreshAdminAccessAsync(); } catch { }
            }
            UpdateCloudButtons();
            UpdateHeaderActionStates();
        };
        ShowModeHost(view);
        TipText.Text = string.IsNullOrWhiteSpace(username) ? "Profile is open." : $"Profile @{username} is open.";
    }

    private void ClansButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_supabase.IsSignedIn)
        {
            AccountButton_Click(sender, e);
            return;
        }

        var view = new ClanView(_supabase, () => _store.LoadSummaries());
        view.BackRequested += () => HideModeHost("Returned from clans.");
        view.ProfileRequested += username => ShowProfileView(username);
        ShowModeHost(view);
        TipText.Text = "Clan page is open.";
    }

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_supabase.IsSignedIn)
        {
            AccountButton_Click(sender, e);
            return;
        }
        if (!_supabase.CanViewAdmin)
        {
            MessageBox.Show(this, "The admin page is only available to the admin and moderators.", "StrafeLab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var view = new AdminView(_supabase);
        view.BackRequested += () => HideModeHost("Returned from admin stats.");
        ShowModeHost(view);
        TipText.Text = "Admin stats are open.";
    }

    private void OpenConclusionsButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        double currentSessionMs = _active
            ? Math.Max(0, _clock.NowMs() - _sessionStartMs)
            : (_analyzer.Events.Count == 0 ? 0 : _analyzer.Events[^1].SessionTimeMs);
        _analyzer.ApplyAutoJiggleTags(currentSessionMs);

        var attempts = _analyzer.RecentAttempts
            .Reverse()
            .Where(a => _analyzer.IsAttemptIncludedInStats(a))
            .ToList();

        if (attempts.Count == 0)
        {
            MessageBox.Show(this, "No included attempts are available for conclusions. Check your filters or record a session first.", "StrafeLab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var view = new ConclusionsView(attempts, _settings);
        view.BackRequested += () => HideModeHost("Returned from conclusions.");
        ShowModeHost(view);
        TipText.Text = "Conclusions are open in the main window.";
    }

    private void HeaderConclusionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPeekModeVisible)
        {
            _peekModeView?.ShowConclusionsFromHeader();
            UpdatePrimarySessionButtons();
            return;
        }

        OpenConclusionsButton_Click(sender, e);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var view = new SettingsView(_preferences);
        view.BackRequested += () => HideModeHost("Returned from settings.");
        view.PreferencesApplied += prefs =>
        {
            _preferencesStore.Save(prefs);
            ApplyPreferencesToUi(registerHotkeys: true);
            _peekModeView?.ApplyPreferences(_preferences);
            UpdateHeaderActionStates();
            RefreshStats();
            TipText.Text = "Settings applied.";
        };
        ShowModeHost(view);
    }

    private void ReportsButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        var view = new ReportsView(() => _analyzer.RecentAttempts.Reverse().Where(a => _analyzer.IsAttemptIncludedInStats(a)).ToList(), _settings);
        view.BackRequested += () => HideModeHost("Returned from reports.");
        ShowModeHost(view);
        TipText.Text = "Reports are open in the main window.";
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true });
    }

    private void FocusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        RefreshStats();
    }

    private void TraceOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        RefreshTraceCanvas();
    }

    private void TraceZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        _traceZoom = 1.0;
        _tracePanX = 0;
        _tracePanY = 0;
        RefreshTraceCanvas();
    }

    private void TraceCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TraceCanvas == null) return;
        double width = Math.Max(TraceCanvas.ActualWidth, 1);
        double height = Math.Max(TraceCanvas.ActualHeight, 1);
        double baseScale = Math.Max(_traceLastBaseScale, 0.0001);
        double oldZoom = _traceZoom;
        double oldScale = baseScale * oldZoom;
        Point cursor = e.GetPosition(TraceCanvas);
        double worldX = (cursor.X - width / 2.0 - _tracePanX) / oldScale;
        double worldY = (cursor.Y - height / 2.0 - _tracePanY) / oldScale;

        double zoomFactor = e.Delta > 0 ? 1.18 : 1 / 1.18;
        _traceZoom = Math.Clamp(_traceZoom * zoomFactor, 0.25, 30.0);
        double newScale = baseScale * _traceZoom;
        _tracePanX = cursor.X - width / 2.0 - worldX * newScale;
        _tracePanY = cursor.Y - height / 2.0 - worldY * newScale;
        e.Handled = true;
        RefreshTraceCanvas();
    }

    private void TraceCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _traceIsPanning = true;
        _traceLastPanPoint = e.GetPosition(TraceCanvas);
        TraceCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void TraceCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _traceIsPanning = false;
        TraceCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void TraceCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_traceIsPanning) return;
        Point p = e.GetPosition(TraceCanvas);
        _tracePanX += p.X - _traceLastPanPoint.X;
        _tracePanY += p.Y - _traceLastPanPoint.Y;
        _traceLastPanPoint = p;
        RefreshTraceCanvas();
    }

    private void TraceCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_traceIsPanning) return;
        _traceIsPanning = false;
        TraceCanvas.ReleaseMouseCapture();
    }

    private void AttemptFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        RefreshStats();
    }

    private void AttemptsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _selectedAttempt = AttemptsGrid.SelectedItem as StrafeAttempt;
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();
        RefreshTraceCanvas();
    }

    private async void AttemptsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        var selected = AttemptsGrid.SelectedItems.OfType<StrafeAttempt>().ToList();
        if (selected.Count == 0) return;

        int removed = _analyzer.RemoveAttempts(selected);
        if (removed == 0) return;

        e.Handled = true;
        if (_selectedAttempt is not null && selected.Contains(_selectedAttempt)) _selectedAttempt = null;
        AttemptsGrid.SelectedItem = null;
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();

        if (!_active && _hasSavedCurrentSession)
        {
            ApplySettingsFromUi();
            var endedAt = _lastEndedAt == default ? DateTimeOffset.Now : _lastEndedAt;
            var startedAt = _startedAt == default ? endedAt : _startedAt;
            var summary = _analyzer.BuildSummary(startedAt, endedAt);
            await _store.SaveAsync(summary, _analyzer.Events, _analyzer.Attempts);
        }

        TipText.Text = removed == 1 ? "Deleted selected attempt." : $"Deleted {removed} selected attempts.";
        RefreshStats();
    }

    private void ReplaySpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        double slowdown = 1.0;
        if (button.Tag is string tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            slowdown = Math.Max(1.0, parsed);
        }

        StartReplay(slowdown);
    }

    private void ReplayStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_replayAttempt == null)
        {
            StopReplay(resetToSelectedStart: true);
            return;
        }

        double cursorMs = GetCurrentReplayAttemptTimeMs();
        _replayTimer.Stop();
        UpdateReplayVisual(Math.Clamp(cursorMs, _replayWindowStartMs, _replayWindowEndMs), isPlaying: false);
    }

    private void ReplayTimeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (_replayAttempt != null)
        {
            double cursorMs = _replayTimer.IsEnabled
                ? GetCurrentReplayAttemptTimeMs()
                : _replayWindowStartMs;
            UpdateReplayVisual(cursorMs, _replayTimer.IsEnabled);
        }
        else
        {
            ResetReplayForSelectedAttempt();
        }
    }

    private void StartReplay(double slowdown)
    {
        if (_selectedAttempt == null || !_selectedAttempt.HasClick)
        {
            ReplayStatusText.Text = "Select a click-confirmed attempt first.";
            return;
        }

        _replayAttempt = _selectedAttempt;
        _replaySlowdown = Math.Max(1.0, slowdown);
        SetReplayWindow(_replayAttempt);
        _replayStartedAtMs = _clock.NowMs();
        _replayTimer.Start();
        UpdateReplayVisual(_replayWindowStartMs, isPlaying: true);
    }

    private void StopReplay(bool resetToSelectedStart)
    {
        _replayTimer.Stop();
        if (resetToSelectedStart)
        {
            ResetReplayForSelectedAttempt();
        }
    }

    private void ResetReplayForSelectedAttempt()
    {
        if (ReplayTimelineCanvas == null) return;

        _replayAttempt = _selectedAttempt;
        if (_replayAttempt == null || !_replayAttempt.HasClick)
        {
            ReplayTimelineCanvas.Children.Clear();
            SetKeyVisual(KeyABox, KeyAStateText, isDown: false, isWarning: false, isShot: false);
            SetKeyVisual(KeyDBox, KeyDStateText, isDown: false, isWarning: false, isShot: false);
            SetKeyVisual(KeyM1Box, KeyM1StateText, isDown: false, isWarning: false, isShot: false);
            ReplayGuidanceText.Text = "Target timing tips appear here when you select an attempt.";
            ReplayStatusText.Text = "Select a click-confirmed attempt to replay its key timing.";
            return;
        }

        SetReplayWindow(_replayAttempt);
        UpdateReplayVisual(_replayWindowStartMs, isPlaying: false);
    }

    private void ReplayTimer_Tick(object? sender, EventArgs e)
    {
        if (_replayAttempt == null)
        {
            _replayTimer.Stop();
            return;
        }

        double cursorMs = GetCurrentReplayAttemptTimeMs();
        if (cursorMs >= _replayWindowEndMs)
        {
            cursorMs = _replayWindowEndMs;
            _replayTimer.Stop();
        }

        UpdateReplayVisual(cursorMs, _replayTimer.IsEnabled);
    }

    private double GetCurrentReplayAttemptTimeMs()
    {
        double elapsedRealMs = Math.Max(0, _clock.NowMs() - _replayStartedAtMs);
        return _replayWindowStartMs + elapsedRealMs / Math.Max(1.0, _replaySlowdown);
    }

    private void SetReplayWindow(StrafeAttempt attempt)
    {
        double clickMs = attempt.ClickTimeMs ?? Math.Max(attempt.ReleaseTimeMs, attempt.OppositeDownTimeMs) + 120;
        double firstEventMs = Math.Min(attempt.ReleaseTimeMs, Math.Min(attempt.OppositeDownTimeMs, clickMs));
        double lastEventMs = Math.Max(attempt.ReleaseTimeMs, Math.Max(attempt.OppositeDownTimeMs, clickMs));
        _replayWindowStartMs = Math.Max(0, firstEventMs - 80);
        _replayWindowEndMs = Math.Max(lastEventMs + 120, _replayWindowStartMs + 220);
    }

    private void UpdateReplayVisual(double cursorMs, bool isPlaying)
    {
        if (_replayAttempt == null) return;
        var attempt = _replayAttempt;

        bool fromKeyDown = cursorMs < attempt.ReleaseTimeMs;
        bool toKeyDown = cursorMs >= attempt.OppositeDownTimeMs;
        bool m1Down = attempt.ClickTimeMs.HasValue && cursorMs >= attempt.ClickTimeMs.Value && cursorMs <= attempt.ClickTimeMs.Value + 60;

        bool aDown = (attempt.FromKey == "A" && fromKeyDown) || (attempt.ToKey == "A" && toKeyDown);
        bool dDown = (attempt.FromKey == "D" && fromKeyDown) || (attempt.ToKey == "D" && toKeyDown);
        bool overlapNow = aDown && dDown;
        bool movingShotNow = m1Down && (aDown || dDown);

        SetKeyVisual(KeyABox, KeyAStateText, aDown, overlapNow || (movingShotNow && aDown), isShot: false);
        SetKeyVisual(KeyDBox, KeyDStateText, dDown, overlapNow || (movingShotNow && dDown), isShot: false);
        SetKeyVisual(KeyM1Box, KeyM1StateText, m1Down, isWarning: movingShotNow, isShot: m1Down);

        DrawReplayTimeline(attempt, cursorMs);

        string phase = DescribeReplayPhase(attempt, cursorMs);
        string running = isPlaying
            ? (_replaySlowdown <= 1.01 ? "Playing 1x" : $"Playing {_replaySlowdown:0}x slow")
            : "Paused";
        ReplayGuidanceText.Text = BuildReplayGuidanceText(attempt);
        ReplayStatusText.Text = $"{running} · #{attempt.Index} {attempt.Direction} · counter delay {attempt.CounterDelayMs:+0.0;-0.0;0.0} ms · {phase}";
    }

    private string BuildReplayGuidanceText(StrafeAttempt attempt)
    {
        string counterTarget = $"Best movement handoff: release {attempt.FromKey} and press {attempt.ToKey} with no overlap, ideally about {_settings.IdealCounterMinMs:0.#} ms later (allowed {_settings.IdealCounterMinMs:0.#}-{_settings.IdealCounterMaxMs:0.#} ms).";
        string clickTarget = attempt.HasClick
            ? $" Best shot timing: click about {_settings.IdealClickMinAfterCounterMs:0.#}-{_settings.IdealClickMaxAfterCounterMs:0.#} ms after the counter key." 
            : string.Empty;

        if (attempt.CounterDelayMs < 0)
        {
            return counterTarget + clickTarget + $" This attempt overlapped by {Math.Abs(attempt.CounterDelayMs):0.0} ms, so the counter key was pressed before the original key was fully released.";
        }

        double slowerBy = Math.Max(0, attempt.CounterDelayMs - _settings.IdealCounterMinMs);
        string slowerText = slowerBy <= 0.05
            ? "This attempt hit the fastest possible movement handoff."
            : $"This attempt was {slowerBy:0.0} ms slower than the fastest clean handoff.";

        if (attempt.ClickFromCounterMs.HasValue)
        {
            return counterTarget + clickTarget + $" Actual click timing: {attempt.ClickFromCounterMs.Value:0.0} ms after the counter key. {slowerText}";
        }

        return counterTarget + clickTarget + " " + slowerText;
    }

    private static void SetKeyVisual(Border box, TextBlock label, bool isDown, bool isWarning, bool isShot)
    {
        if (isShot)
        {
            box.Background = isWarning ? BrushFromRgb(76, 58, 20) : BrushFromRgb(0, 86, 110);
            box.BorderBrush = isWarning ? BrushFromRgb(255, 206, 69) : BrushFromRgb(0, 224, 255);
            label.Text = isWarning ? "moving shot" : "click";
            return;
        }

        if (isDown && isWarning)
        {
            box.Background = BrushFromRgb(76, 58, 20);
            box.BorderBrush = BrushFromRgb(255, 206, 69);
            label.Text = "down / warning";
            return;
        }

        if (isDown)
        {
            box.Background = BrushFromRgb(14, 75, 58);
            box.BorderBrush = BrushFromRgb(56, 217, 150);
            label.Text = "down";
            return;
        }

        box.Background = BrushFromRgb(17, 24, 39);
        box.BorderBrush = BrushFromRgb(51, 65, 85);
        label.Text = "up";
    }

    private void DrawReplayTimeline(StrafeAttempt attempt, double cursorMs)
    {
        double width = Math.Max(ReplayTimelineCanvas.ActualWidth, 360);
        double height = Math.Max(ReplayTimelineCanvas.ActualHeight, 58);
        ReplayTimelineCanvas.Children.Clear();

        double left = 18;
        double right = Math.Max(left + 20, width - 18);
        double laneH = 9;
        double aY = 10;
        double dY = 24;
        double mY = 38;

        var baseBrush = new SolidColorBrush(Color.FromArgb(110, 148, 163, 184));
        ReplayTimelineCanvas.Children.Add(new Line { X1 = left, Y1 = height / 2, X2 = right, Y2 = height / 2, Stroke = baseBrush, StrokeThickness = 1 });

        // Coaching bands: where the counter key and shot would be considered ideal.
        DrawReplayBand(attempt.ReleaseTimeMs + _settings.IdealCounterMinMs, attempt.ReleaseTimeMs + _settings.IdealCounterMaxMs, 0, height, new SolidColorBrush(Color.FromArgb(24, 56, 217, 150)));
        if (attempt.ClickTimeMs.HasValue)
        {
            DrawReplayBand(attempt.OppositeDownTimeMs + _settings.IdealClickMinAfterCounterMs, attempt.OppositeDownTimeMs + _settings.IdealClickMaxAfterCounterMs, mY - 3, laneH + 6, new SolidColorBrush(Color.FromArgb(30, 0, 224, 255)));
        }

        if (attempt.CounterDelayMs < 0)
        {
            DrawReplayBand(attempt.OppositeDownTimeMs, attempt.ReleaseTimeMs, 0, height, new SolidColorBrush(Color.FromArgb(44, 255, 206, 69)));
        }
        else if (attempt.CounterDelayMs > _settings.IdealCounterMaxMs)
        {
            DrawReplayBand(attempt.ReleaseTimeMs, attempt.OppositeDownTimeMs, 0, height, new SolidColorBrush(Color.FromArgb(40, 255, 92, 122)));
        }
        else if (attempt.CounterDelayMs > 0)
        {
            DrawReplayBand(attempt.ReleaseTimeMs, attempt.OppositeDownTimeMs, 0, height, new SolidColorBrush(Color.FromArgb(26, 124, 92, 255)));
        }

        if (attempt.FromKey == "A")
            DrawReplayInterval(_replayWindowStartMs, attempt.ReleaseTimeMs, aY, laneH, BrushFromRgb(56, 217, 150));
        else
            DrawReplayInterval(_replayWindowStartMs, attempt.ReleaseTimeMs, dY, laneH, BrushFromRgb(56, 217, 150));

        if (attempt.ToKey == "A")
            DrawReplayInterval(attempt.OppositeDownTimeMs, _replayWindowEndMs, aY, laneH, BrushFromRgb(0, 224, 255));
        else
            DrawReplayInterval(attempt.OppositeDownTimeMs, _replayWindowEndMs, dY, laneH, BrushFromRgb(0, 224, 255));

        if (attempt.ClickTimeMs.HasValue)
        {
            DrawReplayInterval(attempt.ClickTimeMs.Value, Math.Min(attempt.ClickTimeMs.Value + 60, _replayWindowEndMs), mY, laneH, BrushFromRgb(255, 255, 255));
        }

        DrawReplayMarker(attempt.ReleaseTimeMs, $"{attempt.FromKey} up", BrushFromRgb(255, 206, 69));
        DrawReplayMarker(attempt.OppositeDownTimeMs, $"{attempt.ToKey} down", BrushFromRgb(0, 224, 255));
        if (attempt.ClickTimeMs.HasValue) DrawReplayMarker(attempt.ClickTimeMs.Value, "M1", BrushFromRgb(255, 255, 255));

        double x = ReplayX(cursorMs);
        ReplayTimelineCanvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = height, Stroke = BrushFromRgb(255, 92, 122), StrokeThickness = 2 });

        void DrawReplayBand(double startMs, double endMs, double y, double h, Brush brush)
        {
            double x1 = ReplayX(startMs);
            double x2 = ReplayX(endMs);
            if (x2 < x1) (x1, x2) = (x2, x1);
            var band = new Rectangle
            {
                Width = Math.Max(2, x2 - x1),
                Height = h,
                Fill = brush
            };
            Canvas.SetLeft(band, x1);
            Canvas.SetTop(band, y);
            ReplayTimelineCanvas.Children.Add(band);
        }

        void DrawReplayInterval(double startMs, double endMs, double y, double h, Brush brush)
        {
            double x1 = ReplayX(startMs);
            double x2 = ReplayX(endMs);
            if (x2 < x1) (x1, x2) = (x2, x1);
            var rect = new Rectangle
            {
                Width = Math.Max(2, x2 - x1),
                Height = h,
                RadiusX = 4,
                RadiusY = 4,
                Fill = brush,
                Opacity = 0.75
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, y);
            ReplayTimelineCanvas.Children.Add(rect);
        }

        void DrawReplayMarker(double ms, string text, Brush brush)
        {
            double mx = ReplayX(ms);
            ReplayTimelineCanvas.Children.Add(new Line { X1 = mx, Y1 = 2, X2 = mx, Y2 = height - 2, Stroke = brush, StrokeThickness = 1.2 });
            var label = new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = 10
            };
            Canvas.SetLeft(label, Math.Min(width - 70, Math.Max(2, mx + 3)));
            Canvas.SetTop(label, 0);
            ReplayTimelineCanvas.Children.Add(label);
        }

        double ReplayX(double ms)
        {
            double denom = Math.Max(1, _replayWindowEndMs - _replayWindowStartMs);
            double f = Math.Clamp((ms - _replayWindowStartMs) / denom, 0, 1);
            return left + f * (right - left);
        }
    }

    private string DescribeReplayPhase(StrafeAttempt attempt, double cursorMs)
    {
        bool fromDown = cursorMs < attempt.ReleaseTimeMs;
        bool toDown = cursorMs >= attempt.OppositeDownTimeMs;
        bool bothDown = fromDown && toDown;
        bool neitherDown = !fromDown && !toDown;

        if (attempt.ClickTimeMs.HasValue && cursorMs >= attempt.ClickTimeMs.Value)
        {
            if (attempt.IsMovingAtClick)
            {
                string held = string.IsNullOrWhiteSpace(attempt.HeldKeysAtClick) ? "A/D" : attempt.HeldKeysAtClick;
                return $"M1 while moving: {held} was still held.";
            }
            return attempt.ClickFromCounterMs < _settings.IdealClickMinAfterCounterMs
                ? "M1 too early. Delay the shot."
                : "M1 after stop window.";
        }

        if (bothDown)
        {
            double overlap = Math.Abs(attempt.CounterDelayMs);
            return $"Overlap: {attempt.ToKey} down while {attempt.FromKey} is still held ({overlap:0.0} ms).";
        }

        if (neitherDown && attempt.CounterDelayMs > 0)
        {
            return $"Late gap: {attempt.ToKey} starts {attempt.CounterDelayMs:0.0} ms after {attempt.FromKey} up.";
        }

        if (toDown)
        {
            return $"Counter key {attempt.ToKey} is down.";
        }

        return $"Original key {attempt.FromKey} is still held.";
    }

    private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private void AttemptIncludeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        if (sender is CheckBox box && box.DataContext is StrafeAttempt attempt)
        {
            attempt.IsIncluded = box.IsChecked == true;
        }

        AttemptsGrid.Items.Refresh();
        RefreshStats();
    }

    private void AttemptJiggleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        if (sender is CheckBox box && box.DataContext is StrafeAttempt attempt)
        {
            attempt.IsJiggle = box.IsChecked == true;
            attempt.IsAutoTaggedJiggle = false;
        }

        AttemptsGrid.Items.Refresh();
        RefreshStats();
    }

    private bool AttemptViewFilter(object obj)
    {
        if (obj is not StrafeAttempt attempt) return false;

        bool showIncluded = ShowIncludedAttemptsCheckBox?.IsChecked != false;
        bool showFiltered = ShowFilteredAttemptsCheckBox?.IsChecked == true;
        bool effectiveIncluded = _analyzer.IsAttemptIncludedInStats(attempt);

        if (!attempt.HasClick) return false;
        if (effectiveIncluded && !showIncluded) return false;
        if (!effectiveIncluded && !showFiltered) return false;
        if (HideJigglesCheckBox?.IsChecked == true && attempt.IsJiggle) return false;

        return attempt.Grade switch
        {
            TimingGrade.Perfect => ShowPerfectCheckBox?.IsChecked != false,
            TimingGrade.Overlap => ShowOverlapCheckBox?.IsChecked != false,
            TimingGrade.Late => ShowLateCheckBox?.IsChecked != false,
            TimingGrade.EarlyClick => ShowEarlyClickCheckBox?.IsChecked != false,
            TimingGrade.MissedClick => false,
            _ => true
        };
    }

    private void ApplyPreferencesToUi(bool registerHotkeys)
    {
        if (!_uiReady) return;

        StartButton.Content = IsPeekModeVisible ? $"Start [{_preferences.StartStopHotkey}]" : $"Start [{_preferences.StartStopHotkey}]";
        StopButton.Content = IsPeekModeVisible ? $"Stop [{_preferences.StartStopHotkey}]" : $"Stop + Save [{_preferences.StartStopHotkey}]";
        PeekButton.Content = IsPeekModeVisible ? "Back to main" : $"Peek mode [{_preferences.PeekArmHotkey}]";
        TraceCountBox.Text = _preferences.DefaultTraceCount.ToString(CultureInfo.InvariantCulture);
        ApplyPreferencesToSettings();

        UseColumn.Visibility = _preferences.Columns.Use ? Visibility.Visible : Visibility.Collapsed;
        IndexColumn.Visibility = _preferences.Columns.Index ? Visibility.Visible : Visibility.Collapsed;
        DirectionColumn.Visibility = _preferences.Columns.Direction ? Visibility.Visible : Visibility.Collapsed;
        CounterDelayColumn.Visibility = _preferences.Columns.CounterDelay ? Visibility.Visible : Visibility.Collapsed;
        ClickDelayColumn.Visibility = _preferences.Columns.ClickDelay ? Visibility.Visible : Visibility.Collapsed;
        MistakesColumn.Visibility = _preferences.Columns.Mistakes ? Visibility.Visible : Visibility.Collapsed;
        ResultColumn.Visibility = _preferences.Columns.Result ? Visibility.Visible : Visibility.Collapsed;
        WhatHappenedColumn.Visibility = _preferences.Columns.WhatHappened ? Visibility.Visible : Visibility.Collapsed;
        AttemptFilterPanel.Visibility = _preferences.ShowAdvancedFilters ? Visibility.Visible : Visibility.Collapsed;

        ApplyCustomColors(_preferences.Colors);
        UpdatePrimarySessionButtons();

        if (registerHotkeys && IsLoaded)
        {
            UnregisterGlobalHotkeys();
            RegisterGlobalHotkeys(new WindowInteropHelper(this).Handle);
        }
    }

    private void UpdatePrimarySessionButtons()
    {
        if (!_uiReady) return;

        bool inPeek = IsPeekModeVisible;
        bool peekActive = inPeek && _peekModeView?.IsActive == true;

        StartButton.Content = inPeek ? $"Start [{_preferences.StartStopHotkey}]" : $"Start [{_preferences.StartStopHotkey}]";
        StopButton.Content = inPeek ? $"Stop [{_preferences.StartStopHotkey}]" : $"Stop + Save [{_preferences.StartStopHotkey}]";
        PeekButton.Content = inPeek ? "Back to main" : $"Peek mode [{_preferences.PeekArmHotkey}]";

        if (_stopSaveInProgress)
        {
            StartButton.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Visible;
            StopButton.IsEnabled = false;
            UpdateHeaderActionStates();
            return;
        }

        if (inPeek)
        {
            StartButton.Visibility = peekActive ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = peekActive ? Visibility.Visible : Visibility.Collapsed;
            StartButton.IsEnabled = !peekActive;
            StopButton.IsEnabled = peekActive;
            UpdateHeaderActionStates();
            return;
        }

        StartButton.Visibility = _active ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = _active ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !_active;
        StopButton.IsEnabled = _active;
        UpdateHeaderActionStates();
    }

    private void UpdateHeaderActionStates()
    {
        if (!_uiReady) return;
        UpdateCloudButtons();

        if (IsPeekModeVisible)
        {
            bool hasPeekData = _peekModeView?.HasData == true;
            OpenConclusionsButton.IsEnabled = false;
            ReportsButton.IsEnabled = false;
            HeaderConclusionsButton.IsEnabled = hasPeekData;
            HeaderConclusionsButton.ToolTip = hasPeekData ? "Open Peek mode conclusions." : "Record at least one Peek mode attempt first.";
            return;
        }

        bool hasIncludedAttempts = _analyzer.RecentAttempts.Any(a => _analyzer.IsAttemptIncludedInStats(a));
        bool canReview = !_active && !_stopSaveInProgress && hasIncludedAttempts;
        OpenConclusionsButton.IsEnabled = canReview;
        ReportsButton.IsEnabled = canReview;
        HeaderConclusionsButton.IsEnabled = canReview;
        HeaderConclusionsButton.ToolTip = canReview ? "Open session conclusions." : "Record at least one included attempt first.";
    }

    private void UpdateCloudButtons()
    {
        if (!_uiReady) return;
        AccountButton.Content = _supabase.IsSignedIn ? _supabase.DisplayName : "Account";
        AccountButton.ToolTip = _supabase.IsSignedIn ? "Open your profile." : "Sign in, create an account, or share anonymous practice stats.";
        ClansButton.IsEnabled = _supabase.IsSignedIn;
        ClansButton.ToolTip = _supabase.IsSignedIn ? "Open clan stats and invites." : "Sign in before opening clans.";
        AdminButton.Visibility = _supabase.CanViewAdmin ? Visibility.Visible : Visibility.Collapsed;
        AdminButton.IsEnabled = _supabase.CanViewAdmin;
        AdminButton.ToolTip = _supabase.IsAdmin
            ? "Open admin shared-stat summaries and manage moderators."
            : _supabase.IsModerator
                ? "Open moderator shared-stat summaries."
                : "The admin page is only available to the admin and moderators.";
    }

    private static void ApplyCustomColors(ColorPreferences colors)
    {
        SetBrush("WindowBgBrush", ParseColor(colors.WindowBackground, Color.FromRgb(11, 16, 32)));
        SetBrush("BgBrush", ParseColor(colors.Background, Color.FromRgb(11, 16, 32)));
        SetBrush("BackgroundBrush", ParseColor(colors.Background, Color.FromRgb(11, 16, 32)));
        SetBrush("CardBrush", ParseColor(colors.Card, Color.FromRgb(17, 24, 44)));
        SetBrush("SoftBrush", ParseColor(colors.Surface, Color.FromRgb(23, 32, 54)));
        SetBrush("AccentBrush", ParseColor(colors.Accent, Color.FromRgb(124, 92, 255)));
        SetBrush("Accent2Brush", ParseColor(colors.AccentAlt, Color.FromRgb(0, 224, 255)));
        SetBrush("TextBrush", ParseColor(colors.Text, Color.FromRgb(245, 247, 255)));
        SetBrush("MutedTextBrush", ParseColor(colors.MutedText, Color.FromRgb(168, 176, 200)));
        SetBrush("BorderBrush", ParseColor(colors.Border, Color.FromRgb(38, 48, 74)));
        SetBrush("GoodBrush", ParseColor(colors.Good, Color.FromRgb(56, 217, 150)));
        SetBrush("WarnBrush", ParseColor(colors.Warning, Color.FromRgb(255, 206, 69)));
        SetBrush("BadBrush", ParseColor(colors.Bad, Color.FromRgb(255, 92, 122)));
        SetBrush("StepLeftBrush", ParseColor(colors.StepLeft, Color.FromRgb(0, 224, 255)));
        SetBrush("StepRightBrush", ParseColor(colors.StepRight, Color.FromRgb(124, 92, 255)));
        SetBrush("SprayBrush", ParseColor(colors.Spray, Color.FromRgb(255, 206, 69)));
        SetBrush("TraceHighlightBrush", ParseColor(colors.TraceHighlight, Color.FromRgb(0, 224, 255)));
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            var text = hex.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;
            return (Color)ColorConverter.ConvertFromString(text);
        }
        catch
        {
            return fallback;
        }
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
        }
        else
        {
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }

    private static uint HotkeyToVirtualKey(string? value, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string key = value.Trim().ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') return key[0];
        if (key.StartsWith("F", StringComparison.Ordinal) && int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }
        return fallback;
    }

    private void ApplySettingsFromUi()
    {
        ApplyPreferencesToSettings();
        _settings.AutoTagJiggles = AutoTagJigglesCheckBox?.IsChecked == true;
        _settings.ExcludeJigglesFromStats = ExcludeJigglesCheckBox?.IsChecked != false;
        _settings.ExcludeNoClickFromStats = true;
        _settings.JiggleAutoTagAfterMs = ParseBox(JiggleAgeBox.Text, 260);
        _settings.JiggleMaxMousePathDegrees = ParseBox(JiggleMouseBox.Text, 0.08);
    }

    private void ApplyPreferencesToSettings()
    {
        _settings.IdealCounterMinMs = _preferences.CounterMinMs;
        _settings.IdealCounterMaxMs = _preferences.CounterMaxMs;
        _settings.IdealClickMinAfterCounterMs = _preferences.ClickMinMs;
        _settings.IdealClickMaxAfterCounterMs = _preferences.ClickMaxMs;
        _settings.KeyboardOverlapToleranceMs = _preferences.HallEffectToleranceMs;

        _settings.Calibration.Dpi = _preferences.Dpi;
        _settings.Calibration.Sensitivity = _preferences.Sensitivity;
        _settings.Calibration.YawDegreesPerCountAtSensitivityOne = _preferences.Yaw;
        _settings.Calibration.PitchDegreesPerCountAtSensitivityOne = _preferences.Pitch;
        _settings.Calibration.Multiplier = _preferences.Multiplier;
    }

    private static double ParseBox(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
    }

    private string CurrentFocus => FocusComboBox?.SelectedItem is ComboBoxItem item && item.Content is string s ? s : "Overall";

    private void RefreshStats()
    {
        if (!_uiReady) return;
        ApplySettingsFromUi();

        var now = DateTimeOffset.Now;
        var started = _startedAt == default ? now : _startedAt;
        double currentSessionMs = _active
            ? Math.Max(0, _clock.NowMs() - _sessionStartMs)
            : (_analyzer.Events.Count == 0 ? 0 : _analyzer.Events[^1].SessionTimeMs);
        _analyzer.ApplyAutoJiggleTags(currentSessionMs);
        var summary = _analyzer.BuildSummary(started, now);

        if (_active)
        {
            StatusText.Text = "Live";
            SessionTimeText.Text = TimeSpan.FromMilliseconds(Math.Max(0, _clock.NowMs() - _sessionStartMs)).ToString(@"mm\:ss\.f");
        }
        else
        {
            SessionTimeText.Text = "not recording";
        }

        AttemptsText.Text = summary.Attempts.ToString(CultureInfo.InvariantCulture);
        EventsText.Text = $"{summary.Attempts} click-confirmed · {summary.FilteredAttempts} hidden";
        PerfectRateText.Text = $"{summary.PerfectRate:0}%";
        PerfectDetailText.Text = $"{summary.Perfect} clean · {summary.MovingAtClick} moving / {summary.Attempts}";
        AvgDelayText.Text = $"{summary.AverageCounterDelayMs:0.0} ms";
        SpreadText.Text = $"stdev {summary.StdDevCounterDelayMs:0.0} ms";
        var week = _store.Aggregate(TimeSpan.FromDays(7));
        WeekRateText.Text = $"{week.PerfectRate:0}%";
        WeekDetailText.Text = $"{week.Attempts} attempts · {week.MovingAtClick} moving";

        var includedAttempts = _analyzer.RecentAttempts.Where(a => _analyzer.IsAttemptIncludedInStats(a)).Reverse().ToList();
        var coach = CoachingAnalyzer.AnalyzeSession(includedAttempts, _settings, CurrentFocus);
        var progress = _progressStore.Load();
        TipText.Text = coach.MainIssue;
        LatestAttemptText.Text = coach.LatestAttemptLabel;
        DirectionWeaknessText.Text = coach.DirectionWeakness;
        QualityScoreText.Text = $"Quality score: {coach.AverageQuality.Label}. Category maxes show where points are lost.";
        PracticePrescriptionText.Text = coach.PracticePrescription;
        FocusFeedbackText.Text = string.IsNullOrWhiteSpace(coach.FocusFeedback) ? $"Focus: {CurrentFocus}." : coach.FocusFeedback;
        if (progress.BestCleanStreak > 0)
        {
            PracticePrescriptionText.Text += $"  Personal best: {progress.BestCleanStreak} clean reps in a row; best clean session {progress.BestSessionCleanRate:0}%.";
        }
        UpdatePrimarySessionButtons();

        _attemptsView.Refresh();
        AttemptsGrid.Items.Refresh();
        RefreshTraceCanvas();
    }

    private void RefreshTraceCanvas()
    {
        if (TraceCanvas == null) return;

        TraceCanvas.Children.Clear();
        double width = TraceCanvas.ActualWidth;
        double height = TraceCanvas.ActualHeight;
        if (width < 20 || height < 20)
        {
            TraceStatsText.Text = "Trace overlay initializes after the window is visible.";
            return;
        }

        int maxTraces = Math.Clamp((int)Math.Round(ParseBox(TraceCountBox.Text, 24)), 1, 200);
        var attempts = _analyzer.RecentAttempts
            .Where(a => _analyzer.IsAttemptIncludedInStats(a))
            .Where(a => a.MouseTrace.Count > 0)
            .Take(maxTraces)
            .ToList();

        var selectedAttempt = _selectedAttempt?.MouseTrace.Count > 0 ? _selectedAttempt : null;
        if (selectedAttempt != null && !attempts.Contains(selectedAttempt))
        {
            attempts.Insert(0, selectedAttempt);
        }

        if (attempts.Count == 0)
        {
            DrawAxes(width, height, 1.0);
            TraceStatsText.Text = "No mouse trace yet. Start a session, counter-strafe, move the mouse, then click.";
            return;
        }

        var traces = attempts.Select(a => BuildTrace(a)).Where(t => t.Count > 1).ToList();
        var selectedTrace = selectedAttempt == null ? null : BuildTrace(selectedAttempt);
        if (selectedTrace != null && selectedTrace.Count <= 1) selectedTrace = null;

        if (traces.Count == 0 && selectedTrace == null)
        {
            DrawAxes(width, height, 1.0);
            TraceStatsText.Text = "Waiting for movement points between counter key and click.";
            return;
        }

        var scaleTraces = traces.ToList();
        if (selectedTrace != null) scaleTraces.Add(selectedTrace);

        double maxAbsX = scaleTraces.SelectMany(t => t).Select(p => Math.Abs(p.X)).DefaultIfEmpty(1).Max();
        double maxAbsY = scaleTraces.SelectMany(t => t).Select(p => Math.Abs(p.Y)).DefaultIfEmpty(1).Max();
        maxAbsX = Math.Max(maxAbsX, 0.05);
        maxAbsY = Math.Max(maxAbsY, 0.05);
        double baseScale = Math.Min((width - 28) / (maxAbsX * 2.0), (height - 28) / (maxAbsY * 2.0));
        _traceLastBaseScale = Math.Max(0.0001, baseScale);
        double scale = baseScale * _traceZoom;

        DrawAxes(width, height, scale);

        if (ShowUniqueLinesCheckBox.IsChecked == true)
        {
            for (int i = traces.Count - 1; i >= 0; i--)
            {
                DrawPolyline(traces[i], width, height, scale, TraceBrush(i), 1.4);
                if (ShowClickMarkersCheckBox.IsChecked == true)
                {
                    DrawEndpoint(traces[i][^1], width, height, scale, TraceBrush(i));
                }
            }
        }

        if (ShowAverageLineCheckBox.IsChecked == true && traces.Count >= 2)
        {
            var averageTrace = BuildAverageTrace(traces, 64);
            DrawPolyline(averageTrace, width, height, scale, new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 3.0);
            if (ShowClickMarkersCheckBox.IsChecked == true)
            {
                DrawEndpoint(averageTrace[^1], width, height, scale, new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 4.0);
            }
        }

        if (selectedAttempt != null && selectedTrace != null)
        {
            var highlightBrush = Application.Current.Resources["TraceHighlightBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(0, 224, 255));
            DrawPolyline(selectedTrace, width, height, scale, highlightBrush, 4.2);
            if (ShowClickMarkersCheckBox.IsChecked == true)
            {
                DrawEndpoint(selectedTrace[^1], width, height, scale, highlightBrush, 6.5);
            }
        }

        var subject = selectedAttempt ?? attempts[0];
        string prefix = selectedAttempt == null ? "Latest" : "Selected";
        TraceStatsText.Text = subject.MouseTrace.Count == 0
            ? "No trace metrics available for this attempt."
            : $"{prefix} #{subject.Index}: {subject.Direction}, path {subject.PathLengthDegrees:0.00} deg, efficiency {subject.PathEfficiency:0.00} · zoom {_traceZoom * 100:0}% · wheel to zoom, drag to pan";
    }

    private static List<TracePoint> BuildTrace(StrafeAttempt attempt)
    {
        var result = new List<TracePoint> { new(0, 0) };
        result.AddRange(attempt.MouseTrace.Select(p => new TracePoint(p.XDegrees, p.YDegrees)));
        return result;
    }

    private static List<TracePoint> BuildAverageTrace(IReadOnlyList<List<TracePoint>> traces, int samples)
    {
        var result = new List<TracePoint>();
        for (int i = 0; i < samples; i++)
        {
            double f = samples == 1 ? 0 : i / (double)(samples - 1);
            double x = 0;
            double y = 0;
            foreach (var trace in traces)
            {
                var p = InterpolateByFraction(trace, f);
                x += p.X;
                y += p.Y;
            }
            result.Add(new TracePoint(x / traces.Count, y / traces.Count));
        }
        return result;
    }

    private static TracePoint InterpolateByFraction(IReadOnlyList<TracePoint> trace, double fraction)
    {
        if (trace.Count == 0) return new TracePoint(0, 0);
        if (trace.Count == 1) return trace[0];
        double pos = fraction * (trace.Count - 1);
        int i = (int)Math.Floor(pos);
        if (i >= trace.Count - 1) return trace[^1];
        double local = pos - i;
        var a = trace[i];
        var b = trace[i + 1];
        return new TracePoint(a.X + (b.X - a.X) * local, a.Y + (b.Y - a.Y) * local);
    }

    private void DrawAxes(double width, double height, double scale)
    {
        var axisBrush = new SolidColorBrush(Color.FromArgb(80, 168, 176, 200));
        double cx = width / 2.0 + _tracePanX;
        double cy = height / 2.0 + _tracePanY;
        TraceCanvas.Children.Add(new Line { X1 = 0, Y1 = cy, X2 = width, Y2 = cy, Stroke = axisBrush, StrokeThickness = 1 });
        TraceCanvas.Children.Add(new Line { X1 = cx, Y1 = 0, X2 = cx, Y2 = height, Stroke = axisBrush, StrokeThickness = 1 });

        var origin = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromArgb(180, 0, 224, 255)) };
        Canvas.SetLeft(origin, cx - 3);
        Canvas.SetTop(origin, cy - 3);
        TraceCanvas.Children.Add(origin);

        var label = new TextBlock
        {
            Text = $"0,0 counter-key press origin · zoom {_traceZoom * 100:0}%",
            Foreground = new SolidColorBrush(Color.FromArgb(180, 168, 176, 200)),
            FontSize = 11
        };
        Canvas.SetLeft(label, 10);
        Canvas.SetTop(label, 8);
        TraceCanvas.Children.Add(label);
    }

    private void DrawPolyline(IReadOnlyList<TracePoint> points, double width, double height, double scale, Brush brush, double thickness)
    {
        if (points.Count < 2) return;
        double cx = width / 2.0 + _tracePanX;
        double cy = height / 2.0 + _tracePanY;
        var polyline = new Polyline
        {
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        foreach (var p in points)
        {
            polyline.Points.Add(new Point(cx + p.X * scale, cy + p.Y * scale));
        }

        TraceCanvas.Children.Add(polyline);
    }

    private void DrawEndpoint(TracePoint point, double width, double height, double scale, Brush brush, double radius = 3.0)
    {
        double cx = width / 2.0 + _tracePanX;
        double cy = height / 2.0 + _tracePanY;
        var dot = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = brush,
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        };
        Canvas.SetLeft(dot, cx + point.X * scale - radius);
        Canvas.SetTop(dot, cy + point.Y * scale - radius);
        TraceCanvas.Children.Add(dot);
    }

    private static Brush TraceBrush(int index)
    {
        string[] resourceKeys = ["StepLeftBrush", "StepRightBrush", "GoodBrush", "WarnBrush", "BadBrush", "Accent2Brush"];
        string key = resourceKeys[index % resourceKeys.Length];
        if (Application.Current.Resources[key] is SolidColorBrush brush)
        {
            var c = brush.Color;
            return new SolidColorBrush(Color.FromArgb(115, c.R, c.G, c.B));
        }

        Color[] colors =
        {
            Color.FromRgb(0, 224, 255),
            Color.FromRgb(124, 92, 255),
            Color.FromRgb(56, 217, 150),
            Color.FromRgb(255, 206, 69),
            Color.FromRgb(255, 92, 122),
            Color.FromRgb(120, 190, 255)
        };
        var c2 = colors[index % colors.Length];
        return new SolidColorBrush(Color.FromArgb(115, c2.R, c2.G, c2.B));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly record struct TracePoint(double X, double Y);
}
