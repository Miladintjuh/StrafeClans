using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StrafeLab.Models;

namespace StrafeLab;

public partial class PeekModeView : UserControl
{
    private readonly PeekAnalyzer _analyzer = new();
    private readonly ObservableCollection<PeekAttempt> _visibleAttempts = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _wallClock = new();
    private double? _sessionStartRawMs;
    private bool _active;
    private bool _uiReady;
    private PeekAttempt? _selectedAttempt;
    private bool _manualRepMode = false;
    private bool _singleRepCaptureActive;
    private bool _continuousSessionEverStarted;
    private AppPreferences _preferences;
    public event Action? RequestLiveTrainerView;
    public event Action? StateChanged;

    public bool IsActive => _active;
    public bool HasData => _analyzer.Attempts.Count > 0;

    public PeekModeView(AppPreferences preferences)
    {
        _preferences = preferences;
        InitializeComponent();
        PeekGrid.ItemsSource = _visibleAttempts;
        _analyzer.AttemptChanged += Analyzer_AttemptChanged;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        _uiReady = true;
        ApplyPreferences(_preferences);
        RefreshUi();
    }

    public void ApplyPreferences(AppPreferences preferences)
    {
        _preferences = preferences;
        CleanMaxBox.Text = preferences.PeekCleanMaxMs.ToString(CultureInfo.InvariantCulture);
        OverlapToleranceBox.Text = preferences.PeekOverlapToleranceMs.ToString(CultureInfo.InvariantCulture);
        SprayHoldBox.Text = preferences.PeekSprayHoldMs.ToString(CultureInfo.InvariantCulture);
        ApplySettingsFromUi();
        DrawTimingChart();
        DrawPeekConclusions();
    }

    public void StartContinuousFromHeader() => StartPeekMode(manualMode: false);
    public void StopContinuousFromHeader() => StopPeekMode();
    public void ShowConclusionsFromHeader() => ShowPeekConclusions();

    public void ReceiveRawInput(InputEventRecord raw)
    {
        if (!_active && !_singleRepCaptureActive)
        {
            if (raw.Kind == InputKind.KeyDown && (raw.Code == "W" || raw.Code == "S") && SingleRepOverlay?.Visibility == Visibility.Visible)
            {
                ArmSingleAttemptFromHotkey();
            }
            return;
        }

        _sessionStartRawMs ??= raw.SessionTimeMs;
        var e = new InputEventRecord
        {
            WallTime = raw.WallTime,
            SessionTimeMs = raw.SessionTimeMs - _sessionStartRawMs.Value,
            Code = raw.Code,
            Kind = raw.Kind,
            DeltaX = raw.DeltaX,
            DeltaY = raw.DeltaY
        };

        ApplySettingsFromUi();
        _analyzer.Add(e);
    }


    public void ArmSingleAttemptFromHotkey()
    {
        ManualRepCheckBox.IsChecked = true;
        ApplySettingsFromUi();

        if (!_active)
        {
            _analyzer.Reset();
            _visibleAttempts.Clear();
            _selectedAttempt = null;
            PeekGrid.SelectedItem = null;
            _sessionStartRawMs = null;
            _singleRepCaptureActive = true;
            _wallClock.Restart();
            SingleRepOverlay.Visibility = Visibility.Collapsed;
        }

        _analyzer.ArmSingleAttempt();
        ArmStateText.Text = "recording one rep";
        StatusText.Text = "Armed rep";
        SelectedSummaryText.Text = "Do one full peek: step out, counter-strafe, shoot, then reset. The result opens here without saving a session.";
        CoachingText.Text = "One rep is armed. Press W or S after a result to arm the next rep and clear the previous one.";
        SystemSounds.Asterisk.Play();
        StateChanged?.Invoke();
        RefreshUi();
    }

    public void ToggleContinuousSessionFromHotkey()
    {
        if (_active)
        {
            SystemSounds.Exclamation.Play();
            StopPeekMode();
            return;
        }

        SystemSounds.Asterisk.Play();
        StartPeekMode(manualMode: false);
    }

    private void ArmButton_Click(object sender, RoutedEventArgs e) => ArmSingleAttemptFromHotkey();

    private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearAll();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => RequestLiveTrainerView?.Invoke();
    private void LiveViewButton_Click(object sender, RoutedEventArgs e) => ShowPeekLive();
    private void ConclusionsButton_Click(object sender, RoutedEventArgs e) => ShowPeekConclusions();

    private void ShowPeekLive()
    {
        if (PeekLivePanel == null || PeekConclusionsPanel == null) return;
        PeekLivePanel.Visibility = Visibility.Visible;
        PeekConclusionsPanel.Visibility = Visibility.Collapsed;
        DrawTimingChart();
        DrawSelectedMouseTrace();
    }

    public void ShowPeekConclusions()
    {
        if (PeekLivePanel == null || PeekConclusionsPanel == null) return;
        PeekLivePanel.Visibility = Visibility.Collapsed;
        PeekConclusionsPanel.Visibility = Visibility.Visible;
        DrawPeekConclusions();
    }

    private void StartPeekMode(bool? manualMode = null)
    {
        if (manualMode.HasValue)
        {
            ManualRepCheckBox.IsChecked = manualMode.Value;
        }

        ApplySettingsFromUi();
        _analyzer.Reset();
        _visibleAttempts.Clear();
        _selectedAttempt = null;
        _sessionStartRawMs = null;
        _active = true;
        _continuousSessionEverStarted = true;
        _singleRepCaptureActive = false;
        SingleRepOverlay.Visibility = Visibility.Collapsed;
        _wallClock.Restart();
        StatusText.Text = "Live";
        ArmStateText.Text = _manualRepMode ? "Press F8 / Arm rep" : "Auto-detecting";
        CoachingText.Text = _manualRepMode
            ? "Peek mode is live in manual-rep mode. Press F8, do one full peek, then review the result."
            : "Continuous peek session is live. Step out with A/D, counter with the opposite key, shoot, then move back to reset. The next rep starts after you release the reset movement.";
        StateChanged?.Invoke();
        RefreshUi();
    }

    private void StopPeekMode()
    {
        if (!_active) return;
        ApplySettingsFromUi();
        _active = false;
        _continuousSessionEverStarted = false;
        _singleRepCaptureActive = false;
        _wallClock.Stop();
        _analyzer.FlushCurrentAttempt();
        StatusText.Text = "Stopped";
        StateChanged?.Invoke();
        RefreshUi();
    }

    private void ClearAll()
    {
        _analyzer.Reset();
        _visibleAttempts.Clear();
        _selectedAttempt = null;
        _sessionStartRawMs = null;
        _wallClock.Reset();
        _active = false;
        _continuousSessionEverStarted = false;
        _singleRepCaptureActive = false;
        SingleRepOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = "Idle";
        ElapsedText.Text = "not recording";
        ArmStateText.Text = "not armed";
        SelectedSummaryText.Text = "Select a row to see the exact mistake and what to practice.";
        CoachingText.Text = "Start peek mode and record a few left/right reps. Coaching will compare directions and shot patterns.";
        StateChanged?.Invoke();
        RefreshUi();
    }

