using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StrafeLab.Models;
using StrafeLab.Services;

namespace StrafeLab;

public partial class ConclusionsView : UserControl
{
    private readonly IReadOnlyList<StrafeAttempt> _attempts;
    private readonly AnalysisSettings _settings;

    public event Action? BackRequested;

    public ConclusionsView(IReadOnlyList<StrafeAttempt> attempts, AnalysisSettings settings)
    {
        InitializeComponent();
        _attempts = attempts.ToList();
        _settings = settings;
        Loaded += (_, _) => RefreshAll();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        int total = _attempts.Count;
        int clean = _attempts.Count(a => a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick);
        int moving = _attempts.Count(a => a.IsMovingAtClick);
        double avgDelay = Avg(_attempts.Select(a => a.CounterDelayMs));
        var coaching = CoachingAnalyzer.AnalyzeSession(_attempts, _settings);

        SubtitleText.Text = $"{total} included attempts. Counter target {_settings.IdealCounterMinMs:0}-{_settings.IdealCounterMaxMs:0} ms; click target {_settings.IdealClickMinAfterCounterMs:0}-{_settings.IdealClickMaxAfterCounterMs:0} ms.";
        AttemptsText.Text = total.ToString(CultureInfo.InvariantCulture);
        CleanRateText.Text = $"{Pct(clean, total):0}%";
        MovingText.Text = $"{Pct(moving, total):0}%";
        DelayText.Text = $"{avgDelay:+0.0;-0.0;0.0} ms";
        QualityText.Text = $"{coaching.AverageQuality.Total}/100";

        TimingInsightText.Text = BuildTimingInsight();
        DirectionInsightText.Text = BuildDirectionInsight();
        AimInsightText.Text = BuildAimInsight();
        ScoreBreakdownText.Text = BuildScoreBreakdown(coaching);
        PracticeText.Text = BuildPracticePlan();

        DrawTimingChart();
        DrawDirectionChart();
        DrawResultChart();
        DrawAimChart();
    }

    private string BuildTimingInsight()
    {
        if (_attempts.Count == 0) return "Record a session first.";
        int moving = _attempts.Count(a => a.IsMovingAtClick);
        int overlap = _attempts.Count(a => a.Grade == TimingGrade.Overlap);
        int late = _attempts.Count(a => a.Grade == TimingGrade.Late);
        int early = _attempts.Count(a => a.Grade == TimingGrade.EarlyClick);
        if (moving >= overlap && moving >= late && moving > 0) return "Main issue: moving shots. You are clicking while a movement key is still held. Result is logged as Moving even when timing also shows overlap or late.";
        if (overlap > late && overlap > 0) return "Main issue: overlap. Release the original movement key before the counter key, or increase rapid-trigger release sensitivity if your keyboard is sending overlap.";
        if (late > overlap && late > 0) return "Main issue: slow counter. Press the opposite key sooner after letting go.";
        if (early > 0) return "Main issue: early shot. Delay M1 until after the stop has registered.";
        return "Timing is mostly clean. Focus on reducing spread and reviewing outliers.";
    }

    private string BuildDirectionInsight()
    {
        var ad = _attempts.Where(a => a.FromKey == "A" && a.ToKey == "D").ToList();
        var da = _attempts.Where(a => a.FromKey == "D" && a.ToKey == "A").ToList();
        if (ad.Count == 0 || da.Count == 0) return "Record both A>D and D>A attempts before comparing sides.";
        double adMoving = Pct(ad.Count(a => a.IsMovingAtClick), ad.Count);
        double daMoving = Pct(da.Count(a => a.IsMovingAtClick), da.Count);
        double adAvg = Avg(ad.Select(a => a.CounterDelayMs));
        double daAvg = Avg(da.Select(a => a.CounterDelayMs));
        string weaker = Math.Abs(adMoving - daMoving) >= 10 ? (adMoving > daMoving ? "A>D has more moving shots." : "D>A has more moving shots.") :
                        Math.Abs(adAvg - daAvg) >= 10 ? (adAvg > daAvg ? "A>D is slower on average." : "D>A is slower on average.") :
                        "No strong direction bias yet.";
        return $"A>D: {ad.Count} attempts, avg {adAvg:+0.0;-0.0;0.0} ms, moving {adMoving:0}%. D>A: {da.Count} attempts, avg {daAvg:+0.0;-0.0;0.0} ms, moving {daMoving:0}%. {weaker}";
    }

    private string BuildAimInsight()
    {
        var traced = _attempts.Where(a => a.MouseTrace.Count > 1).ToList();
        if (traced.Count == 0) return "No mouse trace data yet.";
        double eff = Avg(traced.Select(a => a.PathEfficiency));
        double path = Avg(traced.Select(a => a.PathLengthDegrees));
        return $"{traced.Count}/{_attempts.Count} attempts have traces. Avg efficiency {eff:0.00}; avg path {path:0.00} deg. Lower efficiency usually means overflicking, zig-zagging, or correcting too late.";
    }

