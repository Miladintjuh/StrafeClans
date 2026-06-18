using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StrafeLab.Models;
using StrafeLab.Services;

namespace StrafeLab;

public partial class ReportsView : UserControl
{
    private readonly Func<IReadOnlyList<StrafeAttempt>> _attemptProvider;
    private readonly AnalysisSettings _settings;
    private readonly List<ReportBlock> _library = new();
    private readonly List<ReportBlock> _reportBlocks = new();

    public event Action? BackRequested;

    public ReportsView(Func<IReadOnlyList<StrafeAttempt>> attemptProvider, AnalysisSettings settings)
    {
        InitializeComponent();
        _attemptProvider = attemptProvider;
        _settings = settings;
        Loaded += (_, _) => InitializeBlocks();
    }

    private void InitializeBlocks()
    {
        if (_library.Count == 0)
        {
            _library.AddRange([
                new ReportBlock("summary", "Session summary", "Attempts, clean rate, moving rate, and timing averages."),
                new ReportBlock("direction", "Left/right comparison", "Compare A>D and D>A timing, mistakes, and moving shots."),
                new ReportBlock("mistakes", "Mistake breakdown", "Overlap, slow, early shot, and moving categories."),
                new ReportBlock("click", "Click timing", "Shot delay after the counter key and one-tap timing quality."),
                new ReportBlock("moving", "Moving shots", "Shots where a movement key was still held at M1."),
                new ReportBlock("mouse", "Mouse trace quality", "Path efficiency, overflick tendency, and trace availability."),
                new ReportBlock("quality", "Rep quality score", "Total score with max category points for timing, shot discipline, mouse control, and consistency."),
                new ReportBlock("coaching", "Coaching notes", "Prioritized practice actions from the report data.")
            ]);
            _reportBlocks.AddRange(_library.Take(4));
        }

        RefreshLists();
        GeneratePreview();
    }

    private void RefreshLists()
    {
        LibraryList.ItemsSource = null;
        LibraryList.ItemsSource = _library;
        ReportList.ItemsSource = null;
        ReportList.ItemsSource = _reportBlocks;
    }