    private void Analyzer_AttemptChanged(PeekAttempt attempt)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_visibleAttempts.Contains(attempt))
            {
                _visibleAttempts.Insert(0, attempt);
            }

            if (_singleRepCaptureActive && attempt.HasClick && attempt.HasCounter)
            {
                _singleRepCaptureActive = false;
                _selectedAttempt = attempt;
                PeekGrid.SelectedItem = attempt;
                ShowSingleRepResult(attempt);
            }

            PeekGrid.Items.Refresh();
            ApplyFiltersToGrid();
            DrawTimingChart();
            DrawSelectedMouseTrace();
            StateChanged?.Invoke();
            RefreshUi();
        });
    }

    private void ShowSingleRepResult(PeekAttempt attempt)
    {
        attempt.Recalculate();
        string timing = attempt.TimingClass switch
        {
            PeekTimingClass.Clean => "Clean counter timing",
            PeekTimingClass.Overlap => "Overlap in the handoff",
            PeekTimingClass.LateGap => "Late counter gap",
            _ => "Timing pending"
        };
        string shot = attempt.ShotPatternLabel;
        SingleRepTitleText.Text = $"Rep #{attempt.Index}: {timing}";
        SingleRepSummaryText.Text = $"{attempt.StepLabel} · {attempt.CounterDelayLabel} counter · {shot} · {attempt.MouseActionLabel} mouse";
        SingleRepAdviceText.Text = attempt.Advice;
        SingleRepOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "Rep complete";
        ArmStateText.Text = "W/S or F8 re-arms";
        CoachingText.Text = "Review the single-rep result. Press W or S to arm the next rep and replace this data.";
    }

    private void SingleRepCloseButton_Click(object sender, RoutedEventArgs e)
    {
        SingleRepOverlay.Visibility = Visibility.Collapsed;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        ApplySettingsFromUi();
        ApplyFiltersToGrid();
        RefreshUi();
    }

    private void PeekGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedAttempt = PeekGrid.SelectedItem as PeekAttempt;
        UpdateSelectedAttemptText();
        DrawTimingChart();
        DrawSelectedMouseTrace();
    }

    private void PeekGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        var selected = PeekGrid.SelectedItems.OfType<PeekAttempt>().ToList();
        if (selected.Count == 0) return;

        int removed = _analyzer.RemoveAttempts(selected);
        foreach (var attempt in selected) _visibleAttempts.Remove(attempt);
        if (_selectedAttempt is not null && selected.Contains(_selectedAttempt)) _selectedAttempt = null;
        PeekGrid.SelectedItem = null;
        e.Handled = true;
        CoachingText.Text = removed == 1 ? "Deleted selected Peek attempt." : $"Deleted {removed} selected Peek attempts.";
        StateChanged?.Invoke();
        RefreshUi();
    }

    private void PeekLivePanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PeekChartRow == null || PeekGrid == null) return;
        double h = Math.Max(1, PeekLivePanel.ActualHeight);
        if (h < 560)
        {
            PeekChartRow.Height = new GridLength(230);
            PeekGrid.FontSize = 10;
            PeekGrid.RowHeight = 22;
        }
        else if (h < 680)
        {
            PeekChartRow.Height = new GridLength(270);
            PeekGrid.FontSize = 11;
            PeekGrid.RowHeight = 24;
        }
        else
        {
            PeekChartRow.Height = new GridLength(320);
            PeekGrid.FontSize = 12;
            PeekGrid.RowHeight = 26;
        }
    }

    private void TimingChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTimingChart();
    private void SelectedMouseCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSelectedMouseTrace();
    private void PeekConclusionCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPeekConclusions();

    private void ApplySettingsFromUi()
    {
        _analyzer.CleanMaxMs = ParseBox(CleanMaxBox.Text, 45);
        _analyzer.OverlapToleranceMs = ParseBox(OverlapToleranceBox.Text, 8);
        _analyzer.SprayHoldMs = ParseBox(SprayHoldBox.Text, 180);
        _analyzer.MaxMouseTraceMs = _preferences.PeekMouseTraceMaxMs;
        _analyzer.MaxMouseTracePoints = _preferences.PeekMouseTraceMaxPoints;
        _analyzer.ResetMouseAfterClickMs = _preferences.PeekResetMouseAfterClickMs;
        _manualRepMode = ManualRepCheckBox?.IsChecked != false;
        _analyzer.ManualSingleAttemptMode = _manualRepMode;
    }

    private void RefreshUi()
    {
        if (!_uiReady) return;
        ApplySettingsFromUi();

        if (_singleRepCaptureActive)
        {
            StatusText.Text = _analyzer.IsArmed ? "Armed rep" : "Capturing rep";
            ElapsedText.Text = "single attempt";
            ArmStateText.Text = _analyzer.IsArmed ? "waiting for step-out" : "recording one rep";
        }
        else if (_active)
        {
            StatusText.Text = _analyzer.IsArmed ? "Armed" : "Live";
            ElapsedText.Text = _wallClock.Elapsed.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
            ArmStateText.Text = _manualRepMode
                ? (_analyzer.IsArmed ? "recording next peek" : "press F8 / Arm rep")
                : "auto-detecting";
        }
        else if (_analyzer.Attempts.Count == 0)
        {
            StatusText.Text = "Idle";
            ElapsedText.Text = "not recording";
            ArmStateText.Text = "not armed";
        }
        else
        {
            ElapsedText.Text = "stopped";
        }

        var attempts = _analyzer.Attempts.ToList();
        ClearButton.IsEnabled = attempts.Count > 0;
        LiveViewButton.IsEnabled = _continuousSessionEverStarted;
        ConclusionsButton.IsEnabled = attempts.Count > 0;
        AttemptCountText.Text = attempts.Count.ToString(CultureInfo.InvariantCulture);
        int sprays = attempts.Count(a => a.IsSpray);
        int oneTaps = attempts.Count(a => a.IsOneTap);
        int multiTaps = attempts.Count(a => !a.IsSpray && a.ClickTimesMs.Count > 1);
        double avgShotSpeed = attempts.Where(a => a.FirstClickAfterCounterMs.HasValue).Select(a => a.FirstClickAfterCounterMs!.Value).DefaultIfEmpty(0).Average();
        ShotPatternText.Text = $"{oneTaps} one taps · {multiTaps} multi · {sprays} sprays · shot {avgShotSpeed:0} ms";

        SetDirectionCard(attempts.Where(a => a.StepKey == "A").ToList(), LeftRateText, LeftDetailText, "left");
        SetDirectionCard(attempts.Where(a => a.StepKey == "D").ToList(), RightRateText, RightDetailText, "right");
        UpdateFocusAdvice(attempts);
        UpdateSelectedAttemptText();
        ApplyFiltersToGrid();
        DrawTimingChart();
        DrawSelectedMouseTrace();
        DrawPeekConclusions();
    }

    private void SetDirectionCard(IReadOnlyList<PeekAttempt> attempts, TextBlock rateText, TextBlock detailText, string label)
    {
        if (attempts.Count == 0)
        {
            rateText.Text = "0%";
            detailText.Text = $"0 {label} attempts";
            return;
        }

        int clean = attempts.Count(a => a.TimingClass == PeekTimingClass.Clean);
        double rate = clean * 100.0 / attempts.Count;
        double avg = attempts.Where(a => a.CounterDelayMs.HasValue).Select(a => a.CounterDelayMs!.Value).DefaultIfEmpty(0).Average();
        double shot = attempts.Where(a => a.FirstClickAfterCounterMs.HasValue).Select(a => a.FirstClickAfterCounterMs!.Value).DefaultIfEmpty(0).Average();
        double oneTapRate = attempts.Count(a => a.IsOneTap) * 100.0 / attempts.Count;
        rateText.Text = $"{rate:0}%";
        detailText.Text = $"{clean}/{attempts.Count} clean · c {avg:+0.0;-0.0;0.0} ms · shot {shot:0} ms · 1tap {oneTapRate:0}%";
    }

    private void UpdateFocusAdvice(IReadOnlyList<PeekAttempt> attempts)
    {
        if (attempts.Count < 4)
        {
            FocusText.Text = "record reps";
            FocusDetailText.Text = "need a few left/right attempts";
            CoachingText.Text = "Record at least 4-6 click-confirmed peeks. The chart will separate step-left and step-right timing.";
            return;
        }

        var groups = attempts.GroupBy(a => a.StepKey).ToDictionary(g => g.Key, g => g.ToList());
        string worstDirection = "overall";
        double worstCleanRate = 101;
        foreach (var pair in groups)
        {
            if (pair.Value.Count < 2) continue;
            double cleanRate = pair.Value.Count(a => a.TimingClass == PeekTimingClass.Clean) * 100.0 / pair.Value.Count;
            if (cleanRate < worstCleanRate)
            {
                worstCleanRate = cleanRate;
                worstDirection = pair.Key == "A" ? "step left" : "step right";
            }
        }

        var late = attempts.Count(a => a.TimingClass == PeekTimingClass.LateGap);
        var overlap = attempts.Count(a => a.TimingClass == PeekTimingClass.Overlap);
        var sprays = attempts.Count(a => a.IsSpray);
        var messy = attempts.Count(a => a.MouseAction == PeekMouseAction.Messy || a.MouseAction == PeekMouseAction.Overflick);

        FocusText.Text = worstDirection;
        string timingAdvice = overlap > late
            ? "main timing issue: overlap. Release the step-out key sooner or reduce overlap from rapid-trigger settings."
            : late > overlap
                ? "main timing issue: late/gap. Press the counter key sooner; your release and counter press are too separated."
                : "timing is balanced; work on consistency and shot discipline.";
        string shotAdvice = sprays > attempts.Count * 0.35 ? " Many reps become sprays; decide whether the drill target is 1-tap/burst or controlled spray." : "";
        string mouseAdvice = messy > attempts.Count * 0.35 ? " Mouse paths are often inefficient; focus on matching bot speed then making a small final correction." : "";
        FocusDetailText.Text = timingAdvice;
        CoachingText.Text = $"Practice focus: {worstDirection}. {timingAdvice}{shotAdvice}{mouseAdvice}";
    }

    private void UpdateSelectedAttemptText()
    {
        if (_selectedAttempt == null)
        {
            SelectedSummaryText.Text = "Select a row to see the exact mistake and what to practice.";
            return;
        }

        SelectedSummaryText.Text = $"#{_selectedAttempt.Index} · {_selectedAttempt.StepLabel} · {_selectedAttempt.CounterLabel} · {_selectedAttempt.CounterDelayLabel} · shot {_selectedAttempt.ShotSpeedLabel} · {_selectedAttempt.ShotPatternLabel}. {_selectedAttempt.Advice}";
    }

    private void ApplyFiltersToGrid()
    {
        if (PeekGrid.ItemsSource == null) return;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(PeekGrid.ItemsSource);
        view.Filter = o =>
        {
            if (o is not PeekAttempt a) return false;
            if (a.StepKey == "A" && ShowLeftCheckBox.IsChecked == false) return false;
            if (a.StepKey == "D" && ShowRightCheckBox.IsChecked == false) return false;
            if (a.IsSpray && ShowSpraysCheckBox.IsChecked == false) return false;
            if (!a.IsSpray && ShowTapsCheckBox.IsChecked == false) return false;
            return true;
        };
        view.Refresh();
    }

    private List<PeekAttempt> GetFilteredAttemptsInOrder()
    {
        return _analyzer.Attempts
            .Where(a => a.CounterDelayMs.HasValue)
            .Where(a => a.StepKey != "A" || ShowLeftCheckBox.IsChecked != false)
            .Where(a => a.StepKey != "D" || ShowRightCheckBox.IsChecked != false)
            .Where(a => !a.IsSpray || ShowSpraysCheckBox.IsChecked != false)
            .Where(a => a.IsSpray || ShowTapsCheckBox.IsChecked != false)
            .OrderBy(a => a.Index)
            .ToList();
    }

    private void DrawTimingChart()
    {
        if (TimingChartCanvas == null) return;
        TimingChartCanvas.Children.Clear();
        double width = TimingChartCanvas.ActualWidth;
        double height = TimingChartCanvas.ActualHeight;
        if (width < 80 || height < 80) return;

        var attempts = GetFilteredAttemptsInOrder();
        double left = 58;
        double right = width - 24;
        double top = 36;
        double bottom = height - 38;
        double plotW = Math.Max(10, right - left);
        double plotH = Math.Max(10, bottom - top);

        double cleanMax = Math.Max(1, _analyzer.CleanMaxMs);
        double overlapTol = Math.Max(0, _analyzer.OverlapToleranceMs);
        double minY = -Math.Max(60, overlapTol + 42);
        double maxY = Math.Max(60, cleanMax + 42);
        var inlierDelays = attempts
            .Select(a => a.CounterDelayMs ?? 0)
            .Where(v => v >= minY && v <= maxY)
            .ToList();
        int outlierCount = attempts.Count - inlierDelays.Count;
        double chartAverage = inlierDelays.Count > 0
            ? inlierDelays.Average()
            : attempts.Select(a => a.CounterDelayMs ?? 0).DefaultIfEmpty(0).Average();

        DrawChartGrid(left, right, top, bottom, minY, maxY);
        DrawCleanBand(left, right, top, bottom, minY, maxY, -overlapTol, cleanMax);

        var title = new TextBlock
        {
            Text = attempts.Count == 0
                ? "Peek c-strafe timing"
                : outlierCount > 0
                    ? $"Peek c-strafe timing · avg {chartAverage:+0.0;-0.0;0.0} ms · {outlierCount} outlier{(outlierCount == 1 ? "" : "s")}"
                    : $"Peek c-strafe timing · avg {chartAverage:+0.0;-0.0;0.0} ms",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        };
        Canvas.SetLeft(title, left);
        Canvas.SetTop(title, 10);
        TimingChartCanvas.Children.Add(title);

        if (attempts.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = _manualRepMode ? "Press F8 / Arm rep, then perform one complete peek." : "No click-confirmed peek attempts yet.",
                Foreground = BrushFromArgb(200, 168, 176, 200),
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(empty, left + 20);
            Canvas.SetTop(empty, top + plotH / 2 - 8);
            TimingChartCanvas.Children.Add(empty);
            return;
        }

        for (int i = 0; i < attempts.Count; i++)
        {
            var a = attempts[i];
            double x = attempts.Count == 1 ? left + plotW / 2 : left + i * plotW / (attempts.Count - 1);
            double rawYValue = a.CounterDelayMs!.Value;
            bool highOutlier = rawYValue > maxY;
            bool lowOutlier = rawYValue < minY;
            double y = Y(Math.Clamp(rawYValue, minY, maxY));
            bool selected = ReferenceEquals(a, _selectedAttempt);
            double r = selected ? 7 : a.IsSpray ? 5.5 : 4.6;

            if (highOutlier || lowOutlier)
            {
                var arrow = new Polygon
                {
                    Fill = DirectionBrush(a.StepKey, a.IsSpray),
                    Stroke = selected ? Brushes.White : TimingClassBrush(a.TimingClass),
                    StrokeThickness = selected ? 2.4 : 1.2,
                    ToolTip = $"#{a.Index} outlier {a.CounterDelayLabel}: shot {a.ShotSpeedLabel}, {a.ShotPatternLabel}"
                };
                if (highOutlier)
                {
                    arrow.Points.Add(new Point(x, top + 3));
                    arrow.Points.Add(new Point(x - 7, top + 17));
                    arrow.Points.Add(new Point(x + 7, top + 17));
                }
                else
                {
                    arrow.Points.Add(new Point(x, bottom - 3));
                    arrow.Points.Add(new Point(x - 7, bottom - 17));
                    arrow.Points.Add(new Point(x + 7, bottom - 17));
                }
                TimingChartCanvas.Children.Add(arrow);
                continue;
            }

            var dot = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = DirectionBrush(a.StepKey, a.IsSpray),
                Stroke = selected ? Brushes.White : TimingClassBrush(a.TimingClass),
                StrokeThickness = selected ? 2.4 : 1.2,
                ToolTip = $"#{a.Index} {a.StepLabel}: counter {a.CounterDelayLabel}, shot {a.ShotSpeedLabel}, {a.ShotPatternLabel}"
            };
            Canvas.SetLeft(dot, x - r);
            Canvas.SetTop(dot, y - r);
            TimingChartCanvas.Children.Add(dot);
        }

        AddLegend(left, bottom + 12);

        double Y(double value)
        {
            double f = (value - minY) / Math.Max(1, maxY - minY);
            return bottom - f * plotH;
        }

        void DrawChartGrid(double l, double r, double t, double b, double low, double high)
        {
            var gridBrush = BrushFromArgb(70, 148, 163, 184);
            var axisBrush = BrushFromArgb(180, 226, 232, 255);
            foreach (double val in new[] { -50.0, 0.0, 50.0 })
            {
                if (val < low || val > high) continue;
                double y = Y(val);
                TimingChartCanvas.Children.Add(new Line { X1 = l, Y1 = y, X2 = r, Y2 = y, Stroke = val == 0 ? axisBrush : gridBrush, StrokeThickness = val == 0 ? 1.5 : 1 });
                var label = new TextBlock { Text = val > 0 ? $"+{val:0}" : val.ToString("0", CultureInfo.InvariantCulture), Foreground = BrushFromArgb(210, 226, 232, 255), FontSize = 11 };
                Canvas.SetLeft(label, 18);
                Canvas.SetTop(label, y - 8);
                TimingChartCanvas.Children.Add(label);
            }

            int xLines = 8;
            for (int i = 0; i <= xLines; i++)
            {
                double x = l + i * (r - l) / xLines;
                TimingChartCanvas.Children.Add(new Line { X1 = x, Y1 = t, X2 = x, Y2 = b, Stroke = BrushFromArgb(32, 148, 163, 184), StrokeThickness = 1 });
            }
        }

        void DrawCleanBand(double l, double r, double t, double b, double low, double high, double cleanLow, double cleanHigh)
        {
            double y1 = Y(cleanLow);
            double y2 = Y(cleanHigh);
            if (y2 > y1) (y1, y2) = (y2, y1);
            var band = new Rectangle
            {
                Width = r - l,
                Height = Math.Max(2, y2 - y1),
                Fill = BrushFromArgb(30, 56, 217, 150)
            };
            Canvas.SetLeft(band, l);
            Canvas.SetTop(band, y1);
            TimingChartCanvas.Children.Add(band);
        }
    }

    private void AddLegend(double left, double top)
    {
        AddLegendItem(left, top, DirectionBrush("A", false), "Step left");
        AddLegendItem(left + 100, top, DirectionBrush("D", false), "Step right");
        AddLegendItem(left + 210, top, DirectionBrush("A", true), "Spray/burst hold");
    }

    private void AddLegendItem(double x, double y, Brush brush, string text)
    {
        var dot = new Ellipse { Width = 9, Height = 9, Fill = brush, Stroke = Brushes.White, StrokeThickness = 0.6 };
        Canvas.SetLeft(dot, x);
        Canvas.SetTop(dot, y + 3);
        TimingChartCanvas.Children.Add(dot);
        var label = new TextBlock { Text = text, Foreground = BrushFromArgb(210, 226, 232, 255), FontSize = 11 };
        Canvas.SetLeft(label, x + 14);
        Canvas.SetTop(label, y);
        TimingChartCanvas.Children.Add(label);
    }

    private void DrawSelectedMouseTrace()
    {
        if (SelectedMouseCanvas == null) return;
        SelectedMouseCanvas.Children.Clear();
        double width = SelectedMouseCanvas.ActualWidth;
        double height = SelectedMouseCanvas.ActualHeight;
        if (width < 40 || height < 40) return;

        DrawMouseAxes(width, height);
        var a = _selectedAttempt;
        if (a == null || a.MouseTrace.Count < 2)
        {
            var label = new TextBlock
            {
                Text = "No selected trace yet.",
                Foreground = BrushFromArgb(200, 168, 176, 200),
                FontWeight = FontWeights.SemiBold
            };
            Canvas.SetLeft(label, 18);
            Canvas.SetTop(label, height / 2 - 8);
            SelectedMouseCanvas.Children.Add(label);
            return;
        }

        var points = new List<Point> { new(0, 0) };
        points.AddRange(a.MouseTrace.Select(p => new Point(p.XDegrees, p.YDegrees)));
        double maxAbsX = Math.Max(0.03, points.Max(p => Math.Abs(p.X)));
        double maxAbsY = Math.Max(0.03, points.Max(p => Math.Abs(p.Y)));
        double scale = Math.Min((width - 28) / (maxAbsX * 2), (height - 28) / (maxAbsY * 2));
        double cx = width / 2;
        double cy = height / 2;

        var line = new Polyline
        {
            Stroke = BrushFromRgb(0, 224, 255),
            StrokeThickness = 3.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        foreach (var p in points)
        {
            line.Points.Add(new Point(cx + p.X * scale, cy + p.Y * scale));
        }
        SelectedMouseCanvas.Children.Add(line);

        var end = points[^1];
        var dot = new Ellipse { Width = 11, Height = 11, Fill = Brushes.White, Stroke = BrushFromRgb(0, 224, 255), StrokeThickness = 2 };
        Canvas.SetLeft(dot, cx + end.X * scale - 5.5);
        Canvas.SetTop(dot, cy + end.Y * scale - 5.5);
        SelectedMouseCanvas.Children.Add(dot);

        var metrics = new TextBlock
        {
            Text = $"{a.MouseActionLabel} · shot {a.ShotSpeedLabel} · {a.FirstBulletLabel} · path {a.PathLengthDegrees:0.00} deg · eff {a.PathEfficiency:0.00}",
            Foreground = BrushFromArgb(230, 226, 232, 255),
            FontSize = 11
        };
        Canvas.SetLeft(metrics, 12);
        Canvas.SetTop(metrics, 10);
        SelectedMouseCanvas.Children.Add(metrics);
    }

    private void DrawMouseAxes(double width, double height)
    {
        double cx = width / 2;
        double cy = height / 2;
        var axis = BrushFromArgb(80, 168, 176, 200);
        SelectedMouseCanvas.Children.Add(new Line { X1 = 0, Y1 = cy, X2 = width, Y2 = cy, Stroke = axis, StrokeThickness = 1 });
        SelectedMouseCanvas.Children.Add(new Line { X1 = cx, Y1 = 0, X2 = cx, Y2 = height, Stroke = axis, StrokeThickness = 1 });
        var origin = new Ellipse { Width = 6, Height = 6, Fill = BrushFromRgb(0, 224, 255) };
        Canvas.SetLeft(origin, cx - 3);
        Canvas.SetTop(origin, cy - 3);
        SelectedMouseCanvas.Children.Add(origin);
    }


    private void DrawPeekConclusions()
    {
        if (!_uiReady || PeekConclusionTimingCanvas == null) return;
        var attempts = _analyzer.Attempts.Where(a => a.HasClick && a.CounterDelayMs.HasValue).OrderBy(a => a.Index).ToList();
        if (attempts.Count == 0)
        {
            PeekConclusionSummaryText.Text = "No click-confirmed peek reps yet. Start a continuous session with F9 or arm one rep with F8.";
            PeekConclusionAdviceText.Text = "Do several step-left and step-right reps before reading conclusions.";
            DrawEmptyConclusion(PeekConclusionTimingCanvas, "No timing data yet");
            DrawEmptyConclusion(PeekConclusionShotCanvas, "No shot pattern data yet");
            DrawEmptyConclusion(PeekConclusionMouseCanvas, "No crosshair data yet");
            return;
        }

        var left = attempts.Where(a => a.StepKey == "A").ToList();
        var right = attempts.Where(a => a.StepKey == "D").ToList();
        double leftAvg = left.Select(a => a.CounterDelayMs!.Value).DefaultIfEmpty(0).Average();
        double rightAvg = right.Select(a => a.CounterDelayMs!.Value).DefaultIfEmpty(0).Average();
        double leftClean = left.Count == 0 ? 0 : left.Count(a => a.TimingClass == PeekTimingClass.Clean) * 100.0 / left.Count;
        double rightClean = right.Count == 0 ? 0 : right.Count(a => a.TimingClass == PeekTimingClass.Clean) * 100.0 / right.Count;
        double avgShot = attempts.Where(a => a.FirstClickAfterCounterMs.HasValue).Select(a => a.FirstClickAfterCounterMs!.Value).DefaultIfEmpty(0).Average();
        double oneTap = attempts.Count(a => a.IsOneTap) * 100.0 / attempts.Count;

        PeekConclusionSummaryText.Text = $"{attempts.Count} click-confirmed peeks · left clean {leftClean:0}% · right clean {rightClean:0}% · average shot speed {avgShot:0} ms · one-tap rate {oneTap:0}%.";
        PeekConclusionAdviceText.Text = BuildPeekConclusionAdvice(left, right, attempts);

        DrawDirectionTimingConclusion(leftAvg, rightAvg, left.Count, right.Count);
        DrawShotPatternConclusion(attempts);
        DrawMouseActionConclusion(attempts);
    }

    private string BuildPeekConclusionAdvice(IReadOnlyList<PeekAttempt> left, IReadOnlyList<PeekAttempt> right, IReadOnlyList<PeekAttempt> all)
    {
        if (all.Count < 4) return "Record more reps. Aim for at least 10 left and 10 right attempts before trusting direction conclusions.";

        string directionAdvice;
        double LeftCleanRate(IReadOnlyList<PeekAttempt> attempts) => attempts.Count == 0 ? 100 : attempts.Count(a => a.TimingClass == PeekTimingClass.Clean) * 100.0 / attempts.Count;
        double leftClean = LeftCleanRate(left);
        double rightClean = LeftCleanRate(right);
        if (left.Count == 0 || right.Count == 0)
        {
            directionAdvice = "You need both step-left and step-right reps. Practice the missing side so the comparison is meaningful.";
        }
        else if (Math.Abs(leftClean - rightClean) >= 18)
        {
            directionAdvice = leftClean < rightClean
                ? "Step-left peeks are weaker. Focus on releasing A and pressing D at nearly the same moment."
                : "Step-right peeks are weaker. Focus on releasing D and pressing A at nearly the same moment.";
        }
        else
        {
            directionAdvice = "Left/right timing is fairly balanced. Work on consistency and shot discipline rather than only one side.";
        }

        int overlap = all.Count(a => a.TimingClass == PeekTimingClass.Overlap);
        int late = all.Count(a => a.TimingClass == PeekTimingClass.LateGap);
        string timingAdvice = overlap > late
            ? "The main timing problem is overlap: the counter key arrives while the step-out key is still held. Release the step key sooner or increase rapid-trigger separation."
            : late > overlap
                ? "The main timing problem is late/gap: the step key is released and the counter key arrives too late. Press the counter key sooner or hold the step key slightly longer."
                : "Overlap and late mistakes are similar in count; keep the transition centered around zero.";

        int sprays = all.Count(a => a.IsSpray);
        int oneTaps = all.Count(a => a.IsOneTap);
        string shootingAdvice = sprays > oneTaps
            ? "Many reps become sprays. If the drill target is one-tap discipline, reset after the first click and avoid holding M1."
            : "Shot pattern is mostly controlled. Use shot speed to decide whether you are waiting too long after the stop.";

        int messy = all.Count(a => a.MouseAction == PeekMouseAction.Messy || a.MouseAction == PeekMouseAction.Overflick);
        string mouseAdvice = messy > all.Count * 0.35
            ? "Crosshair paths often show overflick/messy corrections. Try matching the bot speed longer, then make one smaller correction before clicking."
            : "Crosshair paths are mostly efficient. Keep using the trace view to inspect outliers.";

        return directionAdvice + "\n\n" + timingAdvice + "\n\n" + shootingAdvice + "\n\n" + mouseAdvice;
    }

    private void DrawDirectionTimingConclusion(double leftAvg, double rightAvg, int leftCount, int rightCount)
    {
        var canvas = PeekConclusionTimingCanvas;
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w < 80 || h < 70) return;
        DrawConclusionAxes(canvas, w, h, "ms from step release to counter press");
        double maxAbs = Math.Max(60, Math.Max(Math.Abs(leftAvg), Math.Abs(rightAvg)) + 20);
        DrawCenteredBar(canvas, "Step left", leftAvg, leftCount, 72, w, h, maxAbs, DirectionBrush("A", false));
        DrawCenteredBar(canvas, "Step right", rightAvg, rightCount, 176, w, h, maxAbs, DirectionBrush("D", false));
    }

    private void DrawShotPatternConclusion(IReadOnlyList<PeekAttempt> attempts)
    {
        var groups = new[]
        {
            (Label: "1 tap", Count: attempts.Count(a => a.IsOneTap)),
            (Label: "2-3 taps", Count: attempts.Count(a => !a.IsSpray && a.ClickTimesMs.Count >= 2 && a.ClickTimesMs.Count <= 3)),
            (Label: "burst", Count: attempts.Count(a => !a.IsSpray && a.ClickTimesMs.Count >= 4)),
            (Label: "spray", Count: attempts.Count(a => a.IsSpray))
        };
        DrawCategoryBars(PeekConclusionShotCanvas, groups, "attempts");
    }

    private void DrawMouseActionConclusion(IReadOnlyList<PeekAttempt> attempts)
    {
        var groups = new[]
        {
            (Label: "single line", Count: attempts.Count(a => a.MouseAction == PeekMouseAction.SingleLine)),
            (Label: "micro", Count: attempts.Count(a => a.MouseAction == PeekMouseAction.MicroAdjust)),
            (Label: "overflick", Count: attempts.Count(a => a.MouseAction == PeekMouseAction.Overflick)),
            (Label: "messy", Count: attempts.Count(a => a.MouseAction == PeekMouseAction.Messy)),
            (Label: "minimal", Count: attempts.Count(a => a.MouseAction == PeekMouseAction.Minimal || a.MouseAction == PeekMouseAction.Unknown))
        };
        DrawCategoryBars(PeekConclusionMouseCanvas, groups, "attempts");
    }

    private void DrawEmptyConclusion(Canvas canvas, string text)
    {
        canvas.Children.Clear();
        double w = Math.Max(220, canvas.ActualWidth);
        double h = Math.Max(120, canvas.ActualHeight);
        var label = new TextBlock { Text = text, Foreground = BrushFromArgb(210, 168, 176, 200), FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(label, w / 2 - 80);
        Canvas.SetTop(label, h / 2 - 10);
        canvas.Children.Add(label);
    }

    private void DrawConclusionAxes(Canvas canvas, double w, double h, string label)
    {
        var grid = BrushFromArgb(55, 148, 163, 184);
        double mid = h / 2;
        canvas.Children.Add(new Line { X1 = 18, Y1 = mid, X2 = w - 18, Y2 = mid, Stroke = BrushFromArgb(180, 226, 232, 255), StrokeThickness = 1.3 });
        canvas.Children.Add(new Line { X1 = w / 2, Y1 = 34, X2 = w / 2, Y2 = h - 28, Stroke = grid, StrokeThickness = 1 });
        var t = new TextBlock { Text = label, Foreground = BrushFromArgb(210, 168, 176, 200), FontSize = 11 };
        Canvas.SetLeft(t, 18);
        Canvas.SetTop(t, 8);
        canvas.Children.Add(t);
    }

    private void DrawCenteredBar(Canvas canvas, string label, double value, int count, double y, double w, double h, double maxAbs, Brush brush)
    {
        double midX = w / 2;
        double barMax = Math.Max(40, w / 2 - 110);
        double len = Math.Abs(value) / maxAbs * barMax;
        double x = value >= 0 ? midX : midX - len;
        var rect = new Rectangle { Width = Math.Max(2, len), Height = 28, Fill = brush, RadiusX = 5, RadiusY = 5 };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
        var left = new TextBlock { Text = label, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12 };
        Canvas.SetLeft(left, 22);
        Canvas.SetTop(left, y + 5);
        canvas.Children.Add(left);
        var val = new TextBlock { Text = $"{value:+0.0;-0.0;0.0} ms · n={count}", Foreground = BrushFromArgb(230, 226, 232, 255), FontSize = 12 };
        Canvas.SetLeft(val, value >= 0 ? x + len + 8 : Math.Max(120, x - 95));
        Canvas.SetTop(val, y + 5);
        canvas.Children.Add(val);
    }

    private void DrawCategoryBars(Canvas canvas, IEnumerable<(string Label, int Count)> groups, string unit)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        double h = canvas.ActualHeight;
        if (w < 80 || h < 70) return;
        var rows = groups.ToList();
        int max = Math.Max(1, rows.Max(r => r.Count));
        double top = 38;
        double rowH = Math.Max(24, (h - 58) / Math.Max(1, rows.Count));
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            double y = top + i * rowH;
            double x0 = 95;
            double bw = (w - 130) * row.Count / max;
            var label = new TextBlock { Text = row.Label, Foreground = BrushFromArgb(230, 226, 232, 255), FontSize = 12 };
            Canvas.SetLeft(label, 16);
            Canvas.SetTop(label, y + 5);
            canvas.Children.Add(label);
            var rect = new Rectangle { Width = Math.Max(2, bw), Height = Math.Min(24, rowH - 6), Fill = BrushFromRgb(124, 92, 255), RadiusX = 5, RadiusY = 5 };
            Canvas.SetLeft(rect, x0);
            Canvas.SetTop(rect, y + 2);
            canvas.Children.Add(rect);
            var count = new TextBlock { Text = $"{row.Count} {unit}", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold };
            Canvas.SetLeft(count, x0 + Math.Max(4, bw) + 8);
            Canvas.SetTop(count, y + 5);
            canvas.Children.Add(count);
        }
    }

    private static double ParseBox(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
    }

    private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static SolidColorBrush BrushFromArgb(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    private static Brush DirectionBrush(string stepKey, bool isSpray)
    {
        if (isSpray) return AppBrush("SprayBrush", BrushFromRgb(255, 206, 69));
        return stepKey == "A" ? AppBrush("StepLeftBrush", BrushFromRgb(0, 224, 255)) : AppBrush("StepRightBrush", BrushFromRgb(124, 92, 255));
    }

    private static Brush TimingClassBrush(PeekTimingClass timing) => timing switch
    {
        PeekTimingClass.Clean => AppBrush("GoodBrush", BrushFromRgb(56, 217, 150)),
        PeekTimingClass.Overlap => AppBrush("WarnBrush", BrushFromRgb(255, 206, 69)),
        PeekTimingClass.LateGap => AppBrush("BadBrush", BrushFromRgb(255, 92, 122)),
        _ => BrushFromRgb(168, 176, 200)
    };

    private static Brush AppBrush(string key, Brush fallback)
    {
        return Application.Current?.Resources[key] as Brush ?? fallback;
    }
}

