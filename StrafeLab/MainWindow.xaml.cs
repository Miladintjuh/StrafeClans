using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private bool _replayTimelineDragging;
    private double _manualReplayCursorMs = double.NaN;
    private double _sessionStartMs;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastEndedAt;
    private HwndSource? _hotkeySource;
    private bool _stopSaveInProgress;
    private bool _hasSavedCurrentSession;
    private PeekModeView? _peekModeView;
    private readonly List<GuidedDemoStep> _guidedDemoSteps = new();
    private int _guidedDemoIndex;
    private int _guidedDemoVisualVersion;
    private bool _guidedDemoActive;
    private FrameworkElement? _guidedDemoTarget;
    private Brush? _guidedDemoPreviousBrush;
    private Thickness _guidedDemoPreviousThickness;
    private readonly Dictionary<UIElement, double> _guidedDemoPreviousOpacity = new();
    private readonly Dictionary<Panel, int> _guidedDemoPreviousZIndex = new();
    private SettingsView? _guidedDemoSettingsView;
    private ConclusionsView? _guidedDemoConclusionsView;
    private bool _demoSessionLoaded;
    private bool _guidedDemoTraceRowDimmingActive;
    private bool _guidedDemoAttemptSubsetActive;
    private bool _guidedDemoAutoAdvancing;
    private bool _guidedDemoFiveXReplayStarted;
    private bool _guidedDemoFiveXReplayFinished;
    private List<StrafeAttempt>? _guidedDemoAttemptSubset;
    private ShowLastView? _showLastView;
    private bool _showLastActive;
    private bool _showLastStartedOwnSession;

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
        UpdateRawInputMoveEmission();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Activated += (_, _) => ResetInputStateAfterFocusSwitch();
        Deactivated += (_, _) => ResetInputStateAfterFocusSwitch();
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
        else if (_supabase.Session?.NeedsDemo == true)
        {
            await Dispatcher.InvokeAsync(() => ShowDemoWalkthrough());
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

        int attemptsBefore = _analyzer.RecentAttempts.Count;
        int clickConfirmedBefore = _analyzer.RecentAttempts.Count(a => a.HasClick);
        var selectionBeforeAnalyzerUpdate = _selectedAttempt ?? AttemptsGrid.SelectedItem as StrafeAttempt;
        _analyzer.Add(e);
        int clickConfirmedAfter = _analyzer.RecentAttempts.Count(a => a.HasClick);
        if (e.Kind != InputKind.MouseMove)
        {
            TipText.Text = _analyzer.GetCoachingTip();
            _attemptsView.Refresh();
            if (clickConfirmedAfter > clickConfirmedBefore)
            {
                SelectLatestClickConfirmedAttempt();
            }
            else
            {
                ReselectAttemptIfVisible(selectionBeforeAnalyzerUpdate);
            }
            if (_showLastActive) UpdateShowLastView();
        }
        else if (_showLastActive && _analyzer.RecentAttempts.Count != attemptsBefore)
        {
            UpdateShowLastView();
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

    private async void ArmHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        await ArmPeekAttemptFromHotkeyAsync();
        UpdatePrimarySessionButtons();
    }

    private void StartSession()
    {
        if (_stopSaveInProgress) return;

        if (ModeHostBorder.Visibility == Visibility.Visible && ModeHost.Content != _peekModeView)
        {
            HideModeHost("Returned to live trainer.");
        }

        ApplySettingsFromUi();
        _demoSessionLoaded = false;
        _analyzer.BeginNewSession(preserveAttempts: true);
        _attemptsView.Refresh();
        _sessionStartMs = _clock.NowMs();
        _startedAt = DateTimeOffset.Now;
        _lastEndedAt = default;
        _showLastActive = false;
        _showLastStartedOwnSession = false;
        _active = true;
        _hasSavedCurrentSession = false;
        UpdateRawInputMoveEmission();
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
            _showLastActive = false;
            _showLastStartedOwnSession = false;
            UpdateRawInputMoveEmission();
            StatusText.Text = "Saving";

            ApplySettingsFromUi();
            _analyzer.ApplyAutoJiggleTags(Math.Max(0, _clock.NowMs() - _sessionStartMs));
            var endedAt = DateTimeOffset.Now;
            _lastEndedAt = endedAt;
            int saveSessionSerial = _analyzer.CurrentSessionSerial;
            var sessionAttempts = _analyzer.GetAttemptsForSession(saveSessionSerial);
            var sessionEvents = _analyzer.GetEventsForSession(saveSessionSerial);
            var summary = _analyzer.BuildSummaryForSession(saveSessionSerial, _startedAt == default ? endedAt : _startedAt, endedAt);
            var progress = _progressStore.UpdateFromSession(sessionAttempts);
            summary.BestCleanStreak = progress.BestCleanStreak;
            await _store.SaveAsync(summary, sessionEvents, sessionAttempts);
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


    private void ResetInputStateAfterFocusSwitch()
    {
        _listener?.ResetDeviceState();
        _analyzer.ResetHeldKeyState();
        UpdateRawInputMoveEmission();
    }

    private void UpdateRawInputMoveEmission()
    {
        if (_listener == null) return;
        _listener.EmitMouseMoves = _active || _showLastActive || IsPeekModeVisible;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        PreviewKeyDown -= MainWindow_PreviewKeyDown;
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



    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && AttemptsGrid.SelectedItems.Count > 0)
        {
            ClearAttemptSelection();
            e.Handled = true;
            return;
        }

        if (IsTypingIntoTextInput(e.OriginalSource)) return;

        if ((key == Key.Up || key == Key.Down) && TryMoveSelectedAttempt(key == Key.Down ? 1 : -1))
        {
            e.Handled = true;
            return;
        }

        string display = HotkeyDisplayName(key);
        if (string.IsNullOrWhiteSpace(display)) return;

        if (HotkeyMatches(display, _preferences.StartStopHotkey))
        {
            e.Handled = true;
            _ = ToggleSessionFromHotkeyAsync();
        }
        else if (HotkeyMatches(display, _preferences.RemoveLastHotkey))
        {
            e.Handled = true;
            _ = RemoveLastAttemptFromHotkeyAsync();
        }
        else if (HotkeyMatches(display, _preferences.PeekArmHotkey))
        {
            e.Handled = true;
            _ = ArmPeekAttemptFromHotkeyAsync();
        }
    }

    private static bool IsTypingIntoTextInput(object source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current != null)
        {
            if (current is TextBoxBase || current is PasswordBox || current is ComboBox) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool HotkeyMatches(string actual, string? configured)
        => string.Equals(actual.Trim(), (configured ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string HotkeyDisplayName(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString().ToUpperInvariant();
        if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((int)(key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture);
        if (key >= Key.F1 && key <= Key.F24) return key.ToString().ToUpperInvariant();
        return key switch
        {
            Key.Space => "SPACE",
            Key.Insert => "INSERT",
            Key.Delete => "DELETE",
            Key.Home => "HOME",
            Key.End => "END",
            Key.PageUp => "PAGEUP",
            Key.PageDown => "PAGEDOWN",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.Oem3 => "`",
            _ => key.ToString().ToUpperInvariant()
        };
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
            int saveSessionSerial = _analyzer.CurrentSessionSerial;
            var sessionAttempts = _analyzer.GetAttemptsForSession(saveSessionSerial);
            var sessionEvents = _analyzer.GetEventsForSession(saveSessionSerial);
            var summary = _analyzer.BuildSummaryForSession(saveSessionSerial, startedAt, endedAt);
            await _store.SaveAsync(summary, sessionEvents, sessionAttempts);
            TipText.Text = $"Removed latest attempt #{removed.Index} and updated saved session {summary.SessionId}.";
        }

        RefreshStats();
    }


    private async void ShowLastButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanSwitchModeAsync("Show last")) return;
        StartShowLastMode();
    }

    private void StartShowLastMode()
    {
        if (_showLastView == null)
        {
            _showLastView = new ShowLastView();
            _showLastView.BackRequested += StopShowLastMode;
        }

        if (!_active)
        {
            ApplySettingsFromUi();
            _analyzer.BeginNewSession(preserveAttempts: true);
            _attemptsView.Refresh();
            _sessionStartMs = _clock.NowMs();
            _startedAt = DateTimeOffset.Now;
            _lastEndedAt = default;
            _active = true;
            _hasSavedCurrentSession = false;
            _showLastStartedOwnSession = true;
        }
        else
        {
            _showLastStartedOwnSession = false;
        }

        _showLastActive = true;
        UpdateRawInputMoveEmission();
        ShowModeHost(_showLastView, fullWindow: true);
        UpdateShowLastView();
        StatusText.Text = "Listening";
        TipText.Text = "Show last is listening. Make a click-confirmed counter-strafe to review it immediately.";
        UpdatePrimarySessionButtons();
    }

    private void StopShowLastMode()
    {
        if (_showLastStartedOwnSession)
        {
            _active = false;
            _hasSavedCurrentSession = false;
            StatusText.Text = "Idle";
            SessionTimeText.Text = "not recording";
        }
        _showLastActive = false;
        _showLastStartedOwnSession = false;
        UpdateRawInputMoveEmission();
        HideModeHost("Returned from Show last.");
        RefreshStats();
    }

    private void UpdateShowLastView()
    {
        if (_showLastView == null) return;
        var latest = _analyzer.RecentAttempts.Where(a => a.HasClick).OrderByDescending(a => a.Index).FirstOrDefault();
        _showLastView.UpdateAttempt(latest, _settings);
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
        UpdateRawInputMoveEmission();
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

        if (_showLastActive && _showLastStartedOwnSession)
        {
            StopShowLastMode();
            return;
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
        UpdateRawInputMoveEmission();
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

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanSwitchModeAsync("Account")) return;
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
            if (startup && !_preferences.GuidedDemoSeen)
            {
                ShowDemoWalkthrough();
                return;
            }
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
                if (_supabase.Session?.NeedsDemo == true)
                {
                    await Dispatcher.InvokeAsync(() => ShowDemoWalkthrough());
                }
                else if (startup)
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

    private void ShowDemoWalkthrough()
    {
        BeginGuidedDemo();
    }

    private void BeginGuidedDemo()
    {
        _guidedDemoSteps.Clear();
        _guidedDemoIndex = 0;
        _guidedDemoActive = true;
        _guidedDemoFiveXReplayStarted = false;
        _guidedDemoFiveXReplayFinished = false;
        _guidedDemoSettingsView?.SetDemoButtonVisible(false);
        GuidedDemoCompletionOverlay.Visibility = Visibility.Collapsed;

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Set your sensitivity",
            "Enter your real mouse DPI and CS2 sensitivity, then press Apply. StrafeLab uses this to turn raw mouse movement into useful aim numbers like degrees and cm/360.",
            () => _guidedDemoSettingsView?.CalibrationDemoTarget,
            () => ShowSettingsForGuidedDemo(),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Session controls",
            $"This is the normal trainer screen. Idle shows Start [{_preferences.StartStopHotkey}]. Recording shows only Stop + Save. The demo loads your recorded example session so you can learn the review flow.",
            () => StartButton.Visibility == Visibility.Visible ? StartButton : StopButton,
            () => ShowMainTrainerForGuidedDemo(),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Real example attempts",
            "This table lists only useful click-confirmed attempts. Use it to compare reps, spot repeated mistakes, and select the attempt you want to inspect.",
            () => AttemptsCard,
            () => ShowMainTrainerForGuidedDemo(),
            () => _analyzer.RecentAttempts.Count(a => a.HasClick) >= 20 ? null : "The bundled demo session did not load. Check that DemoData/attempts.csv is copied next to the app."));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Click an attempt with a trace",
            "Click one of the bright rows marked Trace. During this demo, the table is simplified to five useful examples so you can immediately see what row selection does. Rows without a trace still show key timing, but will not open the mouse path step.",
            () => AttemptsCard,
            () => ShowMainTrainerForGuidedDemo(),
            () => _selectedAttempt?.MouseTrace.Count > 0 ? null : "Click one of the bright rows marked Trace to continue to the mouse path.",
            true));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Read the mouse trace",
            "The mouse trace shows how your crosshair moved around the shot. A clean trace is short and direct; a messy trace usually means overflicking or correcting too late.",
            () => MouseTracePanel,
            () => ShowMainTrainerForGuidedDemo(),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Replay A/D/M1 timing",
            "This replay is one of StrafeLab's best tools. Press 5x slow to check the order: release old key, press counter key, then M1. The shot should happen after movement is gone. The replay exposes overlap and moving-shot timing.",
            () => ReplayCard,
            () => ShowMainTrainerForGuidedDemo(),
            () => _guidedDemoFiveXReplayFinished ? null : "Click the 5x slow button and let the replay finish before continuing."));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Replay controls",
            "Click the replay as often as you want, or change how fast you want to see the replay.",
            () => ReplayCard,
            () => ShowMainTrainerForGuidedDemo(),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Open Conclusions: Timing",
            "Conclusions uses the same recorded example attempts. Timing shows whether counters are early, late, or overlapping across the session.",
            () => _guidedDemoConclusionsView?.ConclusionsDemoTarget ?? ModeHostBorder,
            () => ShowConclusionsForGuidedDemo(0),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Conclusions: Direction compare",
            "This tab compares A>D and D>A. In real use, this tells you which side should get extra reps first.",
            () => _guidedDemoConclusionsView?.ConclusionsDemoTarget ?? ModeHostBorder,
            () => ShowConclusionsForGuidedDemo(1),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Conclusions: Mistakes",
            "This tab groups the related attempts by mistake type and shows tracked metrics next to them. Use it to see the repeated pattern instead of only judging one row at a time.",
            () => _guidedDemoConclusionsView?.ConclusionsDemoTarget ?? ModeHostBorder,
            () => ShowConclusionsForGuidedDemo(2),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Conclusions: Mouse / aim",
            "This tab connects counter-strafe timing to crosshair movement. Use it to spot late corrections, overflicks, and rushed mouse movement.",
            () => _guidedDemoConclusionsView?.ConclusionsDemoTarget ?? ModeHostBorder,
            () => ShowConclusionsForGuidedDemo(3),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Conclusions: Score / fix",
            "This tab explains where the quality score was lost, so the score becomes a concrete fix instead of just a number.",
            () => _guidedDemoConclusionsView?.ConclusionsDemoTarget ?? ModeHostBorder,
            () => ShowConclusionsForGuidedDemo(4),
            null));

        _guidedDemoSteps.Add(new GuidedDemoStep(
            "Conclusions: Practice plan",
            "Finish here. This tab turns repeated mistakes into a short drill. Close this bubble when you are done reading it.",
            () => _guidedDemoConclusionsView?.ConclusionsDemoTarget ?? ModeHostBorder,
            () => ShowConclusionsForGuidedDemo(5),
            null));

        ApplyGuidedDemoStep();
    }

    private void ShowSettingsForGuidedDemo()
    {
        if (ModeHost.Content is SettingsView settingsView)
        {
            _guidedDemoSettingsView = settingsView;
            settingsView.SetDemoButtonVisible(false);
            ShowModeHost(settingsView, fullWindow: true);
            return;
        }

        var view = new SettingsView(_preferences);
        view.SetDemoButtonVisible(false);
        _guidedDemoSettingsView = view;
        view.BackRequested += () => { if (!_guidedDemoActive) HideModeHost("Returned from settings."); };
        view.ShowDemoRequested += BeginGuidedDemo;
        view.PreferencesApplied += prefs =>
        {
            _preferencesStore.Save(prefs);
            ApplyPreferencesToUi(registerHotkeys: true);
            _peekModeView?.ApplyPreferences(_preferences);
            UpdateHeaderActionStates();
            RefreshStats();
            TipText.Text = "Sensitivity and settings saved.";
        };
        ShowModeHost(view, fullWindow: true);
    }

    private void ShowMainTrainerForGuidedDemo()
    {
        if (ModeHostBorder.Visibility == Visibility.Visible && ModeHost.Content != _peekModeView)
        {
            HideModeHost(null);
        }
        _guidedDemoSettingsView = null;
        _guidedDemoConclusionsView = null;
        HeaderChrome.Visibility = Visibility.Visible;
        MetricsChrome.Visibility = Visibility.Visible;
        MainChrome.Visibility = Visibility.Visible;
        if (_guidedDemoActive) LoadBundledDemoSessionIntoTrainer();
        UpdatePrimarySessionButtons();
        UpdateHeaderActionStates();
        RefreshStats();
    }

    private void LoadBundledDemoSessionIntoTrainer()
    {
        if (_demoSessionLoaded && _analyzer.RecentAttempts.Count >= 20) return;

        try
        {
            var demo = DemoSessionDataLoader.LoadBundled();
            _active = false;
            _stopSaveInProgress = false;
            _startedAt = demo.StartedAt;
            _lastEndedAt = demo.EndedAt;
            _hasSavedCurrentSession = true;
            _analyzer.LoadSessionData(demo.Events, demo.Attempts);
            _demoSessionLoaded = true;

            var suggestedAttempt = _analyzer.RecentAttempts.OrderByDescending(a => a.MouseTrace.Count).FirstOrDefault(a => a.MouseTrace.Count > 0)
                ?? _analyzer.RecentAttempts.FirstOrDefault(a => a.HasClick);
            _selectedAttempt = null;
            AttemptsGrid.SelectedItem = null;
            if (suggestedAttempt != null) AttemptsGrid.ScrollIntoView(suggestedAttempt);
            StopReplay(resetToSelectedStart: false);
            ResetReplayForSelectedAttempt();
            StatusText.Text = "Demo session";
            TipText.Text = "Loaded your real manual test session as the guided demo data.";
        }
        catch (Exception ex)
        {
            TipText.Text = $"Demo data could not be loaded: {ex.Message}";
        }
    }

    private void ShowConclusionsForGuidedDemo(int tabIndex)
    {
        if (_analyzer.RecentAttempts.Count(a => _analyzer.IsAttemptIncludedInStats(a)) == 0)
        {
            ShowMainTrainerForGuidedDemo();
            return;
        }

        ApplySettingsFromUi();
        var attempts = _analyzer.RecentAttempts
            .Reverse()
            .Where(a => _analyzer.IsAttemptIncludedInStats(a))
            .ToList();
        _guidedDemoConclusionsView = new ConclusionsView(attempts, _settings);
        _guidedDemoConclusionsView.BackRequested += () => { if (!_guidedDemoActive) HideModeHost("Returned from conclusions."); };
        ShowModeHost(_guidedDemoConclusionsView, fullWindow: true);
        _guidedDemoConclusionsView.SelectDemoTab(tabIndex);
        TipText.Text = "Conclusions are open for the guided demo.";
    }

    private void GuidedDemoBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_guidedDemoActive || _guidedDemoIndex <= 0) return;
        _guidedDemoIndex--;
        ApplyGuidedDemoStep();
    }

    private void GuidedDemoNextButton_Click(object sender, RoutedEventArgs e)
    {
        AdvanceGuidedDemo(ignoreWarnings: false);
    }

    private void AdvanceGuidedDemo(bool ignoreWarnings)
    {
        if (!_guidedDemoActive) return;
        var current = _guidedDemoSteps[_guidedDemoIndex];
        if (!ignoreWarnings)
        {
            string? warning = current.CanAdvance?.Invoke();
            if (!string.IsNullOrWhiteSpace(warning))
            {
                GuidedDemoWarningText.Text = warning;
                GuidedDemoWarningText.Visibility = Visibility.Visible;
                return;
            }
        }

        if (_guidedDemoIndex >= _guidedDemoSteps.Count - 1)
        {
            FinishGuidedDemo(showCompletion: true);
            return;
        }

        _guidedDemoIndex++;
        ApplyGuidedDemoStep();
    }

    private void TryAutoAdvanceGuidedDemoStep()
    {
        if (!_guidedDemoActive || _guidedDemoAutoAdvancing || _guidedDemoIndex < 0 || _guidedDemoIndex >= _guidedDemoSteps.Count) return;
        var current = _guidedDemoSteps[_guidedDemoIndex];
        if (!current.AutoAdvanceOnCompleted) return;
        if (!string.IsNullOrWhiteSpace(current.CanAdvance?.Invoke())) return;

        _guidedDemoAutoAdvancing = true;
        Dispatcher.InvokeAsync(() =>
        {
            _guidedDemoAutoAdvancing = false;
            if (_guidedDemoActive) AdvanceGuidedDemo(ignoreWarnings: true);
        }, DispatcherPriority.Background);
    }

    private void TryAdvanceGuidedDemoAfterReplayFinished()
    {
        if (!IsGuidedDemoReplayActionStep || !_guidedDemoFiveXReplayStarted || _guidedDemoFiveXReplayFinished || _guidedDemoAutoAdvancing) return;
        _guidedDemoFiveXReplayFinished = true;
        _guidedDemoAutoAdvancing = true;
        Dispatcher.InvokeAsync(() =>
        {
            _guidedDemoAutoAdvancing = false;
            if (_guidedDemoActive && IsGuidedDemoReplayActionStep) AdvanceGuidedDemo(ignoreWarnings: true);
        }, DispatcherPriority.Background);
    }

    private void GuidedDemoExitButton_Click(object sender, RoutedEventArgs e) => FinishGuidedDemo(showCompletion: true);

    private void GuidedDemoCompletionOkButton_Click(object sender, RoutedEventArgs e)
    {
        GuidedDemoCompletionOverlay.Visibility = Visibility.Collapsed;
        ClearGuidedDemoData();
        HideModeHost(null);
        TipText.Text = "Ready. Start a real session when you are set.";
    }

    private void ClearGuidedDemoData()
    {
        RestoreGuidedDemoAttemptSubsetMode();
        if (!_demoSessionLoaded) return;
        _active = false;
        _showLastActive = false;
        _showLastStartedOwnSession = false;
        UpdateRawInputMoveEmission();
        _stopSaveInProgress = false;
        _hasSavedCurrentSession = false;
        _demoSessionLoaded = false;
        _selectedAttempt = null;
        AttemptsGrid.SelectedItem = null;
        _analyzer.Reset();
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();
        RefreshStats();
        StatusText.Text = "Idle";
        SessionTimeText.Text = "not recording";
    }

    private void FinishGuidedDemo(bool showCompletion)
    {
        ClearGuidedDemoHighlight();
        RestoreGuidedDemoAttemptSubsetMode();
        _guidedDemoActive = false;
        GuidedDemoBubble.Visibility = Visibility.Collapsed;
        _supabase.MarkDemoSeen();
        _preferences.GuidedDemoSeen = true;
        _preferencesStore.Save(_preferences);
        UpdateCloudButtons();
        UpdateHeaderActionStates();
        if (showCompletion)
        {
            ShowMainTrainerForGuidedDemo();
            GuidedDemoCompletionOverlay.Visibility = Visibility.Visible;
        }
    }

    private void ApplyGuidedDemoStep()
    {
        if (_guidedDemoSteps.Count == 0) return;
        ClearGuidedDemoHighlight();
        var step = _guidedDemoSteps[_guidedDemoIndex];
        int visualVersion = ++_guidedDemoVisualVersion;
        step.BeforeShow?.Invoke();
        ApplyGuidedDemoAttemptSubsetMode();
        if (step.Title.Equals("Replay A/D/M1 timing", StringComparison.OrdinalIgnoreCase))
        {
            _guidedDemoFiveXReplayStarted = false;
            _guidedDemoFiveXReplayFinished = false;
        }
        GuidedDemoStepText.Text = $"{_guidedDemoIndex + 1}/{_guidedDemoSteps.Count}";
        GuidedDemoTitleText.Text = step.Title;
        GuidedDemoBodyText.Text = step.Body;
        GuidedDemoWarningText.Visibility = Visibility.Collapsed;
        GuidedDemoBackButton.IsEnabled = _guidedDemoIndex > 0;
        GuidedDemoNextButton.Content = _guidedDemoIndex == _guidedDemoSteps.Count - 1 ? "Finish" : "Next";
        GuidedDemoBubble.Visibility = Visibility.Visible;
        ScheduleGuidedDemoVisualRefresh(step, visualVersion);
    }

    private void ScheduleGuidedDemoVisualRefresh(GuidedDemoStep step, int visualVersion)
    {
        RefreshGuidedDemoVisuals(step, visualVersion);
        Dispatcher.InvokeAsync(() => RefreshGuidedDemoVisuals(step, visualVersion), DispatcherPriority.Loaded);
        Dispatcher.InvokeAsync(() => RefreshGuidedDemoVisuals(step, visualVersion), DispatcherPriority.ContextIdle);
        Dispatcher.InvokeAsync(() => RefreshGuidedDemoVisuals(step, visualVersion), DispatcherPriority.ApplicationIdle);
    }

    private void RefreshGuidedDemoVisuals(GuidedDemoStep step, int visualVersion)
    {
        if (!_guidedDemoActive || visualVersion != _guidedDemoVisualVersion) return;
        ClearGuidedDemoHighlight();
        HighlightGuidedDemoTarget(step.Target());
        ApplyGuidedDemoAttemptRowDimming();
    }

    private void HighlightGuidedDemoTarget(FrameworkElement? target)
    {
        if (target == null) return;
        _guidedDemoTarget = target;
        switch (target)
        {
            case Border border:
                _guidedDemoPreviousBrush = border.BorderBrush;
                _guidedDemoPreviousThickness = border.BorderThickness;
                border.BorderBrush = (Brush)FindResource("Accent2Brush");
                border.BorderThickness = new Thickness(3);
                break;
            case Control control:
                _guidedDemoPreviousBrush = control.BorderBrush;
                _guidedDemoPreviousThickness = control.BorderThickness;
                control.BorderBrush = (Brush)FindResource("Accent2Brush");
                control.BorderThickness = new Thickness(3);
                break;
        }
        target.BringIntoView();
        Dispatcher.InvokeAsync(() =>
        {
            target.BringIntoView();
            if (_guidedDemoActive && ReferenceEquals(_guidedDemoTarget, target))
            {
                HideGuidedDemoSpotlight();
                PositionGuidedDemoBubble(target);
            }
        }, DispatcherPriority.Background);
        ApplyGuidedDemoDimming(target);
        PositionGuidedDemoBubble(target);
    }

    private bool IsGuidedDemoTraceSelectionStep => _guidedDemoActive && _guidedDemoIndex >= 0 && _guidedDemoIndex < _guidedDemoSteps.Count && _guidedDemoSteps[_guidedDemoIndex].Title.Contains("trace", StringComparison.OrdinalIgnoreCase) && _guidedDemoSteps[_guidedDemoIndex].Title.Contains("Click", StringComparison.OrdinalIgnoreCase);

    private bool IsGuidedDemoReplayActionStep => _guidedDemoActive
        && _guidedDemoIndex >= 0
        && _guidedDemoIndex < _guidedDemoSteps.Count
        && _guidedDemoSteps[_guidedDemoIndex].Title.Equals("Replay A/D/M1 timing", StringComparison.OrdinalIgnoreCase);

    private void ApplyGuidedDemoAttemptSubsetMode()
    {
        if (!IsGuidedDemoTraceSelectionStep) return;
        if (_guidedDemoAttemptSubsetActive) return;

        var traceRows = _analyzer.RecentAttempts
            .Where(a => a.HasClick && a.MouseTrace.Count > 0)
            .OrderByDescending(a => a.Index)
            .Take(3)
            .ToList();
        var nonTraceRows = _analyzer.RecentAttempts
            .Where(a => a.HasClick && a.MouseTrace.Count == 0 && !traceRows.Contains(a))
            .OrderByDescending(a => a.Index)
            .Take(Math.Max(0, 5 - traceRows.Count))
            .ToList();
        _guidedDemoAttemptSubset = traceRows.Concat(nonTraceRows).Take(5).ToList();

        if (_guidedDemoAttemptSubset.Count == 0) return;
        _guidedDemoAttemptSubsetActive = true;
        AttemptsGrid.ItemsSource = _guidedDemoAttemptSubset;
        AttemptsGrid.SelectedItem = null;
        _selectedAttempt = null;
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();
        AttemptsGrid.Items.Refresh();
        RefreshTraceCanvas();
    }

    private void RestoreGuidedDemoAttemptSubsetMode()
    {
        if (!_guidedDemoAttemptSubsetActive) return;
        _guidedDemoAttemptSubsetActive = false;
        _guidedDemoAttemptSubset = null;
        AttemptsGrid.ItemsSource = _attemptsView;
        AttemptsGrid.Items.Refresh();
    }

    private void ApplyGuidedDemoAttemptRowDimming()
    {
        if (!IsGuidedDemoTraceSelectionStep)
        {
            RestoreGuidedDemoTraceRowDimming();
            return;
        }

        _guidedDemoTraceRowDimmingActive = true;
        AttemptsGrid.UpdateLayout();
        foreach (var item in AttemptsGrid.Items.OfType<StrafeAttempt>())
        {
            if (AttemptsGrid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) continue;
            if (!_guidedDemoPreviousOpacity.ContainsKey(row)) _guidedDemoPreviousOpacity[row] = row.Opacity;
            bool hasTrace = item.MouseTrace.Count > 0;
            row.Opacity = hasTrace ? 1.0 : 0.22;
            row.IsHitTestVisible = true;
        }
    }

    private void RestoreGuidedDemoTraceRowDimming()
    {
        if (!_guidedDemoTraceRowDimmingActive) return;
        foreach (var item in AttemptsGrid.Items.OfType<StrafeAttempt>())
        {
            if (AttemptsGrid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) continue;
            row.IsHitTestVisible = true;
        }
        _guidedDemoTraceRowDimmingActive = false;
    }

    private void PositionGuidedDemoBubble(FrameworkElement target)
    {
        try
        {
            var point = target.TransformToAncestor(this).Transform(new Point(0, 0));
            double centerX = point.X + Math.Max(0, target.ActualWidth) / 2.0;
            double centerY = point.Y + Math.Max(0, target.ActualHeight) / 2.0;
            double width = Math.Max(1, ActualWidth);
            double height = Math.Max(1, ActualHeight);

            GuidedDemoBubble.HorizontalAlignment = centerX < width / 2.0 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            GuidedDemoBubble.VerticalAlignment = centerY < height / 2.0 ? VerticalAlignment.Bottom : VerticalAlignment.Top;

            double horizontalGap = width < 980 ? 10 : 24;
            double verticalGap = height < 760 ? 10 : 24;
            GuidedDemoBubble.Width = width < 980 ? Math.Min(440, Math.Max(320, width - 48)) : 440;
            GuidedDemoBubble.Margin = new Thickness(horizontalGap, verticalGap, horizontalGap, verticalGap);
        }
        catch
        {
            GuidedDemoBubble.HorizontalAlignment = HorizontalAlignment.Right;
            GuidedDemoBubble.VerticalAlignment = VerticalAlignment.Bottom;
            GuidedDemoBubble.Margin = new Thickness(22);
        }
    }

    private const double GuidedDemoSpotlightOpacity = 0.80;
    private const double GuidedDemoSpotlightPadding = 8.0;

    private void ApplyGuidedDemoDimming(FrameworkElement target)
    {
        RestoreGuidedDemoDimming();
        HideGuidedDemoSpotlight();

        if (target.Parent is Panel panel)
        {
            _guidedDemoPreviousZIndex[panel] = Panel.GetZIndex(target);
            Panel.SetZIndex(target, 240);
        }
        ApplyGuidedDemoSiblingDimming(target);
        ApplyGuidedDemoViewRootDimming(target);
    }

    private void ShowGuidedDemoSpotlight(FrameworkElement target)
    {
        try
        {
            GuidedDemoSpotlightOverlay.Visibility = Visibility.Visible;

            double rootWidth = Math.Max(0, RootChrome.ActualWidth);
            double rootHeight = Math.Max(0, RootChrome.ActualHeight);
            if (rootWidth <= 0 || rootHeight <= 0)
            {
                RootChrome.UpdateLayout();
                rootWidth = Math.Max(0, RootChrome.ActualWidth);
                rootHeight = Math.Max(0, RootChrome.ActualHeight);
            }

            var point = target.TransformToAncestor(RootChrome).Transform(new Point(0, 0));
            double targetWidth = target.ActualWidth > 0 ? target.ActualWidth : target.RenderSize.Width;
            double targetHeight = target.ActualHeight > 0 ? target.ActualHeight : target.RenderSize.Height;
            double left = Math.Max(0, point.X - GuidedDemoSpotlightPadding);
            double top = Math.Max(0, point.Y - GuidedDemoSpotlightPadding);
            double right = Math.Min(rootWidth, point.X + targetWidth + GuidedDemoSpotlightPadding);
            double bottom = Math.Min(rootHeight, point.Y + targetHeight + GuidedDemoSpotlightPadding);

            if (right <= left || bottom <= top)
            {
                HideGuidedDemoSpotlight();
                return;
            }

            SetGuidedDemoDimRect(GuidedDemoDimTop, 0, 0, rootWidth, top);
            SetGuidedDemoDimRect(GuidedDemoDimLeft, 0, top, left, bottom - top);
            SetGuidedDemoDimRect(GuidedDemoDimRight, right, top, rootWidth - right, bottom - top);
            SetGuidedDemoDimRect(GuidedDemoDimBottom, 0, bottom, rootWidth, rootHeight - bottom);
        }
        catch
        {
            // Fall back to full-screen dimming if the target cannot be transformed yet.
            GuidedDemoSpotlightOverlay.Visibility = Visibility.Visible;
            double rootWidth = Math.Max(0, RootChrome.ActualWidth);
            double rootHeight = Math.Max(0, RootChrome.ActualHeight);
            SetGuidedDemoDimRect(GuidedDemoDimTop, 0, 0, rootWidth, rootHeight);
            SetGuidedDemoDimRect(GuidedDemoDimLeft, 0, 0, 0, 0);
            SetGuidedDemoDimRect(GuidedDemoDimRight, 0, 0, 0, 0);
            SetGuidedDemoDimRect(GuidedDemoDimBottom, 0, 0, 0, 0);
        }
    }

    private static void SetGuidedDemoDimRect(Rectangle rect, double left, double top, double width, double height)
    {
        Canvas.SetLeft(rect, Math.Max(0, left));
        Canvas.SetTop(rect, Math.Max(0, top));
        rect.Width = Math.Max(0, width);
        rect.Height = Math.Max(0, height);
        rect.Visibility = width > 0 && height > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideGuidedDemoSpotlight()
    {
        GuidedDemoSpotlightOverlay.Visibility = Visibility.Collapsed;
        SetGuidedDemoDimRect(GuidedDemoDimTop, 0, 0, 0, 0);
        SetGuidedDemoDimRect(GuidedDemoDimLeft, 0, 0, 0, 0);
        SetGuidedDemoDimRect(GuidedDemoDimRight, 0, 0, 0, 0);
        SetGuidedDemoDimRect(GuidedDemoDimBottom, 0, 0, 0, 0);
    }

    private void DimGuidedDemoElement(UIElement element)
    {
        if (_guidedDemoPreviousOpacity.ContainsKey(element)) return;
        _guidedDemoPreviousOpacity[element] = element.Opacity;
        element.Opacity = 0.10;
    }

    private static bool IsAncestorOfTarget(FrameworkElement ancestor, FrameworkElement? target)
    {
        if (target == null) return false;
        if (ReferenceEquals(ancestor, target)) return true;
        try { return ancestor.IsAncestorOf(target); }
        catch { return false; }
    }

    private void ApplyGuidedDemoSiblingDimming(FrameworkElement target)
    {
        var scope = FindGuidedDemoDimScope(target);
        if (scope == null) return;

        var keepChild = DirectChildOnPath(scope, target);
        foreach (UIElement child in scope.Children)
        {
            if (ReferenceEquals(child, keepChild)) continue;
            DimGuidedDemoElement(child);
        }
    }

    private void ApplyGuidedDemoViewRootDimming(FrameworkElement target)
    {
        var viewRoot = FindContainingUserControlRoot(target);
        if (viewRoot == null) return;
        DimDirectChildrenExceptPath(viewRoot, target);

        if (FindVisualChild<ScrollViewer>(viewRoot) is ScrollViewer scrollViewer && scrollViewer.Content is Panel scrollContent)
        {
            DimDirectChildrenExceptPath(scrollContent, target);
        }
    }

    private void DimDirectChildrenExceptPath(Panel scope, FrameworkElement target)
    {
        foreach (UIElement child in scope.Children)
        {
            if (child is FrameworkElement fe && (ReferenceEquals(fe, target) || IsAncestorOfTarget(fe, target) || IsAncestorOfTarget(target, fe))) continue;
            DimGuidedDemoElement(child);
        }
    }

    private static Panel? FindContainingUserControlRoot(FrameworkElement target)
    {
        DependencyObject? current = target;
        while (current is not null)
        {
            if (current is UserControl control)
            {
                return control.Content as Panel;
            }
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }

    private static Panel? FindGuidedDemoDimScope(FrameworkElement target)
    {
        var current = target.Parent as DependencyObject;
        while (current is not null)
        {
            if (current is Panel panel && panel.Children.Count > 1)
            {
                return panel;
            }
            current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static UIElement? DirectChildOnPath(Panel scope, FrameworkElement target)
    {
        DependencyObject current = target;
        DependencyObject? parent = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        while (parent is not null && !ReferenceEquals(parent, scope))
        {
            current = parent;
            parent = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
        }
        return current as UIElement;
    }

    private void RestoreGuidedDemoDimming()
    {
        HideGuidedDemoSpotlight();
        RestoreGuidedDemoTraceRowDimming();
        foreach (var (element, opacity) in _guidedDemoPreviousOpacity.ToList())
        {
            element.Opacity = opacity;
        }
        _guidedDemoPreviousOpacity.Clear();

        if (_guidedDemoTarget?.Parent is Panel panel && _guidedDemoPreviousZIndex.TryGetValue(panel, out int zIndex))
        {
            Panel.SetZIndex(_guidedDemoTarget, zIndex);
        }
        _guidedDemoPreviousZIndex.Clear();
    }

    private void ClearGuidedDemoHighlight()
    {
        RestoreGuidedDemoDimming();
        if (_guidedDemoTarget is Border border)
        {
            border.BorderBrush = _guidedDemoPreviousBrush;
            border.BorderThickness = _guidedDemoPreviousThickness;
        }
        else if (_guidedDemoTarget is Control control)
        {
            control.BorderBrush = _guidedDemoPreviousBrush;
            control.BorderThickness = _guidedDemoPreviousThickness;
        }
        _guidedDemoTarget = null;
        _guidedDemoPreviousBrush = null;
        _guidedDemoPreviousThickness = default;
    }

    private sealed record GuidedDemoStep(
        string Title,
        string Body,
        Func<FrameworkElement?> Target,
        Action? BeforeShow,
        Func<string?>? CanAdvance,
        bool AutoAdvanceOnCompleted = false);

    private async void ClansButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanSwitchModeAsync("Rankings")) return;
        if (!_supabase.IsSignedIn)
        {
            AccountButton_Click(sender, e);
            return;
        }

        var view = new ClanView(_supabase, () => _store.LoadSummaries());
        view.BackRequested += () => HideModeHost("Returned from rankings.");
        view.ProfileRequested += username => ShowProfileView(username);
        ShowModeHost(view);
        TipText.Text = "Rankings page is open.";
    }

    private async void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanSwitchModeAsync("Admin")) return;
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

        var view = new AdminView(_supabase, _preferences, prefs =>
        {
            _preferencesStore.Save(prefs);
            ApplyPreferencesToUi(registerHotkeys: true);
            _peekModeView?.ApplyPreferences(_preferences);
            RefreshStats();
        });
        view.BackRequested += () => HideModeHost("Returned from admin stats.");
        ShowModeHost(view);
        TipText.Text = "Admin stats are open.";
    }

    private async void OpenConclusionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanSwitchModeAsync("Conclusions")) return;
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

    private async void HeaderConclusionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanSwitchModeAsync("Conclusions")) return;
        if (IsPeekModeVisible)
        {
            _peekModeView?.ShowConclusionsFromHeader();
            UpdatePrimarySessionButtons();
            return;
        }

        OpenConclusionsButton_Click(sender, e);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanSwitchModeAsync("Settings")) return;
        var view = new SettingsView(_preferences);
        view.SetDemoButtonVisible(!_guidedDemoActive);
        view.BackRequested += () => HideModeHost("Returned from settings.");
        view.ShowDemoRequested += () =>
        {
            _supabase.MarkDemoNeeded();
            _preferences.GuidedDemoSeen = false;
            _preferencesStore.Save(_preferences);
            ShowDemoWalkthrough();
        };
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


    private void MainChrome_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_uiReady || MainChrome.RowDefinitions.Count < 3 || MainChrome.ColumnDefinitions.Count < 2) return;

        // Fixed trainer layout: left column = coaching + attempts, right column = replay + mouse trace.
        // The sections scale inside the window instead of switching/repositioning on resize.
        MainChrome.ColumnDefinitions[0].Width = new GridLength(1.45, GridUnitType.Star);
        MainChrome.ColumnDefinitions[1].Width = new GridLength(1.0, GridUnitType.Star);
        MainChrome.RowDefinitions[0].Height = GridLength.Auto;
        MainChrome.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        MainChrome.RowDefinitions[2].Height = new GridLength(0);

        AttemptsGrid.MaxHeight = double.PositiveInfinity;
        TraceReplayCard.MinHeight = 0;

        Grid.SetColumn(CoachingCard, 0);
        Grid.SetColumnSpan(CoachingCard, 1);
        Grid.SetRow(CoachingCard, 0);

        Grid.SetColumn(AttemptsCard, 0);
        Grid.SetColumnSpan(AttemptsCard, 1);
        Grid.SetRow(AttemptsCard, 1);

        Grid.SetColumn(TraceReplayCard, 1);
        Grid.SetColumnSpan(TraceReplayCard, 1);
        Grid.SetRow(TraceReplayCard, 0);
        Grid.SetRowSpan(TraceReplayCard, 2);
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
        _attemptsView.Refresh();
        RefreshStats();
    }

    private void SelectLatestClickConfirmedAttempt()
    {
        if (AttemptsGrid.ItemsSource != _attemptsView) return;
        var latest = _analyzer.RecentAttempts.Where(a => a.HasClick).OrderByDescending(a => a.Index).FirstOrDefault();
        if (latest == null) return;
        if (!AttemptViewFilter(latest)) return;

        _attemptsView.Refresh();
        _selectedAttempt = latest;
        AttemptsGrid.SelectedItems.Clear();
        AttemptsGrid.SelectedItem = latest;
        AttemptsGrid.CurrentItem = latest;
        AttemptsGrid.ScrollIntoView(latest);
    }

    private void ReselectAttemptIfVisible(StrafeAttempt? attempt)
    {
        if (attempt == null) return;
        if (AttemptsGrid.ItemsSource != _attemptsView) return;
        if (!AttemptViewFilter(attempt)) return;
        if (!AttemptsGrid.Items.OfType<StrafeAttempt>().Contains(attempt)) return;
        if (ReferenceEquals(AttemptsGrid.SelectedItem, attempt)) return;
        _selectedAttempt = attempt;
        AttemptsGrid.SelectedItems.Clear();
        AttemptsGrid.SelectedItem = attempt;
        AttemptsGrid.CurrentItem = attempt;
        AttemptsGrid.ScrollIntoView(attempt);
    }

    private void ClearAttemptSelection()
    {
        AttemptsGrid.UnselectAll();
        AttemptsGrid.SelectedItem = null;
        _selectedAttempt = null;
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();
        RefreshTraceCanvas();
        TipText.Text = "Selection cleared.";
    }

    private bool TryMoveSelectedAttempt(int delta)
    {
        if (delta == 0) return false;
        if (ModeHostBorder.Visibility == Visibility.Visible) return false;
        if (MainChrome.Visibility != Visibility.Visible || AttemptsGrid.Visibility != Visibility.Visible) return false;
        if (AttemptsGrid.SelectedItems.Count == 0 && _selectedAttempt is null) return false;

        var visibleAttempts = AttemptsGrid.Items
            .OfType<StrafeAttempt>()
            .ToList();
        if (visibleAttempts.Count == 0) return false;

        var current = AttemptsGrid.SelectedItem as StrafeAttempt
            ?? _selectedAttempt
            ?? AttemptsGrid.SelectedItems.OfType<StrafeAttempt>().FirstOrDefault();
        if (current is null) return false;

        int index = visibleAttempts.IndexOf(current);
        if (index < 0) return false;

        int nextIndex = Math.Clamp(index + delta, 0, visibleAttempts.Count - 1);
        var next = visibleAttempts[nextIndex];

        AttemptsGrid.SelectedItems.Clear();
        AttemptsGrid.SelectedItem = next;
        AttemptsGrid.CurrentItem = next;
        AttemptsGrid.ScrollIntoView(next);
        AttemptsGrid.Focus();
        return true;
    }

    private void AttemptsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _selectedAttempt = AttemptsGrid.SelectedItem as StrafeAttempt;
        if (_guidedDemoActive) ApplyGuidedDemoAttemptRowDimming();
        StopReplay(resetToSelectedStart: false);
        ResetReplayForSelectedAttempt();
        RefreshTraceCanvas();
        TryAutoAdvanceGuidedDemoStep();
    }

    private async void AttemptsGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearAttemptSelection();
            e.Handled = true;
            return;
        }

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
            int saveSessionSerial = _analyzer.CurrentSessionSerial;
            var sessionAttempts = _analyzer.GetAttemptsForSession(saveSessionSerial);
            var sessionEvents = _analyzer.GetEventsForSession(saveSessionSerial);
            var summary = _analyzer.BuildSummaryForSession(saveSessionSerial, startedAt, endedAt);
            await _store.SaveAsync(summary, sessionEvents, sessionAttempts);
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
        TryAutoAdvanceGuidedDemoStep();
    }

    private void ReplayCustomSlowButton_Click(object sender, RoutedEventArgs e)
    {
        double slowdown = 5.0;
        if (double.TryParse(ReplayCustomSlowBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            slowdown = Math.Clamp(parsed, 1.0, 100.0);
        }
        ReplayCustomSlowBox.Text = slowdown.ToString("0.##", CultureInfo.InvariantCulture);
        StartReplay(slowdown);
        TryAutoAdvanceGuidedDemoStep();
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
        _manualReplayCursorMs = Math.Clamp(cursorMs, _replayWindowStartMs, _replayWindowEndMs);
        UpdateReplayVisual(_manualReplayCursorMs, isPlaying: false);
    }

    private void ReplayTimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedAttempt == null || !_selectedAttempt.HasClick) return;
        _replayAttempt = _selectedAttempt;
        SetReplayWindow(_replayAttempt);
        _replayTimer.Stop();
        _replayTimelineDragging = true;
        ReplayTimelineCanvas.CaptureMouse();
        SetReplayCursorFromPoint(e.GetPosition(ReplayTimelineCanvas));
        e.Handled = true;
    }

    private void ReplayTimelineCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_replayTimelineDragging) return;
        SetReplayCursorFromPoint(e.GetPosition(ReplayTimelineCanvas));
        e.Handled = true;
    }

    private void ReplayTimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_replayTimelineDragging) return;
        _replayTimelineDragging = false;
        ReplayTimelineCanvas.ReleaseMouseCapture();
        SetReplayCursorFromPoint(e.GetPosition(ReplayTimelineCanvas));
        e.Handled = true;
    }

    private void ReplayTimelineCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_replayTimelineDragging) return;
        _replayTimelineDragging = false;
        ReplayTimelineCanvas.ReleaseMouseCapture();
    }

    private void SetReplayCursorFromPoint(Point point)
    {
        if (_replayAttempt == null) return;
        double width = Math.Max(ReplayTimelineCanvas.ActualWidth, 360);
        double left = 18;
        double right = Math.Max(left + 20, width - 18);
        double f = Math.Clamp((point.X - left) / Math.Max(1, right - left), 0, 1);
        _manualReplayCursorMs = _replayWindowStartMs + f * (_replayWindowEndMs - _replayWindowStartMs);
        UpdateReplayVisual(_manualReplayCursorMs, isPlaying: false);
    }

    private void ReplayTimeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (_replayAttempt != null)
        {
            double cursorMs = _replayTimer.IsEnabled
                ? GetCurrentReplayAttemptTimeMs()
                : (!double.IsNaN(_manualReplayCursorMs) ? _manualReplayCursorMs : _replayWindowStartMs);
            _manualReplayCursorMs = cursorMs;
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
        if (IsGuidedDemoReplayActionStep && _replaySlowdown >= 5.0)
        {
            _guidedDemoFiveXReplayStarted = true;
            _guidedDemoFiveXReplayFinished = false;
        }
        SetReplayWindow(_replayAttempt);
        _manualReplayCursorMs = double.NaN;
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

        _manualReplayCursorMs = double.NaN;
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
        bool finishedReplay = false;
        if (cursorMs >= _replayWindowEndMs)
        {
            cursorMs = _replayWindowEndMs;
            _replayTimer.Stop();
            finishedReplay = true;
        }

        _manualReplayCursorMs = cursorMs;
        UpdateReplayVisual(cursorMs, _replayTimer.IsEnabled);
        if (finishedReplay && _replaySlowdown >= 5.0) TryAdvanceGuidedDemoAfterReplayFinished();
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
        if (attempt.CounterKeyUpTimeMs.HasValue) lastEventMs = Math.Max(lastEventMs, attempt.CounterKeyUpTimeMs.Value);
        if (attempt.ClickUpTimeMs.HasValue) lastEventMs = Math.Max(lastEventMs, attempt.ClickUpTimeMs.Value);
        _replayWindowStartMs = Math.Max(0, firstEventMs - 80);
        _replayWindowEndMs = Math.Max(lastEventMs + 120, _replayWindowStartMs + 220);
    }

    private double GetCounterKeyEndMs(StrafeAttempt attempt)
    {
        if (attempt.CounterKeyUpTimeMs.HasValue)
        {
            return Math.Clamp(attempt.CounterKeyUpTimeMs.Value, attempt.OppositeDownTimeMs, _replayWindowEndMs);
        }

        if (attempt.ClickTimeMs.HasValue)
        {
            bool heldAtClick = !string.IsNullOrWhiteSpace(attempt.HeldKeysAtClick) &&
                attempt.HeldKeysAtClick.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(attempt.ToKey);
            return heldAtClick ? _replayWindowEndMs : Math.Max(attempt.OppositeDownTimeMs, attempt.ClickTimeMs.Value - 1);
        }

        return _replayWindowEndMs;
    }

    private bool IsCounterKeyDownAt(StrafeAttempt attempt, double cursorMs)
    {
        return cursorMs >= attempt.OppositeDownTimeMs && cursorMs <= GetCounterKeyEndMs(attempt);
    }

    private double GetClickEndMs(StrafeAttempt attempt)
    {
        if (!attempt.ClickTimeMs.HasValue) return _replayWindowEndMs;
        if (attempt.ClickUpTimeMs.HasValue && attempt.ClickUpTimeMs.Value >= attempt.ClickTimeMs.Value)
        {
            return Math.Clamp(attempt.ClickUpTimeMs.Value, attempt.ClickTimeMs.Value, _replayWindowEndMs);
        }

        return Math.Min(_replayWindowEndMs, attempt.ClickTimeMs.Value + 28);
    }

    private bool IsM1DownAt(StrafeAttempt attempt, double cursorMs)
    {
        return attempt.ClickTimeMs.HasValue && cursorMs >= attempt.ClickTimeMs.Value && cursorMs <= GetClickEndMs(attempt);
    }

    private void UpdateReplayVisual(double cursorMs, bool isPlaying)
    {
        if (_replayAttempt == null) return;
        var attempt = _replayAttempt;

        bool fromKeyDown = cursorMs < attempt.ReleaseTimeMs;
        bool toKeyDown = IsCounterKeyDownAt(attempt, cursorMs);
        bool m1Down = IsM1DownAt(attempt, cursorMs);

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
        ReplayStatusText.Text = $"{running} · #{attempt.Index} {attempt.Direction} · counter delay {attempt.CounterDelayMs:+0.0;-0.0;0.0} ms · table mistake: {attempt.MistakeLabel} · {phase}";
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
            label.Text = "click";
            return;
        }

        if (isDown && isWarning)
        {
            box.Background = BrushFromRgb(76, 58, 20);
            box.BorderBrush = BrushFromRgb(255, 206, 69);
            label.Text = "down";
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

        double counterKeyEndMs = GetCounterKeyEndMs(attempt);
        if (attempt.ToKey == "A")
            DrawReplayInterval(attempt.OppositeDownTimeMs, counterKeyEndMs, aY, laneH, BrushFromRgb(0, 224, 255));
        else
            DrawReplayInterval(attempt.OppositeDownTimeMs, counterKeyEndMs, dY, laneH, BrushFromRgb(0, 224, 255));

        if (attempt.ClickTimeMs.HasValue)
        {
            DrawReplayInterval(attempt.ClickTimeMs.Value, GetClickEndMs(attempt), mY, laneH, BrushFromRgb(255, 255, 255));
        }

        DrawReplayMarker(attempt.ReleaseTimeMs, $"{attempt.FromKey} up", BrushFromRgb(255, 206, 69));
        DrawReplayMarker(attempt.OppositeDownTimeMs, $"{attempt.ToKey} down", BrushFromRgb(0, 224, 255));
        if (attempt.CounterKeyUpTimeMs.HasValue) DrawReplayMarker(attempt.CounterKeyUpTimeMs.Value, $"{attempt.ToKey} up", BrushFromRgb(255, 206, 69));
        if (attempt.ClickTimeMs.HasValue) DrawReplayMarker(attempt.ClickTimeMs.Value, "M1", BrushFromRgb(255, 255, 255));
        if (attempt.ClickUpTimeMs.HasValue) DrawReplayMarker(attempt.ClickUpTimeMs.Value, "M1 up", BrushFromRgb(148, 163, 184));

        DrawReplayTimeTicks();

        double x = ReplayX(cursorMs);
        ReplayTimelineCanvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = height, Stroke = BrushFromRgb(255, 92, 122), StrokeThickness = 2 });

        void DrawReplayTimeTicks()
        {
            const int tickCount = 5;
            double duration = Math.Max(1, _replayWindowEndMs - attempt.ReleaseTimeMs);
            for (int i = 0; i < tickCount; i++)
            {
                double f = tickCount == 1 ? 0 : i / (double)(tickCount - 1);
                double relativeMs = duration * f;
                double ms = attempt.ReleaseTimeMs + relativeMs;
                double tx = ReplayX(ms);
                ReplayTimelineCanvas.Children.Add(new Line { X1 = tx, Y1 = height - 12, X2 = tx, Y2 = height - 2, Stroke = new SolidColorBrush(Color.FromArgb(115, 148, 163, 184)), StrokeThickness = 1 });
                var label = new TextBlock { Text = $"{relativeMs:0} ms", Foreground = new SolidColorBrush(Color.FromArgb(165, 168, 176, 200)), FontSize = 10 };
                Canvas.SetLeft(label, Math.Min(width - 38, Math.Max(0, tx - 14)));
                Canvas.SetTop(label, height - 28);
                ReplayTimelineCanvas.Children.Add(label);
            }
        }

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
        bool toDown = IsCounterKeyDownAt(attempt, cursorMs);
        bool bothDown = fromDown && toDown;
        bool neitherDown = !fromDown && !toDown;

        if (IsM1DownAt(attempt, cursorMs))
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
        _analyzer.RegradeAllAttempts();

        UseColumn.Visibility = Visibility.Collapsed;
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
            // Hotkeys are handled locally so typed characters are not blocked globally.
            // Re-registering with Windows is intentionally disabled.
        }
    }

    private void UpdatePrimarySessionButtons()
    {
        if (!_uiReady) return;

        bool inPeek = IsPeekModeVisible;
        bool peekActive = inPeek && _peekModeView?.IsActive == true;

        StartButton.Content = inPeek ? $"Start [{_preferences.StartStopHotkey}]" : $"Start [{_preferences.StartStopHotkey}]";
        StopButton.Content = inPeek ? $"Stop [{_preferences.StartStopHotkey}]" : $"Stop + Save [{_preferences.StartStopHotkey}]";
        PeekButton.Content = inPeek ? "Gameplay mode" : "Peek mode";
        ShowLastButton.Visibility = inPeek ? Visibility.Collapsed : Visibility.Visible;
        ArmHeaderButton.Visibility = Visibility.Collapsed;

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
        ShowLastButton.IsEnabled = !IsPeekModeVisible && !_stopSaveInProgress;

        if (IsPeekModeVisible)
        {
            bool hasPeekData = _peekModeView?.HasData == true;
            OpenConclusionsButton.IsEnabled = false;
            ReportsButton.IsEnabled = false;
        ReportsButton.Visibility = Visibility.Collapsed;
            HeaderConclusionsButton.IsEnabled = hasPeekData;
            HeaderConclusionsButton.ToolTip = hasPeekData ? "Open Peek mode conclusions." : "Record at least one Peek mode attempt first.";
            return;
        }

        bool hasIncludedAttempts = _analyzer.RecentAttempts.Any(a => _analyzer.IsAttemptIncludedInStats(a));
        bool canReview = !_active && !_stopSaveInProgress && hasIncludedAttempts;
        OpenConclusionsButton.IsEnabled = canReview;
        ReportsButton.IsEnabled = false;
        ReportsButton.Visibility = Visibility.Collapsed;
        HeaderConclusionsButton.IsEnabled = canReview;
        HeaderConclusionsButton.ToolTip = canReview ? "Open session conclusions." : "Record at least one included attempt first.";
    }

    private void UpdateCloudButtons()
    {
        if (!_uiReady) return;
        AccountButton.Content = _supabase.IsSignedIn ? _supabase.DisplayName : "Account";
        AccountButton.ToolTip = _supabase.IsSignedIn ? "Open your profile." : "Sign in, create an account, or share anonymous practice stats.";
        ClansButton.IsEnabled = _supabase.IsSignedIn;
        ClansButton.ToolTip = _supabase.IsSignedIn ? "Open personal rankings, clan stats, and invites." : "Sign in before opening rankings.";
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
        if (key.Length == 1 && key[0] is >= '0' and <= '9') return key[0];
        if (key.StartsWith("F", StringComparison.Ordinal) && int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }
        return key switch
        {
            "SPACE" => 0x20,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "-" => 0xBD,
            "=" => 0xBB,
            "`" => 0xC0,
            _ => fallback
        };
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
        _settings.CleanFastMaxTotalMs = _preferences.CleanFastMaxTotalMs;
        _settings.CleanPerfectMaxTotalMs = Math.Max(_settings.CleanFastMaxTotalMs, _preferences.CleanPerfectMaxTotalMs);
        _settings.CleanJustInTimeMinTotalMs = Math.Max(_settings.CleanPerfectMaxTotalMs, _preferences.CleanJustInTimeMinTotalMs);
        _settings.MaxAttemptPairWindowMs = _preferences.CounterPairWindowMs;
        _settings.MouseTraceMaxMs = _preferences.MouseTraceMaxMs;
        _settings.MouseTraceMaxPoints = _preferences.MouseTraceMaxPoints;

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

    private string CurrentFocus => "Overall";

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
        TipText.Text = _analyzer.GetCoachingTip();
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
        var selectedAttempts = AttemptsGrid.SelectedItems
            .OfType<StrafeAttempt>()
            .Where(a => a.MouseTrace.Count > 0)
            .Distinct()
            .ToList();

        bool compareSelectedOnly = selectedAttempts.Count > 1;
        var selectedAttempt = selectedAttempts.Count == 1
            ? selectedAttempts[0]
            : (_selectedAttempt?.MouseTrace.Count > 0 ? _selectedAttempt : null);

        var attempts = compareSelectedOnly
            ? selectedAttempts
            : _analyzer.RecentAttempts
                .Where(a => _analyzer.IsAttemptIncludedInStats(a))
                .Where(a => a.MouseTrace.Count > 0)
                .Take(maxTraces)
                .ToList();

        if (!compareSelectedOnly && selectedAttempt != null && !attempts.Contains(selectedAttempt))
        {
            attempts.Insert(0, selectedAttempt);
        }

        if (attempts.Count == 0)
        {
            DrawAxes(width, height, 1.0);
            TraceStatsText.Text = "No mouse trace yet. Start a session, counter-strafe, move the mouse, then click.";
            return;
        }

        var tracePairs = attempts
            .Select(a => new { Attempt = a, Trace = BuildTrace(a) })
            .Where(t => t.Trace.Count > 1)
            .ToList();

        if (tracePairs.Count == 0)
        {
            DrawAxes(width, height, 1.0);
            TraceStatsText.Text = "Waiting for movement points between counter key and click.";
            return;
        }

        var scaleTraces = tracePairs.Select(t => t.Trace).ToList();
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
            for (int i = tracePairs.Count - 1; i >= 0; i--)
            {
                bool isSelected = selectedAttempt != null && ReferenceEquals(tracePairs[i].Attempt, selectedAttempt);
                var brush = compareSelectedOnly ? TraceBrush(i) : TraceBrush(i);
                double thickness = compareSelectedOnly ? 2.8 : isSelected ? 4.4 : 1.35;
                DrawPolyline(tracePairs[i].Trace, width, height, scale, brush, thickness);
                if (ShowClickMarkersCheckBox.IsChecked == true)
                {
                    DrawEndpoint(tracePairs[i].Trace[^1], width, height, scale, brush, compareSelectedOnly ? 4.5 : isSelected ? 6.5 : 3.0);
                }
            }
        }

        if (!compareSelectedOnly && ShowAverageLineCheckBox.IsChecked == true && tracePairs.Count >= 2)
        {
            var averageTrace = BuildAverageTrace(tracePairs.Select(t => t.Trace).ToList(), 64);
            DrawPolyline(averageTrace, width, height, scale, new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 3.0);
            if (ShowClickMarkersCheckBox.IsChecked == true)
            {
                DrawEndpoint(averageTrace[^1], width, height, scale, new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 4.0);
            }
        }

        if (!compareSelectedOnly && selectedAttempt != null)
        {
            var selectedTrace = BuildTrace(selectedAttempt);
            if (selectedTrace.Count > 1)
            {
                var highlightBrush = Application.Current.Resources["TraceHighlightBrush"] as Brush ?? new SolidColorBrush(Color.FromRgb(0, 224, 255));
                DrawPolyline(selectedTrace, width, height, scale, highlightBrush, 4.8);
                if (ShowClickMarkersCheckBox.IsChecked == true)
                {
                    DrawEndpoint(selectedTrace[^1], width, height, scale, highlightBrush, 6.8);
                }
            }
        }

        if (compareSelectedOnly)
        {
            TraceStatsText.Text = $"Comparing {tracePairs.Count} selected attempts only · zoom {_traceZoom * 100:0}% · wheel to zoom, drag to pan";
            return;
        }

        var subject = selectedAttempt ?? tracePairs[0].Attempt;
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