    private string BuildPracticePlan()
    {
        if (_attempts.Count == 0) return "Record a session, then return here.";
        var coaching = CoachingAnalyzer.AnalyzeSession(_attempts, _settings);
        int moving = _attempts.Count(a => a.IsMovingAtClick);
        int overlap = _attempts.Count(a => a.Grade == TimingGrade.Overlap);
        int late = _attempts.Count(a => a.Grade == TimingGrade.Late);
        var lines = new List<string>();
        lines.Add("Priority: " + coaching.PracticePrescription);
        lines.Add("1. Use the live table to click the worst attempts, then replay them in slow motion.");
        if (moving > 0) lines.Add("2. Moving shots: practice releasing A/D before M1. In replay, M1 should light after both movement keys are up or after a brief clean counter tap.");
        if (overlap > 0) lines.Add("3. Overlap: slow the sequence down until the original key visibly turns off before the counter key turns on.");
        if (late > 0) lines.Add("4. Slow counter: make release and opposite-key press one rhythm, not two separate actions.");
        lines.Add("5. Re-run 20 reps and compare the direction chart. The goal is similar left/right averages and fewer moving-shot results.");
        return string.Join("\n\n", lines);
    }

    private string BuildScoreBreakdown(SessionCoachingInsight coaching)
    {
        var q = coaching.AverageQuality;
        return $"Overall quality: {q.Total}/{q.TotalMax}\n\n" +
               $"Timing: {q.Timing}/{q.TimingMax} - release/counter handoff and overlap control.\n" +
               $"Shot discipline: {q.Shot}/{q.ShotMax} - no moving shots and click inside the target timing window.\n" +
               $"Mouse control: {q.Mouse}/{q.MouseMax} - straight path, micro-correction quality, and avoiding messy overflicks.\n" +
               $"Consistency: {q.Consistency}/{q.ConsistencyMax} - how tightly attempts cluster instead of relying on one good rep.\n\n" +
               $"Priority: {coaching.MainIssue}\n\n" +
               $"Fix now: {coaching.PracticePrescription}";
    }

    private void DrawTimingChart()
    {
        var canvas = TimingCanvas;
        if (!Ready(canvas)) return;
        Prepare(canvas, "Counter timing by attempt");
        double w = canvas.ActualWidth, h = canvas.ActualHeight, left = 56, right = 22, top = 36, bottom = 40;
        double plotW = w - left - right, plotH = h - top - bottom;
        double minY = -80, maxY = 160;
        DrawAxis(canvas, left, top, plotW, plotH, minY, maxY);
        var attempts = _attempts.TakeLast(Math.Min(80, _attempts.Count)).ToList();
        if (attempts.Count == 0) return;
        for (int i = 0; i < attempts.Count; i++)
        {
            var a = attempts[i];
            double x = left + (attempts.Count == 1 ? plotW / 2 : i * plotW / (attempts.Count - 1));
            double y = MapY(a.CounterDelayMs, minY, maxY, top, plotH);
            var color = a.IsMovingAtClick ? Color.FromRgb(255, 92, 122) : a.Grade switch
            {
                TimingGrade.Overlap => Color.FromRgb(255, 206, 69),
                TimingGrade.Late => Color.FromRgb(120, 190, 255),
                TimingGrade.EarlyClick => Color.FromRgb(255, 150, 90),
                _ => Color.FromRgb(56, 217, 150)
            };
            DrawDot(canvas, x, y, color, a.IsMovingAtClick ? 6 : 4);
        }
    }

    private void DrawDirectionChart()
    {
        if (!Ready(DirectionCanvas)) return;
        Prepare(DirectionCanvas, "Average delay by direction");
        var rows = new[]
        {
            (Label: "A>D", Attempts: _attempts.Where(a => a.FromKey == "A" && a.ToKey == "D").ToList()),
            (Label: "D>A", Attempts: _attempts.Where(a => a.FromKey == "D" && a.ToKey == "A").ToList())
        };
        DrawBarChart(DirectionCanvas, rows.Select(r => (r.Label, Avg(r.Attempts.Select(a => a.CounterDelayMs)))).ToList(), "ms");
    }

    private void DrawResultChart()
    {
        if (!Ready(ResultCanvas)) return;
        Prepare(ResultCanvas, "Results by direction");
        var rows = new[]
        {
            ("A>D moving", _attempts.Count(a => a.FromKey == "A" && a.ToKey == "D" && a.IsMovingAtClick)),
            ("A>D clean", _attempts.Count(a => a.FromKey == "A" && a.ToKey == "D" && a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick)),
            ("D>A moving", _attempts.Count(a => a.FromKey == "D" && a.ToKey == "A" && a.IsMovingAtClick)),
            ("D>A clean", _attempts.Count(a => a.FromKey == "D" && a.ToKey == "A" && a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick))
        }.ToList();
        DrawBarChart(ResultCanvas, rows.Select(r => (r.Item1, (double)r.Item2)).ToList(), "count");
    }