public sealed class PeekAnalyzer
{
    private readonly GameCalibration _calibration = new();
    private PeekAttempt? _current;
    private int _index;
    private bool _aDown;
    private bool _dDown;
    private bool _m1Down;
    private bool _waitingForNeutralAfterReset;
    private double? _m1DownAtMs;

    public ObservableCollection<PeekAttempt> Attempts { get; } = new();
    public event Action<PeekAttempt>? AttemptChanged;
    public double CleanMaxMs { get; set; } = 45;
    public double OverlapToleranceMs { get; set; } = 8;
    public double SprayHoldMs { get; set; } = 180;
    public bool ManualSingleAttemptMode { get; set; } = true;
    public double MaxMouseTraceMs { get; set; } = 900;
    public int MaxMouseTracePoints { get; set; } = 180;
    public double ResetMouseAfterClickMs { get; set; } = 250;
    public bool IsArmed { get; private set; }

    public int RemoveAttempts(IEnumerable<PeekAttempt> attempts)
    {
        int removed = 0;
        foreach (var attempt in attempts.Distinct().ToList())
        {
            if (!Attempts.Contains(attempt)) continue;
            Attempts.Remove(attempt);
            if (ReferenceEquals(_current, attempt)) _current = null;
            removed++;
        }
        return removed;
    }

