using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StrafeLab.Models;

namespace StrafeLab;

public partial class ConclusionsWindow : Window
{
    private readonly IReadOnlyList<StrafeAttempt> _attempts;
    private readonly AnalysisSettings _settings;

    public ConclusionsWindow(IReadOnlyList<StrafeAttempt> attempts, AnalysisSettings settings)
    {
        InitializeComponent();
        _attempts = attempts.ToList();
        _settings = settings;

        Loaded += (_, _) => RefreshAll();
        TimingCanvas.SizeChanged += (_, _) => DrawTimingChart();
        DirectionRateCanvas.SizeChanged += (_, _) => DrawDirectionCharts();
        DirectionDelayCanvas.SizeChanged += (_, _) => DrawDirectionCharts();
        DirectionAimCanvas.SizeChanged += (_, _) => DrawDirectionCharts();
        AimCanvas.SizeChanged += (_, _) => DrawAimCharts();
        AimBreakdownCanvas.SizeChanged += (_, _) => DrawAimCharts();
        ConsistencyCanvas.SizeChanged += (_, _) => DrawConsistencyCharts();
        ReactionConsistencyCanvas.SizeChanged += (_, _) => DrawConsistencyCharts();
        OutlierCanvas.SizeChanged += (_, _) => DrawConsistencyCharts();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshAll()
    {
        PopulateMetricsAndInsights();
        DrawTimingChart();
        DrawDirectionCharts();
        DrawAimCharts();
        DrawConsistencyCharts();
    }

    private void PopulateMetricsAndInsights()
    {
        int total = _attempts.Count;
        var traced = _attempts.Where(a => a.MouseTrace.Count > 1).ToList();
        var profiles = traced.Select(AnalyzeTrace).ToList();
        var aToD = DirectionStats.Build(_attempts, "A", "D");
        var dToA = DirectionStats.Build(_attempts, "D", "A");

        AttemptsMetricText.Text = total.ToString(CultureInfo.InvariantCulture);
        TraceMetricText.Text = $"{traced.Count} mouse traces";
        SubtitleText.Text = $"{total} included attempts. Counter target {_settings.IdealCounterMinMs:0}-{_settings.IdealCounterMaxMs:0} ms; click target {_settings.IdealClickMinAfterCounterMs:0}-{_settings.IdealClickMaxAfterCounterMs:0} ms after counter key.";

        var issueCounts = new Dictionary<string, int>
        {
            ["Overlap"] = _attempts.Count(a => a.Grade == TimingGrade.Overlap),
            ["Late"] = _attempts.Count(a => a.Grade == TimingGrade.Late),
            ["Moving"] = _attempts.Count(a => a.IsMovingAtClick),
            ["Early"] = _attempts.Count(a => a.Grade == TimingGrade.EarlyClick),
            ["Missed"] = _attempts.Count(a => a.Grade == TimingGrade.MissedClick),
            ["Clean"] = _attempts.Count(a => a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick)
        };
        var mainIssue = issueCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        double mainPct = total == 0 ? 0 : mainIssue.Value * 100.0 / total;
        TimingIssueMetricText.Text = mainIssue.Key == "Clean" ? "Stable" : mainIssue.Key;
        TimingIssueDetailText.Text = total == 0 ? "No timing data yet" : $"{mainIssue.Value}/{total} attempts ({mainPct:0}%). Avg delay {Average(_attempts.Select(a => a.CounterDelayMs)):0.0} ms.";

        string directionBias = BuildDirectionBias(aToD, dToA);
        var directionParts = directionBias.Split('|');
        DirectionBiasMetricText.Text = directionParts[0];
        DirectionBiasDetailText.Text = directionParts.Length > 1 ? directionParts[1] : "A>D and D>A are balanced.";

        var dominantTrace = profiles
            .Where(p => p.Kind != MousePathKind.NoTrace)
            .GroupBy(p => p.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        if (dominantTrace == null)
        {
            AimQualityMetricText.Text = "-";
            AimQualityDetailText.Text = "No mouse traces yet.";
        }
        else
        {
            double pct = dominantTrace.Count * 100.0 / Math.Max(1, profiles.Count);
            AimQualityMetricText.Text = LabelForPathKind(dominantTrace.Kind);
            AimQualityDetailText.Text = $"{dominantTrace.Count}/{profiles.Count} traces ({pct:0}%). Avg efficiency {Average(profiles.Select(p => p.Efficiency)):0.00}.";
        }

        TimingInsightText.Text = BuildTimingInsight();
        DirectionInsightText.Text = BuildDirectionInsight(aToD, dToA);
        AimInsightText.Text = BuildAimInsight(profiles);
        ConsistencyInsightText.Text = BuildConsistencyInsight(profiles);
        ConclusionsText.Text = BuildConclusions(aToD, dToA, profiles);
    }

    private string BuildTimingInsight()
    {
        if (_attempts.Count == 0) return "Record a session first.";

        int total = _attempts.Count;
        int overlap = _attempts.Count(a => a.Grade == TimingGrade.Overlap);
        int late = _attempts.Count(a => a.Grade == TimingGrade.Late);
        int perfect = _attempts.Count(a => a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick);
        int early = _attempts.Count(a => a.Grade == TimingGrade.EarlyClick);
        int missed = _attempts.Count(a => a.Grade == TimingGrade.MissedClick);
        int moving = _attempts.Count(a => a.IsMovingAtClick);
        double avgDelay = Average(_attempts.Select(a => a.CounterDelayMs));
        double spread = StandardDeviation(_attempts.Select(a => a.CounterDelayMs));

        var sb = new StringBuilder();
        sb.AppendLine("Goal: make most bars land in the shaded target window, with fewer bars on the overlap side and fewer long late tails.");
        sb.AppendLine();
        sb.AppendLine($"Clean: {Pct(perfect, total):0}%");
        sb.AppendLine($"Overlap: {Pct(overlap, total):0}%");
        sb.AppendLine($"Late: {Pct(late, total):0}%");
        sb.AppendLine($"Early click: {Pct(early, total):0}%");
        sb.AppendLine($"Moving at shot: {Pct(moving, total):0}%");
        sb.AppendLine($"Missed click: {Pct(missed, total):0}%");
        sb.AppendLine($"Average counter delay: {avgDelay:+0.0;-0.0;0.0} ms");
        sb.AppendLine($"Timing spread: {spread:0.0} ms");
        sb.AppendLine();

        if (moving > 0 && moving >= overlap && moving >= late)
        {
            sb.AppendLine("Main read: you are shooting while A/D is still held. Treat these as moving/inaccurate shots: tap and release the counter key before M1.");
        }
        else if (overlap > late && overlap > 0)
        {
            sb.AppendLine("Main read: you are pressing the opposite key before the original key is fully released. Practice release-first timing: lift the held key, then tap the counter key immediately after.");
        }
        else if (late > overlap && late > 0)
        {
            sb.AppendLine("Main read: the counter key arrives too late. Practice the opposite-key press as one combined motion with the release, not as a separate second action.");
        }
        else if (early > 0)
        {
            sb.AppendLine("Main read: some clicks happen before the counter key window. Delay the shot until the counter input has registered and the mouse movement has stabilized.");
        }
        else
        {
            sb.AppendLine("Main read: timing is reasonably balanced. The next gain is reducing spread and removing outliers.");
        }

        return sb.ToString().Trim();
    }

    private string BuildDirectionInsight(DirectionStats aToD, DirectionStats dToA)
    {
        if (aToD.Count < 3 || dToA.Count < 3)
        {
            return "Record at least 3 included attempts in each direction before trusting the direction comparison. Fewer attempts can make one direction look worse by chance.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Use this page to find a hand-bias. A>D and D>A should have similar result mix, similar average counter delay, and similar aim efficiency.");
        sb.AppendLine();
        sb.AppendLine($"A>D: {aToD.Count} attempts, avg {aToD.AvgDelay:+0.0;-0.0;0.0} ms, late {aToD.LateRate:0}%, overlap {aToD.OverlapRate:0}%, moving {aToD.MovingRate:0}%, aim eff {aToD.AvgEfficiency:0.00}.");
        sb.AppendLine($"D>A: {dToA.Count} attempts, avg {dToA.AvgDelay:+0.0;-0.0;0.0} ms, late {dToA.LateRate:0}%, overlap {dToA.OverlapRate:0}%, moving {dToA.MovingRate:0}%, aim eff {dToA.AvgEfficiency:0.00}.");
        sb.AppendLine();

        double lateDiff = aToD.LateRate - dToA.LateRate;
        double overlapDiff = aToD.OverlapRate - dToA.OverlapRate;
        double movingDiff = aToD.MovingRate - dToA.MovingRate;
        double delayDiff = aToD.AvgDelay - dToA.AvgDelay;
        double aimDiff = aToD.AvgEfficiency - dToA.AvgEfficiency;

        if (Math.Abs(lateDiff) >= 12)
        {
            string dir = lateDiff > 0 ? "A>D" : "D>A";
            sb.AppendLine($"Timing action: {dir} is late more often. Drill only that direction for 2-3 minutes and exaggerate how soon the counter key arrives.");
        }
        if (Math.Abs(overlapDiff) >= 12)
        {
            string dir = overlapDiff > 0 ? "A>D" : "D>A";
            sb.AppendLine($"Timing action: {dir} overlaps more often. Drill release-before-counter on that side; slow the sequence down until red segments shrink.");
        }
        if (Math.Abs(delayDiff) >= 10)
        {
            string dir = delayDiff > 0 ? "A>D" : "D>A";
            sb.AppendLine($"Delay action: {dir} has the larger average delay by {Math.Abs(delayDiff):0.0} ms. Treat it as your weaker side until the bars match.");
        }
        if (Math.Abs(movingDiff) >= 12)
        {
            string dir = movingDiff > 0 ? "A>D" : "D>A";
            sb.AppendLine($"Result action: {dir} has more moving shots. Release the counter key before M1 on that side.");
        }
        if (Math.Abs(aimDiff) >= 0.08 && aToD.TraceCount > 0 && dToA.TraceCount > 0)
        {
            string dir = aimDiff < 0 ? "A>D" : "D>A";
            sb.AppendLine($"Aim action: {dir} has lower path efficiency. Review highlighted traces for that direction and look for overflicks or unnecessary corrections.");
        }
        if (Math.Abs(lateDiff) < 12 && Math.Abs(overlapDiff) < 12 && Math.Abs(delayDiff) < 10)
        {
            sb.AppendLine("Direction action: no major direction bias. Work on global consistency instead of favoring one side.");
        }

        return sb.ToString().Trim();
    }

    private string BuildAimInsight(IReadOnlyList<TraceProfile> profiles)
    {
        var usable = profiles.Where(p => p.Kind != MousePathKind.NoTrace).ToList();
        if (usable.Count == 0)
        {
            return "No usable mouse traces yet. Do attempts where you counter-strafe, move/correct the mouse, then click. The map needs movement between the counter key and M1.";
        }

        int total = usable.Count;
        int single = usable.Count(p => p.Kind == MousePathKind.SingleLine);
        int micro = usable.Count(p => p.Kind == MousePathKind.MicroAdjust);
        int overflick = usable.Count(p => p.Kind == MousePathKind.Overflick);
        int messy = usable.Count(p => p.Kind == MousePathKind.Messy);
        double avgEfficiency = usable.Average(p => p.Efficiency);
        double avgOvershoot = usable.Average(p => p.OvershootDegrees);
        double targetZoneRate = usable.Count(InAimTargetZone) * 100.0 / total;

        var sb = new StringBuilder();
        sb.AppendLine("Best dots are high on the chart and inside the click timing band. Low dots mean the path was inefficient. Right-side dots mean you clicked late. Dots left of the band mean you clicked too early.");
        sb.AppendLine();
        sb.AppendLine($"Target-zone rate: {targetZoneRate:0}%");
        sb.AppendLine($"Average path efficiency: {avgEfficiency:0.00}");
        sb.AppendLine($"Average overshoot: {avgOvershoot:0.00} deg");
        sb.AppendLine($"Single-line {Pct(single, total):0}%, micro-adjust {Pct(micro, total):0}%, overflick {Pct(overflick, total):0}%, messy {Pct(messy, total):0}%.");
        sb.AppendLine();

        if (Pct(overflick, total) >= 25)
        {
            sb.AppendLine("Aim action: overflicks are common. Stop the fast flick earlier, then make a smaller final correction before clicking. In the map, you want fewer red dots and more yellow/green dots inside the target zone.");
        }
        else if (Pct(messy, total) >= 25)
        {
            sb.AppendLine("Aim action: many paths are messy. This usually means too many direction changes before the click. Slow the shot down until the path becomes one main movement plus one small correction.");
        }
        else if (Pct(micro, total) >= 35)
        {
            sb.AppendLine("Aim action: you micro-adjust often. That is good if the click happens after the correction. If yellow dots sit left of the click band, you are clicking during the correction instead of after it.");
        }
        else if (Pct(single, total) >= 55)
        {
            sb.AppendLine("Aim action: many paths are clean single-line movements. Keep that feel and focus on bringing click timing into the target zone.");
        }
        else
        {
            sb.AppendLine("Aim action: the session is mixed. Review the low-efficiency dots first, then compare them to the cleanest green traces in the overlay.");
        }

        return sb.ToString().Trim();
    }

    private string BuildConsistencyInsight(IReadOnlyList<TraceProfile> profiles)
    {
        if (_attempts.Count < 4)
        {
            return "Record at least 4 attempts before using consistency conclusions. With fewer attempts, one outlier can dominate the chart.";
        }

        int split = _attempts.Count / 2;
        var first = _attempts.Take(split).ToList();
        var second = _attempts.Skip(split).ToList();
        double firstAvg = Average(first.Select(a => a.CounterDelayMs));
        double secondAvg = Average(second.Select(a => a.CounterDelayMs));
        double spread = StandardDeviation(_attempts.Select(a => a.CounterDelayMs));
        double clickSpread = StandardDeviation(_attempts.Where(a => a.ClickFromCounterMs.HasValue).Select(a => a.ClickFromCounterMs!.Value));
        var outliers = GetOutlierScores(profiles).OrderByDescending(o => o.Score).Take(3).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Use this tab to spot fatigue, warm-up problems, and isolated attempts worth reviewing.");
        sb.AppendLine();
        sb.AppendLine($"First half avg counter delay: {firstAvg:+0.0;-0.0;0.0} ms");
        sb.AppendLine($"Second half avg counter delay: {secondAvg:+0.0;-0.0;0.0} ms");
        sb.AppendLine($"Counter timing spread: {spread:0.0} ms");
        sb.AppendLine($"Click timing spread: {clickSpread:0.0} ms");
        sb.AppendLine();

        double drift = secondAvg - firstAvg;
        if (Math.Abs(drift) >= 10)
        {
            string direction = drift > 0 ? "later" : "more overlapped/earlier";
            sb.AppendLine($"Consistency action: timing drifts {direction} by {Math.Abs(drift):0.0} ms in the second half. Shorter sets or a reset between reps may help.");
        }
        else
        {
            sb.AppendLine("Consistency action: no large first-half to second-half drift. Focus on individual outliers rather than fatigue.");
        }

        if (spread >= 30)
        {
            sb.AppendLine("Timing spread is high. Slow the drill down and aim for repeated identical key timing before increasing speed.");
        }
        if (clickSpread >= 45)
        {
            sb.AppendLine("Click timing spread is high. Use the click chart to see whether shots are rushed, delayed, or inconsistent after the counter key.");
        }
        if (outliers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Review these attempts in the main table/trace overlay:");
            foreach (var o in outliers)
            {
                sb.AppendLine($"Attempt {o.Attempt.Index}: score {o.Score:0}, {o.Reason}");
            }
        }

        return sb.ToString().Trim();
    }

    private string BuildConclusions(DirectionStats aToD, DirectionStats dToA, IReadOnlyList<TraceProfile> profiles)
    {
        var sb = new StringBuilder();
        int total = Math.Max(1, _attempts.Count);
        int perfect = _attempts.Count(a => a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick);
        int overlap = _attempts.Count(a => a.Grade == TimingGrade.Overlap);
        int late = _attempts.Count(a => a.Grade == TimingGrade.Late);
        int early = _attempts.Count(a => a.Grade == TimingGrade.EarlyClick);
        int missed = _attempts.Count(a => a.Grade == TimingGrade.MissedClick);
        int moving = _attempts.Count(a => a.IsMovingAtClick);
        var usableProfiles = profiles.Where(p => p.Kind != MousePathKind.NoTrace).ToList();

        sb.AppendLine("1. Biggest timing leak");
        if (moving > 0 && moving >= late && moving >= overlap)
        {
            sb.AppendLine($"Moving shots are your biggest leak: {Pct(moving, total):0}% of attempts were clicked while A/D was still held. Result: moving/inaccurate.");
        }
        else if (late >= overlap && late >= early && late > 0)
        {
            sb.AppendLine($"You are late more often than you overlap: {Pct(late, total):0}% late vs {Pct(overlap, total):0}% overlap. Drill faster opposite-key activation after release.");
        }
        else if (overlap > late && overlap > 0)
        {
            sb.AppendLine($"You overlap more often than you are late: {Pct(overlap, total):0}% overlap vs {Pct(late, total):0}% late. Drill clean release-before-counter timing.");
        }
        else if (early > 0)
        {
            sb.AppendLine($"Early clicks appear in {Pct(early, total):0}% of attempts. Delay the shot until the counter key and mouse correction settle.");
        }
        else
        {
            sb.AppendLine($"Timing is stable: {Pct(perfect, total):0}% clean. Work on reducing outliers and keeping both directions equal.");
        }
        if (missed > 0) sb.AppendLine($"Missed/no-click attempts are {Pct(missed, total):0}% of included rows. Mark true jiggles as excluded so they do not distort training stats.");
        if (moving > 0) sb.AppendLine($"Moving-at-shot attempts are logged separately from timing mistakes, so overlap + moving can be reviewed as two mistakes with one result.");

        sb.AppendLine();
        sb.AppendLine("2. Direction focus");
        if (aToD.Count >= 3 && dToA.Count >= 3)
        {
            double lateDiff = aToD.LateRate - dToA.LateRate;
            double overlapDiff = aToD.OverlapRate - dToA.OverlapRate;
            double delayDiff = aToD.AvgDelay - dToA.AvgDelay;
            if (Math.Abs(lateDiff) >= 12 || Math.Abs(overlapDiff) >= 12 || Math.Abs(delayDiff) >= 10)
            {
                if (Math.Abs(lateDiff) >= 12) sb.AppendLine((lateDiff > 0 ? "A>D" : "D>A") + " has the stronger late tendency.");
                if (Math.Abs(overlapDiff) >= 12) sb.AppendLine((overlapDiff > 0 ? "A>D" : "D>A") + " has the stronger overlap tendency.");
                if (Math.Abs(delayDiff) >= 10) sb.AppendLine((delayDiff > 0 ? "A>D" : "D>A") + $" is slower on average by {Math.Abs(delayDiff):0.0} ms.");
            }
            else
            {
                sb.AppendLine("A>D and D>A are close enough that you should drill both sides evenly.");
            }
        }
        else
        {
            sb.AppendLine("More attempts are needed in both directions before drawing a direction conclusion.");
        }

        sb.AppendLine();
        sb.AppendLine("3. Mouse/aim focus");
        if (usableProfiles.Count > 0)
        {
            int overflick = usableProfiles.Count(p => p.Kind == MousePathKind.Overflick);
            int messy = usableProfiles.Count(p => p.Kind == MousePathKind.Messy);
            int micro = usableProfiles.Count(p => p.Kind == MousePathKind.MicroAdjust);
            int single = usableProfiles.Count(p => p.Kind == MousePathKind.SingleLine);
            double targetRate = usableProfiles.Count(InAimTargetZone) * 100.0 / usableProfiles.Count;
            sb.AppendLine($"Target-zone rate is {targetRate:0}%. Single-line {Pct(single, usableProfiles.Count):0}%, micro-adjust {Pct(micro, usableProfiles.Count):0}%, overflick {Pct(overflick, usableProfiles.Count):0}%, messy {Pct(messy, usableProfiles.Count):0}%.");
            if (Pct(overflick, usableProfiles.Count) >= 25) sb.AppendLine("Main aim drill: reduce flick amplitude and add a deliberate micro-correction before clicking.");
            else if (Pct(messy, usableProfiles.Count) >= 25) sb.AppendLine("Main aim drill: remove extra mouse direction changes. Aim for one main path, then one small correction.");
            else if (Pct(micro, usableProfiles.Count) >= 35) sb.AppendLine("Main aim drill: keep micro-adjusting, but make sure the click is after the final adjustment, not during it.");
            else sb.AppendLine("Main aim drill: path quality is acceptable. Push timing consistency while keeping the same path shape.");
        }
        else
        {
            sb.AppendLine("No mouse traces were available. Include mouse movement before M1 to use aim conclusions.");
        }

        sb.AppendLine();
        sb.AppendLine("4. Suggested drill structure");
        sb.AppendLine("- 2 minutes only A>D, then 2 minutes only D>A.");
        sb.AppendLine("- Exclude jiggles/no-shot reps after the session.");
        sb.AppendLine("- Open Direction compare: fix the side with more red/cyan first.");
        sb.AppendLine("- Open Mouse / aim: review low-efficiency and red overflick dots in the trace overlay.");
        sb.AppendLine("- Repeat until the direction bars and timing-over-session lines look similar on both sides.");

        return sb.ToString().Trim();
    }

    private void DrawTimingChart()
    {
        TimingCanvas.Children.Clear();
        double width = TimingCanvas.ActualWidth;
        double height = TimingCanvas.ActualHeight;
        if (width < 80 || height < 80) return;
        if (_attempts.Count == 0)
        {
            DrawNoData(TimingCanvas, "No included attempts.");
            return;
        }

        double left = 54, right = 18, top = 20, bottom = 42;
        double plotW = width - left - right;
        double plotH = height - top - bottom;
        const int bucketSize = 15;
        double minDelay = Math.Max(-90, _attempts.Min(a => a.CounterDelayMs));
        double maxDelay = Math.Min(240, _attempts.Max(a => a.CounterDelayMs));
        int minBucket = (int)Math.Floor(minDelay / bucketSize) * bucketSize;
        int maxBucket = (int)Math.Ceiling(maxDelay / bucketSize) * bucketSize;
        minBucket = Math.Min(minBucket, -15);
        maxBucket = Math.Max(maxBucket, 180);
        double axisMax = maxBucket + bucketSize;
        int bucketCount = Math.Max(1, (maxBucket - minBucket) / bucketSize + 1);

        var buckets = new Dictionary<int, Dictionary<TimingGrade, int>>();
        foreach (var a in _attempts)
        {
            int b = (int)Math.Floor(Math.Clamp(a.CounterDelayMs, minBucket, maxBucket) / bucketSize) * bucketSize;
            if (!buckets.TryGetValue(b, out var grades))
            {
                grades = new Dictionary<TimingGrade, int>();
                buckets[b] = grades;
            }
            grades[a.Grade] = grades.GetValueOrDefault(a.Grade) + 1;
        }

        int maxStack = Math.Max(1, buckets.Values.Select(d => d.Values.Sum()).DefaultIfEmpty(1).Max());
        DrawChartFrame(TimingCanvas, left, top, plotW, plotH, maxStack, "count");
        DrawVerticalBand(TimingCanvas, left, top, plotW, plotH, minBucket, axisMax, _settings.IdealCounterMinMs, _settings.IdealCounterMaxMs, new SolidColorBrush(Color.FromArgb(42, 56, 217, 150)), "target");

        double barW = plotW / bucketCount;
        var order = new[] { TimingGrade.Overlap, TimingGrade.Late, TimingGrade.Perfect, TimingGrade.EarlyClick, TimingGrade.MissedClick, TimingGrade.Unrated };
        for (int i = 0; i < bucketCount; i++)
        {
            int bucket = minBucket + i * bucketSize;
            double x = left + i * barW + 1;
            double y = top + plotH;
            if (buckets.TryGetValue(bucket, out var grades))
            {
                foreach (var grade in order)
                {
                    int count = grades.GetValueOrDefault(grade);
                    if (count == 0) continue;
                    double h = count / (double)maxStack * plotH;
                    y -= h;
                    TimingCanvas.Children.Add(new Rectangle
                    {
                        Width = Math.Max(1, barW - 2),
                        Height = h,
                        Fill = BrushForGrade(grade),
                        Opacity = 0.86
                    }.At(x, y));
                }
            }

            if (i % 2 == 0 || bucket == 0)
            {
                AddCanvasText(TimingCanvas, bucket.ToString(CultureInfo.InvariantCulture), x - 4, top + plotH + 8, 11, MutedBrush());
            }
        }

        if (minBucket <= 0 && maxBucket >= 0)
        {
            double zeroX = MapX(0, minBucket, axisMax, left, plotW);
            TimingCanvas.Children.Add(new Line { X1 = zeroX, X2 = zeroX, Y1 = top, Y2 = top + plotH, Stroke = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), StrokeThickness = 1.3 });
            AddCanvasText(TimingCanvas, "0 ms", zeroX + 4, top + 4, 11, new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)));
        }

