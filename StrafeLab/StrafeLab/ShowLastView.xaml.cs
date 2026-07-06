using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StrafeLab.Models;

namespace StrafeLab;

public partial class ShowLastView : UserControl
{
    private readonly DispatcherTimer _replayTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _replayStopwatch = new();
    private StrafeAttempt? _attempt;
    private AnalysisSettings? _settings;
    private double _replaySlowdown = 1.0;
    private double _replayWindowStartMs;
    private double _replayWindowEndMs;
    private double _manualReplayCursorMs = double.NaN;

    public event Action? BackRequested;

    public ShowLastView()
    {
        InitializeComponent();
        Foreground = (Brush)FindResource("TextBrush");
        _replayTimer.Tick += ReplayTimer_Tick;
    }

    public void UpdateAttempt(StrafeAttempt? attempt, AnalysisSettings settings)
    {
        StopReplay(resetToStart: false);
        _attempt = attempt;
        _settings = settings;
        if (attempt == null)
        {
            StatusText.Text = "Listening for the next click-confirmed counter-strafe…";
            DirectionText.Text = "Direction: -";
            CounterDelayText.Text = "Key wait: -";
            ClickDelayText.Text = "Click wait: -";
            TraceDetailText.Text = "Trace: -";
            MistakeText.Text = "Mistakes: -";
            ResultText.Text = "Result: -";
            WhatHappenedText.Text = "What happened: waiting for attempt.";
            ReplayText.Text = "The next attempt replay will appear here.";
            TraceText.Text = "Mouse path appears when the attempt has movement data.";
            SetKeyVisual(KeyABox, KeyAStateText, false, false, false);
            SetKeyVisual(KeyDBox, KeyDStateText, false, false, false);
            SetKeyVisual(KeyM1Box, KeyM1StateText, false, false, false);
            ReplayCanvas.Children.Clear();
            TraceCanvas.Children.Clear();
            return;
        }

        StatusText.Text = $"Showing latest click-confirmed attempt #{attempt.Index}. Keep playing; the next attempt will replace this view.";
        DirectionText.Text = $"Direction: {attempt.Direction}";
        CounterDelayText.Text = $"Key wait: {attempt.CounterDelayLabel}";
        ClickDelayText.Text = $"Click wait: {attempt.ClickLabel}";
        TraceDetailText.Text = $"Trace: {attempt.TraceLabel}";
        MistakeText.Text = $"Mistakes: {attempt.MistakeLabel}";
        ResultText.Text = $"Result: {attempt.ResultLabel}";
        WhatHappenedText.Text = $"What happened: {attempt.TableWhatHappened}";
        TraceText.Text = attempt.MouseTrace.Count == 0
            ? "No mouse trace was captured for this attempt. The key timing replay is still useful."
            : $"Path {attempt.PathLengthDegrees:0.00} deg · efficiency {attempt.PathEfficiency:0.00} · {attempt.AimControlLabel}";

        SetReplayWindow(attempt);
        _manualReplayCursorMs = _replayWindowStartMs;
        UpdateReplayVisual(_manualReplayCursorMs, isPlaying: false);
        DrawTrace(attempt);
    }

    private static string BuildReplayGuidanceText(StrafeAttempt attempt, AnalysisSettings settings)
    {
        var timing = $"Ideal counter: {settings.IdealCounterMinMs:0.#}-{settings.IdealCounterMaxMs:0.#} ms. Ideal click: {settings.IdealClickMinAfterCounterMs:0.#}-{settings.IdealClickMaxAfterCounterMs:0.#} ms after counter.";
        return $"{timing} Table mistake: {attempt.MistakeLabel}.";
    }