    public void Reset()
    {
        Attempts.Clear();
        _current = null;
        _index = 0;
        _aDown = false;
        _dDown = false;
        _m1Down = false;
        _waitingForNeutralAfterReset = false;
        _m1DownAtMs = null;
        IsArmed = false;
    }

    public void ArmSingleAttempt()
    {
        if (_current != null)
        {
            if (_current.HasClick) FinalizeCurrent(_current.LastInputTimeMs);
            else _current = null;
        }

        _m1Down = false;
        _waitingForNeutralAfterReset = false;
        _m1DownAtMs = null;
        IsArmed = true;
    }

    public void Add(InputEventRecord e)
    {
        if (e.Kind is InputKind.KeyDown or InputKind.KeyUp)
        {
            HandleKey(e);
        }
        else if (e.Kind == InputKind.MouseDown && e.Code == "M1")
        {
            HandleMouseDown(e);
        }
        else if (e.Kind == InputKind.MouseUp && e.Code == "M1")
        {
            HandleMouseUp(e);
        }
        else if (e.Kind == InputKind.MouseMove)
        {
            HandleMouseMove(e);
        }
    }

    public void FlushCurrentAttempt()
    {
        if (_current != null && _current.HasClick)
        {
            FinalizeCurrent(_current.LastInputTimeMs);
        }
        else
        {
            _current = null;
        }
    }