    private void LibraryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (LibraryList.SelectedItem is ReportBlock block)
        {
            DragDrop.DoDragDrop(LibraryList, block, DragDropEffects.Copy);
        }
    }

    private void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryList.SelectedItem is ReportBlock block) AddBlock(block);
    }

    private void ReportList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ReportBlock)) is ReportBlock block) AddBlock(block);
    }

    private void AddBlock(ReportBlock block)
    {
        _reportBlocks.Add(block with { });
        RefreshLists();
        GeneratePreview();
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        int index = ReportList.SelectedIndex;
        if (index <= 0) return;
        (_reportBlocks[index - 1], _reportBlocks[index]) = (_reportBlocks[index], _reportBlocks[index - 1]);
        RefreshLists();
        ReportList.SelectedIndex = index - 1;
    }

    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        int index = ReportList.SelectedIndex;
        if (index < 0 || index >= _reportBlocks.Count - 1) return;
        (_reportBlocks[index + 1], _reportBlocks[index]) = (_reportBlocks[index], _reportBlocks[index + 1]);
        RefreshLists();
        ReportList.SelectedIndex = index + 1;
    }

    private void RemoveBlockButton_Click(object sender, RoutedEventArgs e)
    {
        int index = ReportList.SelectedIndex;
        if (index < 0) return;
        _reportBlocks.RemoveAt(index);
        RefreshLists();
        GeneratePreview();
    }

    private void ClearBlocksButton_Click(object sender, RoutedEventArgs e)
    {
        _reportBlocks.Clear();
        RefreshLists();
        GeneratePreview();
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e) => GeneratePreview();

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(PreviewBox.Text)) Clipboard.SetText(PreviewBox.Text);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void GeneratePreview()
    {
        var attempts = _attemptProvider().Where(a => a.HasClick).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("StrafeLab report");
        sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Included attempts: {attempts.Count}");
        sb.AppendLine(new string('-', 60));

        if (_reportBlocks.Count == 0)
        {
            sb.AppendLine("Drag report blocks into the layout to build a report.");
        }
        else
        {
            foreach (var block in _reportBlocks)
            {
                sb.AppendLine();
                sb.AppendLine(block.Title.ToUpperInvariant());
                sb.AppendLine(BuildBlock(block.Id, attempts));
            }
        }

        PreviewBox.Text = sb.ToString().TrimEnd();
    }

    private string BuildBlock(string id, IReadOnlyList<StrafeAttempt> attempts)
    {
        int total = attempts.Count;
        if (total == 0) return "No click-confirmed attempts available.";

        return id switch
        {
            "summary" => $"Clean: {Rate(attempts.Count(IsClean), total):0}%\nMoving: {Rate(attempts.Count(a => a.IsMovingAtClick), total):0}%\nAverage counter delay: {Average(attempts.Select(a => a.CounterDelayMs)):+0.0;-0.0;0.0} ms\nAverage click delay: {Average(attempts.Where(a => a.ClickFromCounterMs.HasValue).Select(a => a.ClickFromCounterMs!.Value)):+0.0;-0.0;0.0} ms",
            "direction" => DirectionBlock(attempts),
            "mistakes" => $"Overlap: {CountPct(attempts, a => a.Grade == TimingGrade.Overlap)}\nSlow: {CountPct(attempts, a => a.Grade == TimingGrade.Late)}\nEarly shot: {CountPct(attempts, a => a.Grade == TimingGrade.EarlyClick)}\nMoving: {CountPct(attempts, a => a.IsMovingAtClick)}",
            "click" => $"Target click window: {_settings.IdealClickMinAfterCounterMs:0}-{_settings.IdealClickMaxAfterCounterMs:0} ms after counter.\nAvg click delay: {Average(attempts.Where(a => a.ClickFromCounterMs.HasValue).Select(a => a.ClickFromCounterMs!.Value)):0.0} ms.\nEarly shots: {CountPct(attempts, a => a.Grade == TimingGrade.EarlyClick)}.",
            "moving" => MovingBlock(attempts),
            "mouse" => MouseBlock(attempts),
            "quality" => QualityBlock(attempts),
            "coaching" => CoachingBlock(attempts),
            _ => "Unknown block."
        };
    }

    private string DirectionBlock(IReadOnlyList<StrafeAttempt> attempts)
    {
        var ad = attempts.Where(a => a.FromKey == "A" && a.ToKey == "D").ToList();
        var da = attempts.Where(a => a.FromKey == "D" && a.ToKey == "A").ToList();
        return $"A>D: {ad.Count} attempts, avg counter {Average(ad.Select(a => a.CounterDelayMs)):+0.0;-0.0;0.0} ms, moving {CountPct(ad, a => a.IsMovingAtClick)}.\nD>A: {da.Count} attempts, avg counter {Average(da.Select(a => a.CounterDelayMs)):+0.0;-0.0;0.0} ms, moving {CountPct(da, a => a.IsMovingAtClick)}.";
    }

    private static string MovingBlock(IReadOnlyList<StrafeAttempt> attempts)
    {
        var moving = attempts.Where(a => a.IsMovingAtClick).ToList();
        var keys = moving.GroupBy(a => string.IsNullOrWhiteSpace(a.HeldKeysAtClick) ? "A/D" : a.HeldKeysAtClick)
            .Select(g => $"{g.Key}: {g.Count()}");
        return $"Moving shots: {CountPct(attempts, a => a.IsMovingAtClick)}.\nHeld keys at shot: {string.Join(", ", keys.DefaultIfEmpty("none"))}.";
    }

    private static string MouseBlock(IReadOnlyList<StrafeAttempt> attempts)
    {
        var traced = attempts.Where(a => a.MouseTrace.Count > 1).ToList();
        if (traced.Count == 0) return "No usable mouse traces in the selected data.";
        return $"Traced attempts: {traced.Count}/{attempts.Count}.\nAvg path efficiency: {Average(traced.Select(a => a.PathEfficiency)):0.00}.\nAvg path length: {Average(traced.Select(a => a.PathLengthDegrees)):0.00} deg.";
    }

    private string QualityBlock(IReadOnlyList<StrafeAttempt> attempts)
    {
        double avgDelay = Average(attempts.Select(a => a.CounterDelayMs));
        var scores = attempts.Select(a => CoachingAnalyzer.ScoreAttempt(a, _settings, avgDelay)).ToList();
        if (scores.Count == 0) return "No scored attempts.";
        return $"Average quality: {scores.Average(s => s.Total):0}/100.\n" +
               $"Timing: {scores.Average(s => s.Timing):0}/35.\n" +
               $"Shot discipline: {scores.Average(s => s.Shot):0}/30.\n" +
               $"Mouse control: {scores.Average(s => s.Mouse):0}/20.\n" +
               $"Consistency: {scores.Average(s => s.Consistency):0}/15.\n" +
               "Use the lowest category as the next training focus.";
    }

    private string CoachingBlock(IReadOnlyList<StrafeAttempt> attempts)
    {
        var insight = CoachingAnalyzer.AnalyzeSession(attempts, _settings);
        return insight.MainIssue + "\n" + insight.DirectionWeakness + "\n" + insight.PracticePrescription;
    }

    private static string CountPct(IReadOnlyList<StrafeAttempt> attempts, Func<StrafeAttempt, bool> predicate)
    {
        int count = attempts.Count(predicate);
        return $"{count}/{attempts.Count} ({Rate(count, attempts.Count):0}%)";
    }

    private static bool IsClean(StrafeAttempt a) => a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick;
    private static double Average(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }
    private static double Rate(int count, int total) => total == 0 ? 0 : count * 100.0 / total;

    private sealed record ReportBlock(string Id, string Title, string Description)
    {
        public override string ToString() => $"{Title}\n{Description}";
    }
}