    private void ShowLastReplaySpeedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        double slowdown = 1.0;
        if (button.Tag is string tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            slowdown = Math.Max(1.0, parsed);
        }
        StartReplay(slowdown);
    }

    private void ShowLastReplayCustomSlowButton_Click(object sender, RoutedEventArgs e)
    {
        double slowdown = 5.0;
        if (double.TryParse(ShowLastReplayCustomSlowBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            slowdown = Math.Clamp(parsed, 1.0, 100.0);
        }
        ShowLastReplayCustomSlowBox.Text = slowdown.ToString("0.##", CultureInfo.InvariantCulture);
        StartReplay(slowdown);
    }

    private void ShowLastReplayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_attempt == null || _settings == null)
        {
            StopReplay(resetToStart: false);
            return;
        }

        double cursorMs = GetCurrentReplayCursorMs();
        StopReplay(resetToStart: false);
        _manualReplayCursorMs = Math.Clamp(cursorMs, _replayWindowStartMs, _replayWindowEndMs);
        UpdateReplayVisual(_manualReplayCursorMs, isPlaying: false);
    }

    private void StartReplay(double slowdown)
    {
        if (_attempt == null || _settings == null || !_attempt.HasClick)
        {
            ReplayText.Text = "Make or load a click-confirmed attempt first.";
            return;
        }

        _replaySlowdown = Math.Max(1.0, slowdown);
        SetReplayWindow(_attempt);
        _manualReplayCursorMs = double.NaN;
        _replayStopwatch.Restart();
        _replayTimer.Start();
        UpdateReplayVisual(_replayWindowStartMs, isPlaying: true);
    }

    private void StopReplay(bool resetToStart)
    {
        _replayTimer.Stop();
        _replayStopwatch.Stop();
        if (resetToStart && _attempt != null && _settings != null)
        {
            SetReplayWindow(_attempt);
            _manualReplayCursorMs = _replayWindowStartMs;
            UpdateReplayVisual(_manualReplayCursorMs, isPlaying: false);
        }
    }

    private void ReplayTimer_Tick(object? sender, EventArgs e)
    {
        if (_attempt == null || _settings == null)
        {
            StopReplay(resetToStart: false);
            return;
        }

        double cursorMs = GetCurrentReplayCursorMs();
        if (cursorMs >= _replayWindowEndMs)
        {
            cursorMs = _replayWindowEndMs;
            StopReplay(resetToStart: false);
        }

        _manualReplayCursorMs = cursorMs;
        UpdateReplayVisual(cursorMs, _replayTimer.IsEnabled);
    }

    private double GetCurrentReplayCursorMs()
    {
        if (!_replayTimer.IsEnabled)
        {
            return double.IsNaN(_manualReplayCursorMs) ? _replayWindowStartMs : _manualReplayCursorMs;
        }

        double elapsedAttemptMs = _replayStopwatch.Elapsed.TotalMilliseconds / Math.Max(1.0, _replaySlowdown);
        return _replayWindowStartMs + elapsedAttemptMs;
    }

    private void SetReplayWindow(StrafeAttempt attempt)
    {
        double clickMs = attempt.ClickTimeMs ?? Math.Max(attempt.ReleaseTimeMs, attempt.OppositeDownTimeMs) + 120;
        double first = Math.Min(attempt.ReleaseTimeMs, Math.Min(attempt.OppositeDownTimeMs, clickMs));
        double last = Math.Max(attempt.ReleaseTimeMs, Math.Max(attempt.OppositeDownTimeMs, clickMs));
        if (attempt.CounterKeyUpTimeMs.HasValue) last = Math.Max(last, attempt.CounterKeyUpTimeMs.Value);
        if (attempt.ClickUpTimeMs.HasValue) last = Math.Max(last, attempt.ClickUpTimeMs.Value);
        _replayWindowStartMs = Math.Max(0, first - 80);
        _replayWindowEndMs = Math.Max(last + 120, _replayWindowStartMs + 220);
    }

    private void UpdateReplayVisual(double cursorMs, bool isPlaying)
    {
        if (_attempt == null || _settings == null) return;
        var attempt = _attempt;
        var settings = _settings;

        double keyEnd = GetCounterKeyEndMs(attempt, _replayWindowEndMs);
        bool fromKeyDown = cursorMs < attempt.ReleaseTimeMs;
        bool toKeyDown = cursorMs >= attempt.OppositeDownTimeMs && cursorMs <= keyEnd;
        bool m1Down = attempt.ClickTimeMs.HasValue && cursorMs >= attempt.ClickTimeMs.Value && cursorMs <= GetClickEndMs(attempt, _replayWindowEndMs);
        bool aDown = (attempt.FromKey == "A" && fromKeyDown) || (attempt.ToKey == "A" && toKeyDown);
        bool dDown = (attempt.FromKey == "D" && fromKeyDown) || (attempt.ToKey == "D" && toKeyDown);
        bool overlapNow = aDown && dDown;
        bool movingShotNow = m1Down && (aDown || dDown);

        SetKeyVisual(KeyABox, KeyAStateText, aDown, overlapNow || (movingShotNow && aDown), false);
        SetKeyVisual(KeyDBox, KeyDStateText, dDown, overlapNow || (movingShotNow && dDown), false);
        SetKeyVisual(KeyM1Box, KeyM1StateText, m1Down, movingShotNow, m1Down);

        DrawReplayTimeline(attempt, settings, cursorMs);

        string running = isPlaying
            ? (_replaySlowdown <= 1.01 ? "Playing 1x" : $"Playing {_replaySlowdown:0.##}x slow")
            : "Paused";
        ReplayText.Text = $"{BuildReplayGuidanceText(attempt, settings)} {running} · {DescribeReplayPhase(attempt, cursorMs)}";
    }

    private static string DescribeReplayPhase(StrafeAttempt attempt, double cursorMs)
    {
        double clickMs = attempt.ClickTimeMs ?? double.PositiveInfinity;
        if (cursorMs < attempt.ReleaseTimeMs) return $"{attempt.FromKey} is still held.";
        if (cursorMs < attempt.OppositeDownTimeMs) return "between release and counter key.";
        if (cursorMs < clickMs) return $"counter key {attempt.ToKey} is active; wait for M1.";
        if (attempt.ClickTimeMs.HasValue && cursorMs >= attempt.ClickTimeMs.Value) return attempt.IsMovingAtClick ? "M1 during movement warning." : "M1 after stop window.";
        return "attempt timing.";
    }

    private void DrawReplayTimeline(StrafeAttempt attempt, AnalysisSettings settings, double cursorMs)
    {
        double clickMs = attempt.ClickTimeMs ?? Math.Max(attempt.ReleaseTimeMs, attempt.OppositeDownTimeMs) + 120;
        double width = Math.Max(ReplayCanvas.ActualWidth, 420);
        double height = Math.Max(ReplayCanvas.ActualHeight, 90);
        double left = 22;
        double right = Math.Max(left + 40, width - 22);
        double scale = (right - left) / Math.Max(1, _replayWindowEndMs - _replayWindowStartMs);
        double X(double ms) => left + (ms - _replayWindowStartMs) * scale;

        ReplayCanvas.Children.Clear();
        ReplayCanvas.Children.Add(new Line { X1 = left, X2 = right, Y1 = height / 2, Y2 = height / 2, Stroke = BrushFromRgb(51, 65, 85), StrokeThickness = 1 });
        AddTimeTicks(attempt, X, width, height);

        double aY = 18, dY = 38, mY = 58;
        var fromBrush = BrushFromRgb(56, 217, 150);
        var toBrush = BrushFromRgb(0, 224, 255);
        var mBrush = BrushFromRgb(226, 232, 240);
        var warn = new SolidColorBrush(Color.FromArgb(70, 255, 206, 69));
        var bad = new SolidColorBrush(Color.FromArgb(58, 255, 92, 122));

        AddBar(attempt.FromKey == "A" ? aY : dY, X(_replayWindowStartMs), X(attempt.ReleaseTimeMs), fromBrush, 8);
        double keyEnd = GetCounterKeyEndMs(attempt, _replayWindowEndMs);
        AddBar(attempt.ToKey == "A" ? aY : dY, X(attempt.OppositeDownTimeMs), X(keyEnd), toBrush, 8);
        if (attempt.HasClick) AddBar(mY, X(clickMs), X(GetClickEndMs(attempt, _replayWindowEndMs)), mBrush, 8);

        if (attempt.CounterDelayMs < settings.IdealCounterMinMs)
        {
            AddSpan(X(attempt.OppositeDownTimeMs), X(attempt.ReleaseTimeMs), 10, 54, bad);
        }
        if (attempt.ClickFromCounterMs > settings.IdealClickMaxAfterCounterMs)
        {
            AddSpan(X(attempt.OppositeDownTimeMs + settings.IdealClickMaxAfterCounterMs), X(clickMs), 10, 54, warn);
        }

        AddMarker(X(attempt.ReleaseTimeMs), $"{attempt.FromKey} up");
        AddMarker(X(attempt.OppositeDownTimeMs), $"{attempt.ToKey} down");
        if (attempt.CounterKeyUpTimeMs.HasValue) AddMarker(X(attempt.CounterKeyUpTimeMs.Value), $"{attempt.ToKey} up");
        if (attempt.HasClick) AddMarker(X(clickMs), "M1");
        if (attempt.ClickUpTimeMs.HasValue) AddMarker(X(attempt.ClickUpTimeMs.Value), "M1 up");

        double cursorX = X(Math.Clamp(cursorMs, _replayWindowStartMs, _replayWindowEndMs));
        ReplayCanvas.Children.Add(new Line { X1 = cursorX, X2 = cursorX, Y1 = 0, Y2 = height, Stroke = BrushFromRgb(255, 92, 122), StrokeThickness = 2 });
    }

    private static double GetCounterKeyEndMs(StrafeAttempt attempt, double fallbackEnd)
    {
        if (attempt.CounterKeyUpTimeMs.HasValue) return Math.Clamp(attempt.CounterKeyUpTimeMs.Value, attempt.OppositeDownTimeMs, fallbackEnd);
        if (attempt.ClickTimeMs.HasValue)
        {
            bool heldAtClick = !string.IsNullOrWhiteSpace(attempt.HeldKeysAtClick) &&
                attempt.HeldKeysAtClick.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(attempt.ToKey);
            return heldAtClick ? fallbackEnd : Math.Max(attempt.OppositeDownTimeMs, attempt.ClickTimeMs.Value - 1);
        }
        return fallbackEnd;
    }

    private static double GetClickEndMs(StrafeAttempt attempt, double fallbackEnd)
    {
        if (!attempt.ClickTimeMs.HasValue) return fallbackEnd;
        if (attempt.ClickUpTimeMs.HasValue && attempt.ClickUpTimeMs.Value >= attempt.ClickTimeMs.Value)
        {
            return Math.Clamp(attempt.ClickUpTimeMs.Value, attempt.ClickTimeMs.Value, fallbackEnd);
        }
        return Math.Min(fallbackEnd, attempt.ClickTimeMs.Value + 28);
    }

    private void AddTimeTicks(StrafeAttempt attempt, Func<double, double> x, double width, double height)
    {
        const int tickCount = 5;
        double duration = Math.Max(1, _replayWindowEndMs - attempt.ReleaseTimeMs);
        for (int i = 0; i < tickCount; i++)
        {
            double fraction = tickCount == 1 ? 0 : i / (double)(tickCount - 1);
            double relativeMs = duration * fraction;
            double ms = attempt.ReleaseTimeMs + relativeMs;
            double tx = x(ms);
            ReplayCanvas.Children.Add(new Line { X1 = tx, X2 = tx, Y1 = height - 14, Y2 = height - 3, Stroke = BrushFromRgb(148, 163, 184), StrokeThickness = 1, Opacity = 0.75 });
            var label = new TextBlock { Text = $"{relativeMs:0} ms", Foreground = BrushFromRgb(168, 176, 200), FontSize = 10 };
            Canvas.SetLeft(label, Math.Min(width - 44, Math.Max(0, tx - 16)));
            Canvas.SetTop(label, height - 30);
            ReplayCanvas.Children.Add(label);
        }
    }

    private void AddBar(double y, double x1, double x2, Brush brush, double thickness)
    {
        ReplayCanvas.Children.Add(new Line { X1 = Math.Min(x1, x2), X2 = Math.Max(x1, x2), Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
    }

    private void AddSpan(double x1, double x2, double top, double height, Brush fill)
    {
        var rect = new Rectangle { Width = Math.Max(0, Math.Abs(x2 - x1)), Height = height, Fill = fill };
        Canvas.SetLeft(rect, Math.Min(x1, x2));
        Canvas.SetTop(rect, top);
        ReplayCanvas.Children.Insert(0, rect);
    }

    private void AddMarker(double x, string label)
    {
        ReplayCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 8, Y2 = Math.Max(70, ReplayCanvas.ActualHeight - 8), Stroke = BrushFromRgb(226, 232, 240), StrokeThickness = 1 });
        var text = new TextBlock { Text = label, Foreground = BrushFromRgb(226, 232, 240), FontSize = 11 };
        Canvas.SetLeft(text, Math.Min(Math.Max(0, ReplayCanvas.ActualWidth - 64), x + 3));
        Canvas.SetTop(text, 2);
        ReplayCanvas.Children.Add(text);
    }

    private void DrawTrace(StrafeAttempt attempt)
    {
        TraceCanvas.Children.Clear();
        double width = Math.Max(TraceCanvas.ActualWidth, 360);
        double height = Math.Max(TraceCanvas.ActualHeight, 220);
        TraceCanvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = height / 2, Y2 = height / 2, Stroke = BrushFromRgb(38, 48, 74), StrokeThickness = 1 });
        TraceCanvas.Children.Add(new Line { X1 = width / 2, X2 = width / 2, Y1 = 0, Y2 = height, Stroke = BrushFromRgb(38, 48, 74), StrokeThickness = 1 });
        if (attempt.MouseTrace.Count == 0) return;
        double max = Math.Max(1, attempt.MouseTrace.Max(p => Math.Max(Math.Abs(p.XCounts), Math.Abs(p.YCounts))));
        double scale = Math.Min(width, height) * 0.40 / max;
        var points = new PointCollection(attempt.MouseTrace.Select(p => new Point(width / 2 + p.XCounts * scale, height / 2 + p.YCounts * scale)));
        TraceCanvas.Children.Add(new Polyline { Points = points, Stroke = BrushFromRgb(0, 224, 255), StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var start = points.First();
        var end = points.Last();
        AddDot(start.X, start.Y, BrushFromRgb(56, 217, 150), 7);
        AddDot(end.X, end.Y, BrushFromRgb(226, 232, 240), 7);
    }

    private void AddDot(double x, double y, Brush brush, double radius)
    {
        var ellipse = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = brush };
        Canvas.SetLeft(ellipse, x - radius);
        Canvas.SetTop(ellipse, y - radius);
        TraceCanvas.Children.Add(ellipse);
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
        if (isDown)
        {
            box.Background = isWarning ? BrushFromRgb(76, 58, 20) : BrushFromRgb(14, 75, 58);
            box.BorderBrush = isWarning ? BrushFromRgb(255, 206, 69) : BrushFromRgb(56, 217, 150);
            label.Text = "down";
            return;
        }
        box.Background = BrushFromRgb(17, 24, 39);
        box.BorderBrush = BrushFromRgb(51, 65, 85);
        label.Text = "up";
    }

    private void SolutionButton_Click(object sender, RoutedEventArgs e)
    {
        var attempt = _attempt;
        if (attempt == null)
        {
            SolutionTitle.Text = "No attempt yet";
            SolutionBody.Text = "Make one click-confirmed counter-strafe attempt first. Then this panel will explain what went wrong and what to practice.";
        }
        else
        {
            var solution = BuildSolution(attempt);
            SolutionTitle.Text = solution.Title;
            SolutionBody.Text = solution.Body;
            DrawSolutionVisual(attempt);
        }
        SolutionOverlay.Visibility = Visibility.Visible;
    }

    private static (string Title, string Body) BuildSolution(StrafeAttempt attempt)
    {
        if (attempt.IsMovingAtClick)
            return ("Moving shot", "You clicked while A or D was still held. Exercise: do 10 slow reps where M1 is only allowed after both movement keys are up. Speed up only after the table shows no moving shots.");
        if (attempt.Grade == TimingGrade.Overlap)
            return ("Key overlap", "The counter key arrived before the old key was released. Exercise: practice releasing the old key first, then tapping the counter key as one clean rhythm. Aim for a small positive counter delay.");
        if (attempt.Grade == TimingGrade.EarlyClick)
            return ("Early click", "M1 happened before the counter timing was ready. Exercise: add a tiny beat between counter key and click. Watch the replay until M1 appears after the movement warning disappears.");
        if (attempt.Grade == TimingGrade.Late && attempt.ClickFromCounterMs > 160)
            return ("Late click", "Your movement was mostly fine, but the shot came late. Exercise: after a clean counter tap, click inside the first short window instead of waiting for a full reset.");
        if (attempt.Grade == TimingGrade.Late)
            return ("Late counter", "The gap between release and counter key was too long. Exercise: make release and counter one motion. Start slowly and reduce the gap before increasing speed.");
        if (attempt.AimControlLabel is "messy" or "overflick")
            return ("Messy mouse path", "The crosshair path wandered or corrected too much. Exercise: do 10 reps where the mouse path is one short line, then compare path efficiency in the trace panel.");
        return ("Clean rep", "This attempt is usable. Keep the same release → counter → M1 rhythm for a few more reps, then check Conclusions for consistency.");
    }

    private void DrawSolutionVisual(StrafeAttempt attempt)
    {
        SolutionCanvas.Children.Clear();
        double w = Math.Max(420, SolutionCanvas.ActualWidth);
        double h = Math.Max(100, SolutionCanvas.ActualHeight);
        var oldKey = BrushFromRgb(56, 217, 150);
        var counter = BrushFromRgb(0, 224, 255);
        var click = BrushFromRgb(226, 232, 240);
        SolutionCanvas.Children.Add(new Line { X1 = 30, X2 = w - 30, Y1 = h / 2, Y2 = h / 2, Stroke = BrushFromRgb(51, 65, 85), StrokeThickness = 2 });
        AddSolutionSegment(45, 150, h / 2 - 20, oldKey, $"release {attempt.FromKey}");
        AddSolutionSegment(175, 280, h / 2, counter, $"tap {attempt.ToKey}");
        AddSolutionSegment(315, 410, h / 2 + 20, click, "M1 after stop");
    }

    private void AddSolutionSegment(double x1, double x2, double y, Brush brush, string label)
    {
        SolutionCanvas.Children.Add(new Line { X1 = x1, X2 = x2, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var text = new TextBlock { Text = label, Foreground = brush, FontSize = 12, FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(text, x1);
        Canvas.SetTop(text, y + 8);
        SolutionCanvas.Children.Add(text);
    }

    private void CloseSolutionButton_Click(object sender, RoutedEventArgs e) => SolutionOverlay.Visibility = Visibility.Collapsed;
    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void ReplayCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { if (_attempt != null && _settings != null) UpdateReplayVisual(GetCurrentReplayCursorMs(), _replayTimer.IsEnabled); }
    private void TraceCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { if (_attempt != null) DrawTrace(_attempt); }
    private static SolidColorBrush BrushFromRgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