    private void HandleKey(InputEventRecord e)
    {
        if (e.Code != "A" && e.Code != "D") return;

        bool isDown = e.Kind == InputKind.KeyDown;
        bool wasDown = e.Code == "A" ? _aDown : _dDown;

        if (isDown && wasDown) return;

        // A reset movement after the shot ends the current rep. In continuous mode it must not
        // immediately become the start of the next rep, otherwise the walk back to the plate is
        // misread as a peek. Wait until A and D are both released, then the next key-down starts
        // the next peek.
        if (isDown && _current != null && _current.HasClick && _current.HasCounter && e.SessionTimeMs - _current.FirstClickTimeMs >= 25)
        {
            _current.ResetMovementKey = e.Code;
            _current.ResetMovementTimeMs = e.SessionTimeMs;
            _current.LastInputTimeMs = e.SessionTimeMs;
            Evaluate(_current);
            FinalizeCurrent(e.SessionTimeMs);

            if (e.Code == "A") _aDown = true; else _dDown = true;
            if (ManualSingleAttemptMode) return;

            _waitingForNeutralAfterReset = true;
            return;
        }

        if (isDown)
        {
            if (e.Code == "A") _aDown = true; else _dDown = true;

            if (_waitingForNeutralAfterReset)
            {
                return;
            }

            if (_current == null)
            {
                if (ManualSingleAttemptMode && !IsArmed) return;
                StartAttempt(e.Code, e.SessionTimeMs);
                IsArmed = false;
            }
            else if (!_current.HasCounter && e.Code != _current.StepKey)
            {
                _current.CounterKey = e.Code;
                _current.CounterDownTimeMs = e.SessionTimeMs;
                _current.LastInputTimeMs = e.SessionTimeMs;
                Evaluate(_current);
                AttemptChanged?.Invoke(_current);
            }
        }
        else
        {
            if (e.Code == "A") _aDown = false; else _dDown = false;

            if (!_aDown && !_dDown)
            {
                _waitingForNeutralAfterReset = false;
            }

            if (_current != null && e.Code == _current.StepKey && !_current.StepReleaseTimeMs.HasValue)
            {
                _current.StepReleaseTimeMs = e.SessionTimeMs;
                _current.LastInputTimeMs = e.SessionTimeMs;
                Evaluate(_current);
                AttemptChanged?.Invoke(_current);
            }
        }
    }