    private void DrawAimChart()
    {
        if (!Ready(AimCanvas)) return;
        Prepare(AimCanvas, "Mouse path efficiency vs click delay");
        double w = AimCanvas.ActualWidth, h = AimCanvas.ActualHeight, left = 56, right = 22, top = 36, bottom = 40;
        double plotW = w - left - right, plotH = h - top - bottom;
        double minX = 0, maxX = Math.Max(220, _attempts.Where(a => a.ClickFromCounterMs.HasValue).Select(a => a.ClickFromCounterMs!.Value).DefaultIfEmpty(160).Max() + 20);
        double minY = 0, maxY = 1.2;
        DrawAxesOnly(AimCanvas, left, top, plotW, plotH);
        foreach (var a in _attempts.Where(a => a.MouseTrace.Count > 1 && a.ClickFromCounterMs.HasValue))
        {
            double x = left + (a.ClickFromCounterMs!.Value - minX) / (maxX - minX) * plotW;
            double y = top + plotH - (a.PathEfficiency - minY) / (maxY - minY) * plotH;
            DrawDot(AimCanvas, x, y, a.IsMovingAtClick ? Color.FromRgb(255, 92, 122) : Color.FromRgb(0, 224, 255), 4);
        }
        AddText(AimCanvas, left, h - 26, "X = click delay, Y = path efficiency", 12, Color.FromRgb(168,176,200));
    }

    private static void DrawBarChart(Canvas canvas, IReadOnlyList<(string Label, double Value)> rows, string unit)
    {
        double w = canvas.ActualWidth, h = canvas.ActualHeight, left = 110, top = 48, rowH = 44;
        double max = Math.Max(1, rows.Select(r => Math.Abs(r.Value)).DefaultIfEmpty(1).Max());
        for (int i = 0; i < rows.Count; i++)
        {
            double y = top + i * rowH;
            AddText(canvas, 18, y + 8, rows[i].Label, 12, Color.FromRgb(245,247,255));
            double barW = Math.Abs(rows[i].Value) / max * Math.Max(20, w - left - 60);
            var rect = new Rectangle { Width = barW, Height = 22, Fill = new SolidColorBrush(Color.FromRgb(124, 92, 255)), RadiusX = 6, RadiusY = 6 };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, y + 6);
            canvas.Children.Add(rect);
            AddText(canvas, left + barW + 8, y + 8, $"{rows[i].Value:0.0} {unit}", 12, Color.FromRgb(168,176,200));
        }
    }

    private static void Prepare(Canvas canvas, string title)
    {
        canvas.Children.Clear();
        AddText(canvas, 18, 12, title, 14, Colors.White, FontWeights.Bold);
    }

    private static bool Ready(Canvas canvas) => canvas.ActualWidth > 40 && canvas.ActualHeight > 40;
    private static double Avg(IEnumerable<double> values) { var list = values.ToList(); return list.Count == 0 ? 0 : list.Average(); }
    private static double Pct(int n, int total) => total == 0 ? 0 : n * 100.0 / total;
    private static double MapY(double value, double min, double max, double top, double height) => top + height - (value - min) / (max - min) * height;

    private static void DrawAxis(Canvas canvas, double left, double top, double width, double height, double minY, double maxY)
    {
        DrawAxesOnly(canvas, left, top, width, height);
        foreach (double v in new[] { minY, 0, maxY })
        {
            double y = MapY(v, minY, maxY, top, height);
            canvas.Children.Add(new Line { X1 = left, Y1 = y, X2 = left + width, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(38,48,74)), StrokeThickness = 1 });
            AddText(canvas, 18, y - 8, v.ToString("+0;-0;0", CultureInfo.InvariantCulture), 12, Color.FromRgb(168,176,200));
        }
    }

    private static void DrawAxesOnly(Canvas canvas, double left, double top, double width, double height)
    {
        var rect = new Rectangle { Width = width, Height = height, Stroke = new SolidColorBrush(Color.FromRgb(38,48,74)), StrokeThickness = 1 };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        canvas.Children.Add(rect);
    }

    private static void DrawDot(Canvas canvas, double x, double y, Color color, double radius)
    {
        var dot = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = new SolidColorBrush(color), Stroke = Brushes.White, StrokeThickness = 0.5 };
        Canvas.SetLeft(dot, x - radius);
        Canvas.SetTop(dot, y - radius);
        canvas.Children.Add(dot);
    }

    private static void AddText(Canvas canvas, double x, double y, string text, double size, Color color, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = new SolidColorBrush(color), FontWeight = weight ?? FontWeights.Normal };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        canvas.Children.Add(tb);
    }
}