        AddCanvasText(TimingCanvas, "counter delay ms", left + plotW - 110, height - 20, 12, MutedBrush());
    }

    private void DrawDirectionCharts()
    {
        var aToD = DirectionStats.Build(_attempts, "A", "D");
        var dToA = DirectionStats.Build(_attempts, "D", "A");
        DrawDirectionRateChart(aToD, dToA);
        DrawDirectionDelayChart(aToD, dToA);
        DrawDirectionAimChart(aToD, dToA);
    }

    private void DrawDirectionRateChart(DirectionStats aToD, DirectionStats dToA)
    {
        DirectionRateCanvas.Children.Clear();
        double width = DirectionRateCanvas.ActualWidth;
        double height = DirectionRateCanvas.ActualHeight;
        if (width < 80 || height < 80) return;
        if (aToD.Count + dToA.Count == 0)
        {
            DrawNoData(DirectionRateCanvas, "No direction data.");
            return;
        }

        double left = 72, right = 18, top = 42, bottom = 40;
        double plotW = width - left - right;
        double barH = Math.Min(72, (height - top - bottom) / 3.2);
        double gap = barH * 0.75;
        var stats = new[] { aToD, dToA };

        AddCanvasText(DirectionRateCanvas, "0%", left, top - 24, 11, MutedBrush());
        AddCanvasText(DirectionRateCanvas, "50%", left + plotW * 0.5 - 12, top - 24, 11, MutedBrush());
        AddCanvasText(DirectionRateCanvas, "100%", left + plotW - 28, top - 24, 11, MutedBrush());
        for (int i = 0; i <= 4; i++)
        {
            double x = left + plotW * i / 4.0;
            DirectionRateCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top - 6, Y2 = top + 2 * (barH + gap), Stroke = new SolidColorBrush(Color.FromArgb(38, 168, 176, 200)), StrokeThickness = 1 });
        }

        for (int i = 0; i < stats.Length; i++)
        {
            var s = stats[i];
            double y = top + i * (barH + gap);
            AddCanvasText(DirectionRateCanvas, s.Label, 14, y + barH / 2 - 10, 14, TextBrush());
            AddCanvasText(DirectionRateCanvas, $"n={s.Count}", 14, y + barH / 2 + 8, 11, MutedBrush());
            double x = left;
            foreach (var result in s.ResultRates())
            {
                double pct = result.Rate;
                double w = pct / 100.0 * plotW;
                if (w <= 0) continue;
                DirectionRateCanvas.Children.Add(new Rectangle { Width = w, Height = barH, Fill = result.Brush, Opacity = 0.88 }.At(x, y));
                if (w > 42)
                {
                    AddCanvasText(DirectionRateCanvas, $"{result.Label} {pct:0}%", x + 6, y + barH / 2 - 9, 12, new SolidColorBrush(Colors.White));
                }
                x += w;
            }
            DirectionRateCanvas.Children.Add(new Rectangle { Width = plotW, Height = barH, Stroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), StrokeThickness = 1, Fill = Brushes.Transparent }.At(left, y));
        }
    }

    private void DrawDirectionDelayChart(DirectionStats aToD, DirectionStats dToA)
    {
        DirectionDelayCanvas.Children.Clear();
        DrawTwoValueBars(DirectionDelayCanvas, "ms", aToD.Label, aToD.AvgDelay, dToA.Label, dToA.AvgDelay, _settings.IdealCounterMinMs, _settings.IdealCounterMaxMs, true);
    }

    private void DrawDirectionAimChart(DirectionStats aToD, DirectionStats dToA)
    {
        DirectionAimCanvas.Children.Clear();
        DrawTwoValueBars(DirectionAimCanvas, "eff", aToD.Label, aToD.AvgEfficiency, dToA.Label, dToA.AvgEfficiency, 0.82, 1.0, false, 0, 1);
    }

    private void DrawAimCharts()
    {
        DrawAimChart();
        DrawAimBreakdownChart();
    }

    private void DrawAimChart()
    {
        AimCanvas.Children.Clear();
        double width = AimCanvas.ActualWidth;
        double height = AimCanvas.ActualHeight;
        if (width < 80 || height < 80) return;

        var profiles = _attempts.Where(a => a.MouseTrace.Count > 1).Select(AnalyzeTrace).ToList();
        if (profiles.Count == 0)
        {
            DrawNoData(AimCanvas, "No mouse traces yet.");
            return;
        }

        double left = 54, right = 20, top = 20, bottom = 42;
        double plotW = width - left - right;
        double plotH = height - top - bottom;
        double minX = Math.Min(_settings.IdealClickMinAfterCounterMs - 80, profiles.Min(p => p.ClickFromCounterMs));
        double maxX = Math.Max(_settings.IdealClickMaxAfterCounterMs + 120, profiles.Max(p => p.ClickFromCounterMs));
        minX = Math.Max(minX, -100);
        maxX = Math.Min(maxX, 550);

        DrawChartFrame(AimCanvas, left, top, plotW, plotH, 1, "efficiency");
        DrawVerticalBand(AimCanvas, left, top, plotW, plotH, minX, maxX, _settings.IdealClickMinAfterCounterMs, _settings.IdealClickMaxAfterCounterMs, new SolidColorBrush(Color.FromArgb(36, 0, 224, 255)), "click target");
        DrawHorizontalBand(AimCanvas, left, top, plotW, plotH, 0, 1, 0.82, 1.0, new SolidColorBrush(Color.FromArgb(30, 56, 217, 150)), "clean path zone");

        for (int i = 0; i <= 5; i++)
        {
            double eff = i / 5.0;
            double y = top + plotH - eff * plotH;
            AddCanvasText(AimCanvas, eff.ToString("0.0", CultureInfo.InvariantCulture), 8, y - 8, 11, MutedBrush());
        }

        DrawHorizontalGuide(AimCanvas, left, top + plotH - 0.85 * plotH, plotW, "straight-ish");
        DrawHorizontalGuide(AimCanvas, left, top + plotH - 0.55 * plotH, plotW, "messy below");

        foreach (var p in profiles)
        {
            double x = MapX(Math.Clamp(p.ClickFromCounterMs, minX, maxX), minX, maxX, left, plotW);
            double y = top + plotH - Math.Clamp(p.Efficiency, 0, 1) * plotH;
            double r = p.Kind == MousePathKind.Overflick ? 5.5 : 4.5;
            var dot = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = BrushForPathKind(p.Kind),
                Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                StrokeThickness = 0.8,
                Opacity = 0.92
            };
            AimCanvas.Children.Add(dot.At(x - r, y - r));
        }

        for (int i = 0; i <= 4; i++)
        {
            double f = i / 4.0;
            double x = left + f * plotW;
            double value = minX + f * (maxX - minX);
            AddCanvasText(AimCanvas, value.ToString("0", CultureInfo.InvariantCulture), x - 10, top + plotH + 8, 11, MutedBrush());
        }
        AddCanvasText(AimCanvas, "click after counter ms", left + plotW - 138, height - 20, 12, MutedBrush());
    }

    private void DrawAimBreakdownChart()
    {
        AimBreakdownCanvas.Children.Clear();
        double width = AimBreakdownCanvas.ActualWidth;
        double height = AimBreakdownCanvas.ActualHeight;
        if (width < 80 || height < 80) return;

        var profiles = _attempts.Where(a => a.MouseTrace.Count > 1).Select(AnalyzeTrace).Where(p => p.Kind != MousePathKind.NoTrace).ToList();
        if (profiles.Count == 0)
        {
            DrawNoData(AimBreakdownCanvas, "No mouse traces yet.");
            return;
        }

        var kinds = new[] { MousePathKind.SingleLine, MousePathKind.MicroAdjust, MousePathKind.Overflick, MousePathKind.Messy };
        var counts = kinds.Select(k => profiles.Count(p => p.Kind == k)).ToArray();
        int max = Math.Max(1, counts.Max());
        double left = 48, right = 16, top = 18, bottom = 56;
        double plotW = width - left - right;
        double plotH = height - top - bottom;
        DrawChartFrame(AimBreakdownCanvas, left, top, plotW, plotH, max, "count");

        double barW = plotW / kinds.Length * 0.62;
        for (int i = 0; i < kinds.Length; i++)
        {
            double cx = left + (i + 0.5) * plotW / kinds.Length;
            double h = counts[i] / (double)max * plotH;
            double y = top + plotH - h;
            AimBreakdownCanvas.Children.Add(new Rectangle { Width = barW, Height = h, Fill = BrushForPathKind(kinds[i]), Opacity = 0.9 }.At(cx - barW / 2, y));
            AddCanvasText(AimBreakdownCanvas, counts[i].ToString(CultureInfo.InvariantCulture), cx - 5, y - 18, 12, TextBrush());
            AddCanvasText(AimBreakdownCanvas, ShortLabelForPathKind(kinds[i]), cx - 30, top + plotH + 10, 11, MutedBrush());
        }
    }

    private void DrawConsistencyCharts()
    {
        DrawTimingOverSessionChart();
        DrawClickOverSessionChart();
        DrawOutlierChart();
    }

    private void DrawTimingOverSessionChart()
    {
        ConsistencyCanvas.Children.Clear();
        DrawAttemptSeries(ConsistencyCanvas, _attempts.Select(a => new SeriesPoint(a.Index, a.CounterDelayMs, BrushForGrade(a.Grade))).ToList(), _settings.IdealCounterMinMs, _settings.IdealCounterMaxMs, "counter delay ms");
    }

    private void DrawClickOverSessionChart()
    {
        ReactionConsistencyCanvas.Children.Clear();
        var points = _attempts
            .Where(a => a.ClickFromCounterMs.HasValue)
            .Select(a => new SeriesPoint(a.Index, a.ClickFromCounterMs!.Value, BrushForGrade(a.Grade)))
            .ToList();
        DrawAttemptSeries(ReactionConsistencyCanvas, points, _settings.IdealClickMinAfterCounterMs, _settings.IdealClickMaxAfterCounterMs, "click after counter ms");
    }

    private void DrawOutlierChart()
    {
        OutlierCanvas.Children.Clear();
        double width = OutlierCanvas.ActualWidth;
        double height = OutlierCanvas.ActualHeight;
        if (width < 80 || height < 80) return;

        var profiles = _attempts.Where(a => a.MouseTrace.Count > 1).Select(AnalyzeTrace).ToList();
        var outliers = GetOutlierScores(profiles).ToList();
        if (outliers.Count == 0)
        {
            DrawNoData(OutlierCanvas, "No outliers yet.");
            return;
        }

        double left = 48, right = 16, top = 18, bottom = 38;
        double plotW = width - left - right;
        double plotH = height - top - bottom;
        double maxScore = Math.Max(10, outliers.Max(o => o.Score));
        DrawChartFrame(OutlierCanvas, left, top, plotW, plotH, (int)Math.Ceiling(maxScore), "score");

        int minIndex = _attempts.Min(a => a.Index);
        int maxIndex = _attempts.Max(a => a.Index);
        foreach (var o in outliers)
        {
            double x = MapX(o.Attempt.Index, minIndex, maxIndex == minIndex ? minIndex + 1 : maxIndex, left, plotW);
            double y = top + plotH - Math.Clamp(o.Score / maxScore, 0, 1) * plotH;
            double r = o.Score >= outliers.OrderByDescending(x => x.Score).Take(Math.Min(5, outliers.Count)).Last().Score ? 6.5 : 4.0;
            OutlierCanvas.Children.Add(new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = BrushForGrade(o.Attempt.Grade),
                Stroke = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                StrokeThickness = 0.9
            }.At(x - r, y - r));
        }
        AddCanvasText(OutlierCanvas, "attempt index", left + plotW - 88, height - 20, 12, MutedBrush());
    }

    private void DrawAttemptSeries(Canvas canvas, IReadOnlyList<SeriesPoint> points, double idealMin, double idealMax, string yLabel)
    {
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width < 80 || height < 80) return;
        if (points.Count == 0)
        {
            DrawNoData(canvas, "No data.");
            return;
        }

        double left = 54, right = 18, top = 18, bottom = 38;
        double plotW = width - left - right;
        double plotH = height - top - bottom;
        double minY = Math.Min(points.Min(p => p.Value), idealMin) - 20;
        double maxY = Math.Max(points.Max(p => p.Value), idealMax) + 20;
        minY = Math.Max(-140, minY);
        maxY = Math.Min(550, maxY);
        if (Math.Abs(maxY - minY) < 1) maxY = minY + 1;

        int minIndex = points.Min(p => p.Index);
        int maxIndex = points.Max(p => p.Index);
        if (minIndex == maxIndex) maxIndex = minIndex + 1;

        DrawChartFrame(canvas, left, top, plotW, plotH, 0, yLabel);
        DrawHorizontalBand(canvas, left, top, plotW, plotH, minY, maxY, idealMin, idealMax, new SolidColorBrush(Color.FromArgb(38, 56, 217, 150)), "target");
        if (minY <= 0 && maxY >= 0)
        {
            double zeroY = MapY(0, minY, maxY, top, plotH);
            canvas.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = zeroY, Y2 = zeroY, Stroke = new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)), StrokeThickness = 1 });
            AddCanvasText(canvas, "0", 12, zeroY - 8, 11, MutedBrush());
        }

        Point? previous = null;
        foreach (var p in points)
        {
            double x = MapX(p.Index, minIndex, maxIndex, left, plotW);
            double y = MapY(p.Value, minY, maxY, top, plotH);
            if (previous.HasValue)
            {
                canvas.Children.Add(new Line { X1 = previous.Value.X, Y1 = previous.Value.Y, X2 = x, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)), StrokeThickness = 1.2 });
            }
            canvas.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = p.Brush,
                Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                StrokeThickness = 0.8
            }.At(x - 4, y - 4));
            previous = new Point(x, y);
        }

        for (int i = 0; i <= 4; i++)
        {
            double f = i / 4.0;
            double y = top + f * plotH;
            double value = maxY - f * (maxY - minY);
            AddCanvasText(canvas, value.ToString("0", CultureInfo.InvariantCulture), 12, y - 8, 11, MutedBrush());
        }
        AddCanvasText(canvas, "attempt index", left + plotW - 88, height - 20, 12, MutedBrush());
    }

    private static void DrawTwoValueBars(Canvas canvas, string yLabel, string labelA, double valueA, string labelB, double valueB, double idealMin, double idealMax, bool allowNegative, double? fixedMin = null, double? fixedMax = null)
    {
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width < 80 || height < 80) return;

        double left = 50, right = 16, top = 18, bottom = 40;
        double plotW = width - left - right;
        double plotH = height - top - bottom;
        double minY = fixedMin ?? Math.Min(Math.Min(valueA, valueB), idealMin) - 15;
        double maxY = fixedMax ?? Math.Max(Math.Max(valueA, valueB), idealMax) + 15;
        if (!allowNegative) minY = Math.Min(0, minY);
        if (Math.Abs(maxY - minY) < 0.01) maxY = minY + 1;

        DrawChartFrame(canvas, left, top, plotW, plotH, 0, yLabel);
        DrawHorizontalBand(canvas, left, top, plotW, plotH, minY, maxY, idealMin, idealMax, new SolidColorBrush(Color.FromArgb(36, 56, 217, 150)), "target");

        if (allowNegative && minY <= 0 && maxY >= 0)
        {
            double zeroY = MapY(0, minY, maxY, top, plotH);
            canvas.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = zeroY, Y2 = zeroY, Stroke = new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)), StrokeThickness = 1 });
        }

        double[] values = { valueA, valueB };
        string[] labels = { labelA, labelB };
        Brush[] brushes = { new SolidColorBrush(Color.FromRgb(124, 92, 255)), new SolidColorBrush(Color.FromRgb(0, 224, 255)) };
        double zero = allowNegative && minY <= 0 && maxY >= 0 ? 0 : minY;
        double zeroY2 = MapY(zero, minY, maxY, top, plotH);
        double groupW = plotW / 2.0;
        double barW = Math.Min(58, groupW * 0.45);

        for (int i = 0; i < 2; i++)
        {
            double x = left + groupW * i + groupW / 2 - barW / 2;
            double y = MapY(values[i], minY, maxY, top, plotH);
            double h = Math.Abs(zeroY2 - y);
            canvas.Children.Add(new Rectangle { Width = barW, Height = Math.Max(2, h), Fill = brushes[i], Opacity = 0.9 }.At(x, Math.Min(y, zeroY2)));
            AddCanvasText(canvas, values[i].ToString(allowNegative ? "+0.0;-0.0;0.0" : "0.00", CultureInfo.InvariantCulture), x - 6, Math.Min(y, zeroY2) - 20, 12, TextBrush());
            AddCanvasText(canvas, labels[i], x - 6, top + plotH + 10, 12, MutedBrush());
        }
    }

    private static void DrawChartFrame(Canvas canvas, double left, double top, double plotW, double plotH, int maxY, string yLabel)
    {
        var grid = new SolidColorBrush(Color.FromArgb(42, 168, 176, 200));
        var axis = new SolidColorBrush(Color.FromArgb(125, 168, 176, 200));
        for (int i = 0; i <= 5; i++)
        {
            double y = top + i / 5.0 * plotH;
            canvas.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
            if (maxY > 0)
            {
                int label = (int)Math.Round(maxY * (1 - i / 5.0));
                AddCanvasText(canvas, label.ToString(CultureInfo.InvariantCulture), 12, y - 8, 11, MutedBrush());
            }
        }
        for (int i = 0; i <= 4; i++)
        {
            double x = left + i / 4.0 * plotW;
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + plotH, Stroke = new SolidColorBrush(Color.FromArgb(25, 168, 176, 200)), StrokeThickness = 1 });
        }
        canvas.Children.Add(new Line { X1 = left, X2 = left, Y1 = top, Y2 = top + plotH, Stroke = axis, StrokeThickness = 1.2 });
        canvas.Children.Add(new Line { X1 = left, X2 = left + plotW, Y1 = top + plotH, Y2 = top + plotH, Stroke = axis, StrokeThickness = 1.2 });
        AddCanvasText(canvas, yLabel, left + 4, top + 4, 12, MutedBrush());
    }

    private static void DrawVerticalBand(Canvas canvas, double left, double top, double plotW, double plotH, double minX, double maxX, double bandMin, double bandMax, Brush brush, string label)
    {
        double x1 = MapX(Math.Clamp(bandMin, minX, maxX), minX, maxX, left, plotW);
        double x2 = MapX(Math.Clamp(bandMax, minX, maxX), minX, maxX, left, plotW);
        if (Math.Abs(x2 - x1) < 1) return;
        canvas.Children.Add(new Rectangle { Width = Math.Abs(x2 - x1), Height = plotH, Fill = brush }.At(Math.Min(x1, x2), top));
        AddCanvasText(canvas, label, Math.Min(x1, x2) + 5, top + 18, 11, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)));
    }

    private static void DrawHorizontalBand(Canvas canvas, double left, double top, double plotW, double plotH, double minY, double maxY, double bandMin, double bandMax, Brush brush, string label)
    {
        double y1 = MapY(Math.Clamp(bandMin, minY, maxY), minY, maxY, top, plotH);
        double y2 = MapY(Math.Clamp(bandMax, minY, maxY), minY, maxY, top, plotH);
        if (Math.Abs(y2 - y1) < 1) return;
        canvas.Children.Add(new Rectangle { Width = plotW, Height = Math.Abs(y2 - y1), Fill = brush }.At(left, Math.Min(y1, y2)));
        AddCanvasText(canvas, label, left + 8, Math.Min(y1, y2) + 5, 11, new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)));
    }

    private static void DrawHorizontalGuide(Canvas canvas, double left, double y, double width, string label)
    {
        var brush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        canvas.Children.Add(new Line { X1 = left, X2 = left + width, Y1 = y, Y2 = y, Stroke = brush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 } });
        AddCanvasText(canvas, label, left + 8, y + 4, 11, brush);
    }

    private static void DrawNoData(Canvas canvas, string text)
    {
        AddCanvasText(canvas, text, 18, 18, 14, MutedBrush());
    }

    private static void AddCanvasText(Canvas canvas, string text, double x, double y, double fontSize, Brush brush)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = brush
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        canvas.Children.Add(tb);
    }

    private static double MapX(double value, double min, double max, double left, double width)
    {
        if (Math.Abs(max - min) < 0.0001) return left;
        return left + (value - min) / (max - min) * width;
    }

    private static double MapY(double value, double min, double max, double top, double height)
    {
        if (Math.Abs(max - min) < 0.0001) return top + height;
        return top + height - (value - min) / (max - min) * height;
    }

    private static Brush BrushForGrade(TimingGrade grade)
    {
        return grade switch
        {
            TimingGrade.Perfect => new SolidColorBrush(Color.FromRgb(255, 206, 69)),
            TimingGrade.Overlap => new SolidColorBrush(Color.FromRgb(255, 92, 122)),
            TimingGrade.Late => new SolidColorBrush(Color.FromRgb(0, 224, 255)),
            TimingGrade.EarlyClick => new SolidColorBrush(Color.FromRgb(177, 140, 255)),
            TimingGrade.MissedClick => new SolidColorBrush(Color.FromRgb(168, 176, 200)),
            _ => new SolidColorBrush(Color.FromRgb(120, 190, 255))
        };
    }

    private static Brush BrushForPathKind(MousePathKind kind)
    {
        return kind switch
        {
            MousePathKind.SingleLine => new SolidColorBrush(Color.FromRgb(56, 217, 150)),
            MousePathKind.MicroAdjust => new SolidColorBrush(Color.FromRgb(255, 206, 69)),
            MousePathKind.Overflick => new SolidColorBrush(Color.FromRgb(255, 92, 122)),
            MousePathKind.Messy => new SolidColorBrush(Color.FromRgb(177, 140, 255)),
            _ => new SolidColorBrush(Color.FromRgb(168, 176, 200))
        };
    }

    private static Brush MutedBrush() => new SolidColorBrush(Color.FromRgb(168, 176, 200));
    private static Brush TextBrush() => new SolidColorBrush(Color.FromRgb(245, 247, 255));

    private static TraceProfile AnalyzeTrace(StrafeAttempt attempt)
    {
        var points = new List<PlotPoint> { new(0, 0) };
        points.AddRange(attempt.MouseTrace.Select(p => new PlotPoint(p.XDegrees, p.YDegrees)));
        if (points.Count < 2 || attempt.DisplacementDegrees < 0.01)
        {
            return new TraceProfile(attempt, MousePathKind.NoTrace, 0, 0, 0, attempt.ClickFromCounterMs ?? attempt.LastPointTimeFromCounterMs, 0);
        }

        double path = PathLength(points);
        double displacement = Distance(points[0], points[^1]);
        double efficiency = path <= 0 ? 0 : displacement / path;
        int changes = DirectionChanges(points);
        double overshoot = Overshoot(points);
        double lastCorrection = LastCorrectionDistance(points);
        double lastPath = LastPortionPath(points, 0.70);

        bool isOverflick = overshoot > Math.Max(0.04, displacement * 0.12) && lastCorrection > Math.Max(0.03, displacement * 0.08);
        bool isMessy = efficiency < 0.55 || changes >= 6;
        bool isMicro = lastPath > Math.Max(0.03, path * 0.10) && lastPath < path * 0.55 && efficiency >= 0.55;
        bool isSingleLine = efficiency >= 0.82 && changes <= 2 && !isOverflick;

        MousePathKind kind;
        if (isOverflick) kind = MousePathKind.Overflick;
        else if (isMessy) kind = MousePathKind.Messy;
        else if (isSingleLine && !isMicro) kind = MousePathKind.SingleLine;
        else if (isMicro) kind = MousePathKind.MicroAdjust;
        else kind = MousePathKind.SingleLine;

        return new TraceProfile(attempt, kind, efficiency, changes, overshoot, attempt.ClickFromCounterMs ?? attempt.LastPointTimeFromCounterMs, path);
    }

    private static double PathLength(IReadOnlyList<PlotPoint> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        return total;
    }

    private static double LastPortionPath(IReadOnlyList<PlotPoint> points, double startFraction)
    {
        if (points.Count < 2) return 0;
        int start = Math.Clamp((int)Math.Floor((points.Count - 1) * startFraction), 0, points.Count - 1);
        double total = 0;
        for (int i = start + 1; i < points.Count; i++) total += Distance(points[i - 1], points[i]);
        return total;
    }

    private static double LastCorrectionDistance(IReadOnlyList<PlotPoint> points)
    {
        if (points.Count < 2) return 0;
        int start = Math.Clamp((int)Math.Floor((points.Count - 1) * 0.70), 0, points.Count - 1);
        return Distance(points[start], points[^1]);
    }

    private static int DirectionChanges(IReadOnlyList<PlotPoint> points)
    {
        int changes = 0;
        PlotPoint? previous = null;
        for (int i = 1; i < points.Count; i++)
        {
            var v = new PlotPoint(points[i].X - points[i - 1].X, points[i].Y - points[i - 1].Y);
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len < 0.003) continue;
            if (previous.HasValue)
            {
                var p = previous.Value;
                double plen = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                if (plen > 0)
                {
                    double dot = (p.X * v.X + p.Y * v.Y) / (plen * len);
                    if (dot < 0.34) changes++;
                }
            }
            previous = v;
        }
        return changes;
    }

    private static double Overshoot(IReadOnlyList<PlotPoint> points)
    {
        var end = points[^1];
        double endLen = Math.Sqrt(end.X * end.X + end.Y * end.Y);
        if (endLen < 0.01) return 0;
        double maxProjection = points.Max(p => (p.X * end.X + p.Y * end.Y) / endLen);
        return Math.Max(0, maxProjection - endLen);
    }

    private static double Distance(PlotPoint a, PlotPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private bool InAimTargetZone(TraceProfile profile)
    {
        return profile.Efficiency >= 0.82
            && profile.ClickFromCounterMs >= _settings.IdealClickMinAfterCounterMs
            && profile.ClickFromCounterMs <= _settings.IdealClickMaxAfterCounterMs;
    }

    private IEnumerable<OutlierScore> GetOutlierScores(IReadOnlyList<TraceProfile> profiles)
    {
        double counterMid = (_settings.IdealCounterMinMs + _settings.IdealCounterMaxMs) / 2.0;
        double clickMid = (_settings.IdealClickMinAfterCounterMs + _settings.IdealClickMaxAfterCounterMs) / 2.0;
        var profileByAttempt = profiles.ToDictionary(p => p.Attempt.Index, p => p);

        foreach (var attempt in _attempts)
        {
            double counterPenalty = Math.Abs(attempt.CounterDelayMs - counterMid);
            double clickPenalty = attempt.ClickFromCounterMs.HasValue ? Math.Abs(attempt.ClickFromCounterMs.Value - clickMid) * 0.55 : 50;
            double aimPenalty = 0;
            string reason = $"counter {attempt.CounterDelayMs:+0.0;-0.0;0.0} ms";
            if (profileByAttempt.TryGetValue(attempt.Index, out var profile) && profile.Kind != MousePathKind.NoTrace)
            {
                aimPenalty = (1 - Math.Clamp(profile.Efficiency, 0, 1)) * 70 + profile.OvershootDegrees * 25 + profile.DirectionChanges * 2;
                reason += $", {LabelForPathKind(profile.Kind)}, eff {profile.Efficiency:0.00}";
            }
            else if (!attempt.ClickFromCounterMs.HasValue)
            {
                reason += ", no click";
            }

            yield return new OutlierScore(attempt, counterPenalty + clickPenalty + aimPenalty, reason);
        }
    }

    private static string BuildDirectionBias(DirectionStats aToD, DirectionStats dToA)
    {
        if (aToD.Count < 3 || dToA.Count < 3) return "Need more|Record at least 3 included attempts in each direction.";

        double avgDiff = aToD.AvgDelay - dToA.AvgDelay;
        double lateDiff = aToD.LateRate - dToA.LateRate;
        double overlapDiff = aToD.OverlapRate - dToA.OverlapRate;
        double movingDiff = aToD.MovingRate - dToA.MovingRate;

        if (Math.Abs(lateDiff) >= 12)
        {
            return lateDiff > 0
                ? $"A>D late|A>D late {aToD.LateRate:0}% vs D>A {dToA.LateRate:0}%. Avg delay difference {avgDiff:+0.0;-0.0;0.0} ms."
                : $"D>A late|D>A late {dToA.LateRate:0}% vs A>D {aToD.LateRate:0}%. Avg delay difference {avgDiff:+0.0;-0.0;0.0} ms.";
        }

        if (Math.Abs(overlapDiff) >= 12)
        {
            return overlapDiff > 0
                ? $"A>D overlap|A>D overlap {aToD.OverlapRate:0}% vs D>A {dToA.OverlapRate:0}%."
                : $"D>A overlap|D>A overlap {dToA.OverlapRate:0}% vs A>D {aToD.OverlapRate:0}%.";
        }

        if (Math.Abs(movingDiff) >= 12)
        {
            return movingDiff > 0
                ? $"A>D moving|A>D moving shots {aToD.MovingRate:0}% vs D>A {dToA.MovingRate:0}%."
                : $"D>A moving|D>A moving shots {dToA.MovingRate:0}% vs A>D {aToD.MovingRate:0}%.";
        }

        return $"Balanced|Avg delay difference {avgDiff:+0.0;-0.0;0.0} ms; no large late/overlap bias.";
    }

    private static string LabelForPathKind(MousePathKind kind)
    {
        return kind switch
        {
            MousePathKind.SingleLine => "Single line",
            MousePathKind.MicroAdjust => "Micro-adjust",
            MousePathKind.Overflick => "Overflick",
            MousePathKind.Messy => "Messy",
            _ => "No trace"
        };
    }

    private static string ShortLabelForPathKind(MousePathKind kind)
    {
        return kind switch
        {
            MousePathKind.SingleLine => "single",
            MousePathKind.MicroAdjust => "micro",
            MousePathKind.Overflick => "overflick",
            MousePathKind.Messy => "messy",
            _ => "none"
        };
    }

    private static double Average(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }

    private static double StandardDeviation(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0;
        double avg = list.Average();
        return Math.Sqrt(list.Sum(v => Math.Pow(v - avg, 2)) / (list.Count - 1));
    }

    private static double Pct(int count, int total) => total <= 0 ? 0 : count * 100.0 / total;

    private sealed record DirectionStats(
        string Label,
        int Count,
        double AvgDelay,
        double PerfectRate,
        double OverlapRate,
        double LateRate,
        double EarlyRate,
        double MissedRate,
        double MovingRate,
        double AccurateRate,
        double InaccurateRate,
        double AvgEfficiency,
        int TraceCount)
    {
        public static DirectionStats Build(IReadOnlyList<StrafeAttempt> attempts, string from, string to)
        {
            var rows = attempts.Where(a => a.FromKey == from && a.ToKey == to).ToList();
            var traced = rows.Where(a => a.MouseTrace.Count > 1).ToList();
            int count = rows.Count;
            double pct(Func<StrafeAttempt, bool> predicate) => count == 0 ? 0 : rows.Count(predicate) * 100.0 / count;
            return new DirectionStats(
                $"{from}>{to}",
                count,
                count == 0 ? 0 : rows.Average(a => a.CounterDelayMs),
                pct(a => a.Grade == TimingGrade.Perfect),
                pct(a => a.Grade == TimingGrade.Overlap),
                pct(a => a.Grade == TimingGrade.Late),
                pct(a => a.Grade == TimingGrade.EarlyClick),
                pct(a => a.Grade == TimingGrade.MissedClick),
                pct(a => a.IsMovingAtClick),
                pct(a => a.ResultLabel == "Accurate"),
                pct(a => a.ResultLabel == "Inaccurate" || a.ResultLabel == "Moving"),
                traced.Count == 0 ? 0 : traced.Average(a => a.PathEfficiency),
                traced.Count);
        }

        public IEnumerable<ResultRate> ResultRates()
        {
            double moving = MovingRate;
            double inaccurateOnly = Math.Max(0, InaccurateRate - MovingRate);
            yield return new ResultRate("Accurate", AccurateRate, new SolidColorBrush(Color.FromRgb(56, 217, 150)));
            yield return new ResultRate("Moving", moving, new SolidColorBrush(Color.FromRgb(255, 206, 69)));
            yield return new ResultRate("Inaccurate", inaccurateOnly, new SolidColorBrush(Color.FromRgb(255, 92, 122)));
        }
    }

    private sealed record ResultRate(string Label, double Rate, Brush Brush);

    private enum MousePathKind
    {
        NoTrace,
        SingleLine,
        MicroAdjust,
        Overflick,
        Messy
    }

    private readonly record struct TraceProfile(StrafeAttempt Attempt, MousePathKind Kind, double Efficiency, int DirectionChanges, double OvershootDegrees, double ClickFromCounterMs, double PathDegrees);
    private readonly record struct PlotPoint(double X, double Y);
    private readonly record struct SeriesPoint(int Index, double Value, Brush Brush);
    private readonly record struct OutlierScore(StrafeAttempt Attempt, double Score, string Reason);
}

internal static class CanvasElementExtensions
{
    public static T At<T>(this T element, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        return element;
    }
}