    private void StartAttempt(string stepKey, double t)
    {
        IsArmed = false;
        _current = new PeekAttempt
        {
            Index = ++_index,
            StepKey = stepKey,
            CounterKey = stepKey == "A" ? "D" : "A",
            StepDownTimeMs = t,
            LastInputTimeMs = t
        };
    }

    private void HandleMouseDown(InputEventRecord e)
    {
        if (_current == null || !_current.HasCounter) return;

        _m1Down = true;
        _m1DownAtMs = e.SessionTimeMs;
        _current.ClickTimesMs.Add(e.SessionTimeMs);
        _current.LastInputTimeMs = e.SessionTimeMs;
        EnsureVisible(_current);
        Evaluate(_current);
        AttemptChanged?.Invoke(_current);
    }

    private void HandleMouseUp(InputEventRecord e)
    {
        if (!_m1Down || _current == null) return;
        _m1Down = false;
        if (_m1DownAtMs.HasValue)
        {
            _current.ClickHoldDurationsMs.Add(Math.Max(0, e.SessionTimeMs - _m1DownAtMs.Value));
        }
        _m1DownAtMs = null;
        _current.LastInputTimeMs = e.SessionTimeMs;
        Evaluate(_current);
        AttemptChanged?.Invoke(_current);
    }

    private void HandleMouseMove(InputEventRecord e)
    {
        if (_current == null) return;

        // In Peek mode the important aim movement often happens while the player is still
        // stepping out and waiting for the crosshair to catch the bot. Track from the
        // initial step-out key, not only after the counter key.
        if (e.SessionTimeMs < _current.StepDownTimeMs) return;
        if (e.SessionTimeMs - _current.StepDownTimeMs > Math.Max(60, MaxMouseTraceMs)) return;
        if (_current.HasClick && e.SessionTimeMs - _current.FirstClickTimeMs > Math.Max(20, ResetMouseAfterClickMs)) return;
        if (_current.MouseTrace.Count >= Math.Max(8, MaxMouseTracePoints)) return;

        int x = e.DeltaX;
        int y = e.DeltaY;
        if (_current.MouseTrace.Count > 0)
        {
            x += _current.MouseTrace[^1].XCounts;
            y += _current.MouseTrace[^1].YCounts;
        }

        double referenceTime = _current.CounterDownTimeMs ?? _current.StepDownTimeMs;
        _current.MouseTrace.Add(new MouseTracePoint
        {
            SessionTimeMs = e.SessionTimeMs,
            TimeFromCounterMs = e.SessionTimeMs - referenceTime,
            DeltaX = e.DeltaX,
            DeltaY = e.DeltaY,
            XCounts = x,
            YCounts = y,
            XDegrees = x * _calibration.HorizontalDegreesPerCount,
            YDegrees = y * _calibration.VerticalDegreesPerCount
        });

        _current.LastInputTimeMs = e.SessionTimeMs;
        if (_current.HasClick || _current.HasCounter)
        {
            Evaluate(_current);
            AttemptChanged?.Invoke(_current);
        }
    }

    private void EnsureVisible(PeekAttempt attempt)
    {
        if (!Attempts.Contains(attempt)) Attempts.Insert(0, attempt);
    }

    private void FinalizeCurrent(double endTimeMs)
    {
        if (_current == null) return;
        if (_m1Down && _m1DownAtMs.HasValue)
        {
            _current.ClickHoldDurationsMs.Add(Math.Max(0, endTimeMs - _m1DownAtMs.Value));
        }

        if (_current.HasClick)
        {
            _current.EndTimeMs = endTimeMs;
            _current.IsActive = false;
            EnsureVisible(_current);
            Evaluate(_current);
            AttemptChanged?.Invoke(_current);
        }
        _current = null;
        _m1Down = false;
        _m1DownAtMs = null;
    }

    private void Evaluate(PeekAttempt attempt)
    {
        attempt.CleanMaxMs = CleanMaxMs;
        attempt.OverlapToleranceMs = OverlapToleranceMs;
        attempt.SprayHoldMs = SprayHoldMs;
        attempt.Recalculate();
    }
}

public enum PeekTimingClass
{
    Pending,
    Clean,
    Overlap,
    LateGap
}

public enum PeekMouseAction
{
    Unknown,
    Minimal,
    SingleLine,
    MicroAdjust,
    Overflick,
    Messy
}

public sealed class PeekAttempt
{
    public int Index { get; set; }
    public string StepKey { get; set; } = "";
    public string CounterKey { get; set; } = "";
    public double StepDownTimeMs { get; set; }
    public double? StepReleaseTimeMs { get; set; }
    public double? CounterDownTimeMs { get; set; }
    public double? EndTimeMs { get; set; }
    public double? ResetMovementTimeMs { get; set; }
    public string? ResetMovementKey { get; set; }
    public double LastInputTimeMs { get; set; }
    public bool IsActive { get; set; } = true;
    public List<double> ClickTimesMs { get; } = new();
    public List<double> ClickHoldDurationsMs { get; } = new();
    public List<MouseTracePoint> MouseTrace { get; } = new();
    public double CleanMaxMs { get; set; } = 45;
    public double OverlapToleranceMs { get; set; } = 8;
    public double SprayHoldMs { get; set; } = 180;
    public PeekTimingClass TimingClass { get; private set; } = PeekTimingClass.Pending;
    public PeekMouseAction MouseAction { get; private set; } = PeekMouseAction.Unknown;
    public string Advice { get; private set; } = "Waiting for click-confirmed peek data.";

    public bool HasCounter => CounterDownTimeMs.HasValue;
    public bool HasClick => ClickTimesMs.Count > 0;
    public double FirstClickTimeMs => ClickTimesMs.Count == 0 ? 0 : ClickTimesMs[0];
    public double? FirstClickAfterCounterMs => HasCounter && HasClick ? FirstClickTimeMs - CounterDownTimeMs!.Value : null;
    public double? CounterDelayMs => StepReleaseTimeMs.HasValue && CounterDownTimeMs.HasValue ? CounterDownTimeMs.Value - StepReleaseTimeMs.Value : null;
    public bool IsSpray => ClickHoldDurationsMs.Any(d => d >= SprayHoldMs) || ClickTimesMs.Count >= 5;
    public double LongestHoldMs => ClickHoldDurationsMs.DefaultIfEmpty(0).Max();
    public bool IsOneTap => HasClick && ClickTimesMs.Count == 1 && !IsSpray;
    public double? ResetAfterFirstShotMs => HasClick && ResetMovementTimeMs.HasValue ? ResetMovementTimeMs.Value - FirstClickTimeMs : null;

    public string StepLabel => StepKey == "A" ? "Step left" : "Step right";
    public string CounterLabel => HasCounter ? $"{StepKey}->{CounterKey}" : "waiting";
    public string CounterDelayLabel => CounterDelayMs.HasValue ? $"{CounterDelayMs.Value:+0.0;-0.0;0.0} ms" : "pending";
    public string ShotPatternLabel
    {
        get
        {
            if (!HasClick) return "no shot";
            if (IsSpray) return ClickTimesMs.Count >= 5 ? $"spray ({ClickTimesMs.Count})" : $"spray hold {LongestHoldMs:0} ms";
            if (ClickTimesMs.Count == 1) return "1 tap";
            if (ClickTimesMs.Count <= 3) return $"{ClickTimesMs.Count} taps";
            return $"burst {ClickTimesMs.Count}";
        }
    }
    public string ShotSpeedLabel => FirstClickAfterCounterMs.HasValue ? $"{FirstClickAfterCounterMs.Value:0.0} ms" : "pending";
    public string FirstBulletLabel => IsOneTap ? "one tap assumed" : IsSpray ? "spray" : HasClick ? "follow-up shots" : "no shot";
    public string ResetLabel => ResetAfterFirstShotMs.HasValue ? $"reset {ResetAfterFirstShotMs.Value:0.0} ms" : "not reset yet";

    public string MouseActionLabel => MouseAction switch
    {
        PeekMouseAction.Minimal => "minimal",
        PeekMouseAction.SingleLine => "single line",
        PeekMouseAction.MicroAdjust => "micro-adjust",
        PeekMouseAction.Overflick => "overflick",
        PeekMouseAction.Messy => "messy",
        _ => "unknown"
    };

    public double RawEndX => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].XCounts;
    public double RawEndY => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].YCounts;
    public double HorizontalDegrees => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].XDegrees;
    public double VerticalDegrees => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].YDegrees;
    public double DisplacementDegrees => Math.Sqrt(HorizontalDegrees * HorizontalDegrees + VerticalDegrees * VerticalDegrees);
    public double PathLengthDegrees
    {
        get
        {
            if (MouseTrace.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < MouseTrace.Count; i++)
            {
                double dx = MouseTrace[i].XDegrees - MouseTrace[i - 1].XDegrees;
                double dy = MouseTrace[i].YDegrees - MouseTrace[i - 1].YDegrees;
                total += Math.Sqrt(dx * dx + dy * dy);
            }
            return total;
        }
    }
    public double PathEfficiency => PathLengthDegrees <= 0 ? 0 : Math.Min(1, DisplacementDegrees / PathLengthDegrees);

    public void Recalculate()
    {
        TimingClass = ClassifyTiming();
        MouseAction = ClassifyMouse();
        Advice = BuildAdvice();
    }

    private PeekTimingClass ClassifyTiming()
    {
        if (!CounterDelayMs.HasValue) return PeekTimingClass.Pending;
        double d = CounterDelayMs.Value;
        if (d < -Math.Max(0, OverlapToleranceMs)) return PeekTimingClass.Overlap;
        if (d > Math.Max(1, CleanMaxMs)) return PeekTimingClass.LateGap;
        return PeekTimingClass.Clean;
    }

    private PeekMouseAction ClassifyMouse()
    {
        if (MouseTrace.Count < 2) return PeekMouseAction.Unknown;
        if (PathLengthDegrees < 0.06) return PeekMouseAction.Minimal;
        if (PathEfficiency >= 0.82) return PeekMouseAction.SingleLine;
        if (PathEfficiency >= 0.55) return PeekMouseAction.MicroAdjust;
        if (DisplacementDegrees >= 0.35 && PathLengthDegrees > DisplacementDegrees * 2.2) return PeekMouseAction.Overflick;
        return PeekMouseAction.Messy;
    }

    private string BuildAdvice()
    {
        string direction = StepKey == "A" ? "while stepping left" : "while stepping right";
        string timing = TimingClass switch
        {
            PeekTimingClass.Overlap => $"{direction}: {CounterKey} was pressed before {StepKey} was released. Release {StepKey} sooner, or reduce rapid-trigger overlap if it is caused by hall-effect settings.",
            PeekTimingClass.LateGap => $"{direction}: there was a gap before {CounterKey} was pressed. Press the counter key sooner; if needed, hold {StepKey} slightly longer so release and counter happen together.",
            PeekTimingClass.Clean => $"{direction}: key timing was clean.",
            _ => $"{direction}: timing is still pending until both release and counter are observed."
        };

        string shooting = IsSpray
            ? " Shot pattern became a spray between the stop and reset movement; use this only if the drill goal is spray control."
            : ClickTimesMs.Count > 1
                ? $" Follow-up was {ClickTimesMs.Count} taps between stopping and resetting the plate."
                : " One click was fired before reset, so this is counted as a one-tap attempt.";

        if (FirstClickAfterCounterMs.HasValue)
        {
            shooting += FirstClickAfterCounterMs.Value < 70
                ? " Shot speed after counter was very fast; verify you were fully stopped before clicking."
                : FirstClickAfterCounterMs.Value > 220
                    ? " Shot speed after counter was slow; practice matching speed earlier so the crosshair is ready when the stop happens."
                    : " Shot speed after counter is in a reasonable training window.";
        }

        string mouse = MouseAction switch
        {
            PeekMouseAction.SingleLine => " Mouse path is mostly one line; focus on timing more than aim cleanup.",
            PeekMouseAction.MicroAdjust => " Mouse path includes a correction; this can be good if it happens before the click, not during the click.",
            PeekMouseAction.Overflick => " Mouse path suggests an overflick/re-correction. Try matching bot speed longer before clicking.",
            PeekMouseAction.Messy => " Mouse path is inefficient. Slow the first flick and make one deliberate correction.",
            PeekMouseAction.Minimal => " Very little mouse movement was recorded; this may mean the crosshair was already placed or the trace was too short.",
            _ => " Mouse trace is not long enough to classify."
        };

        return timing + shooting + mouse;
    }
}
