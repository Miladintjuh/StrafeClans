using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StrafeLab.Models;

namespace StrafeLab.Services;

public sealed class SessionStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public SessionStore()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrafeLab", "sessions");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task SaveAsync(SessionSummary summary, IReadOnlyList<InputEventRecord> events, IReadOnlyList<StrafeAttempt> attempts)
    {
        string folder = Path.Combine(_root, summary.SessionId);
        Directory.CreateDirectory(folder);

        var chronologicalAttempts = attempts.Reverse().Where(a => a.HasClick).ToList();
        await File.WriteAllTextAsync(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(summary, _json));
        await File.WriteAllTextAsync(Path.Combine(folder, "events.csv"), EventsCsv(events));
        await File.WriteAllTextAsync(Path.Combine(folder, "attempts.csv"), AttemptsCsv(chronologicalAttempts));
        await File.WriteAllTextAsync(Path.Combine(folder, "mouse_traces.csv"), MouseTraceCsv(chronologicalAttempts));
    }

    public IReadOnlyList<SessionSummary> LoadSummaries()
    {
        var result = new List<SessionSummary>();
        if (!Directory.Exists(_root)) return result;

        foreach (var file in Directory.EnumerateFiles(_root, "summary.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = File.ReadAllText(file);
                var summary = JsonSerializer.Deserialize<SessionSummary>(json);
                if (summary != null) result.Add(summary);
            }
            catch
            {
                // Ignore malformed files so one bad session does not break the dashboard.
            }
        }

        return result.OrderByDescending(s => s.StartedAt).ToList();
    }

    public SessionSummary Aggregate(TimeSpan window)
    {
        var cutoff = DateTimeOffset.Now - window;
        var sessions = LoadSummaries().Where(s => s.StartedAt >= cutoff).ToList();
        if (sessions.Count == 0) return new SessionSummary { SessionId = $"Last {window.TotalDays:0} days" };

        int attempts = sessions.Sum(s => s.Attempts);
        int rawAttempts = sessions.Sum(s => s.RawAttempts > 0 ? s.RawAttempts : s.Attempts);
        int tracedAttempts = sessions.Sum(s => s.AttemptsWithMouseTrace);
        return new SessionSummary
        {
            SessionId = $"Last {window.TotalDays:0} days",
            StartedAt = sessions.Min(s => s.StartedAt),
            EndedAt = sessions.Max(s => s.EndedAt),
            TotalEvents = sessions.Sum(s => s.TotalEvents),
            Attempts = attempts,
            RawAttempts = rawAttempts,
            FilteredAttempts = sessions.Sum(s => s.FilteredAttempts),
            JiggleAttempts = sessions.Sum(s => s.JiggleAttempts),
            NoClickAttempts = sessions.Sum(s => s.NoClickAttempts),
            Perfect = sessions.Sum(s => s.Perfect),
            Overlap = sessions.Sum(s => s.Overlap),
            Late = sessions.Sum(s => s.Late),
            EarlyClick = sessions.Sum(s => s.EarlyClick),
            MissedClick = sessions.Sum(s => s.MissedClick),
            MovingAtClick = sessions.Sum(s => s.MovingAtClick),
            AverageCounterDelayMs = WeightedAverage(sessions, s => s.AverageCounterDelayMs, s => s.Attempts),
            StdDevCounterDelayMs = WeightedAverage(sessions, s => s.StdDevCounterDelayMs, s => s.Attempts),
            AverageClickFromCounterMs = WeightedAverage(sessions, s => s.AverageClickFromCounterMs, s => s.Attempts),
            AttemptsWithMouseTrace = tracedAttempts,
            AverageMousePathDegrees = WeightedAverage(sessions, s => s.AverageMousePathDegrees, s => s.AttemptsWithMouseTrace),
            AverageMouseDisplacementDegrees = WeightedAverage(sessions, s => s.AverageMouseDisplacementDegrees, s => s.AttemptsWithMouseTrace),
            AverageMousePathEfficiency = WeightedAverage(sessions, s => s.AverageMousePathEfficiency, s => s.AttemptsWithMouseTrace),
            AverageQualityScore = WeightedAverage(sessions, s => s.AverageQualityScore, s => s.Attempts),
            AverageTimingScore = WeightedAverage(sessions, s => s.AverageTimingScore, s => s.Attempts),
            AverageShotScore = WeightedAverage(sessions, s => s.AverageShotScore, s => s.Attempts),
            AverageMouseScore = WeightedAverage(sessions, s => s.AverageMouseScore, s => s.Attempts),
            AverageConsistencyScore = WeightedAverage(sessions, s => s.AverageConsistencyScore, s => s.Attempts),
            BestCleanStreak = sessions.Max(s => s.BestCleanStreak)
        };
    }

    private static double WeightedAverage(IEnumerable<SessionSummary> sessions, Func<SessionSummary, double> value, Func<SessionSummary, int> weight)
    {
        double totalWeight = sessions.Sum(s => Math.Max(0, weight(s)));
        if (totalWeight <= 0) return 0;
        return sessions.Sum(s => value(s) * Math.Max(0, weight(s))) / totalWeight;
    }

    private static string EventsCsv(IReadOnlyList<InputEventRecord> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("wall_time,session_ms,code,kind,delta_x,delta_y");
        foreach (var e in events)
        {
            sb.AppendLine(string.Join(',',
                Csv(e.WallTime.ToString("O", CultureInfo.InvariantCulture)),
                e.SessionTimeMs.ToString("0.###", CultureInfo.InvariantCulture),
                Csv(e.Code),
                Csv(e.Kind.ToString()),
                e.DeltaX.ToString(CultureInfo.InvariantCulture),
                e.DeltaY.ToString(CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }

    private static string AttemptsCsv(IReadOnlyList<StrafeAttempt> attempts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("index,direction,manual_excluded,is_jiggle,auto_tagged_jiggle,has_click,filter_label,release_ms,opposite_down_ms,counter_delay_ms,click_ms,click_from_release_ms,click_from_counter_ms,grade,mistakes,result,diagnosis,moving_at_click,held_keys_at_click,aim_control,quality_total,quality_timing,quality_shot,quality_mouse,quality_consistency,trace_points,raw_end_x,raw_end_y,horizontal_degrees,vertical_degrees,path_degrees,displacement_degrees,path_efficiency");
        foreach (var a in attempts)
        {
            var q = CoachingAnalyzer.ScoreAttempt(a, new AnalysisSettings());
            sb.AppendLine(string.Join(',',
                a.Index.ToString(CultureInfo.InvariantCulture),
                Csv(a.Direction),
                a.IsExcluded ? "1" : "0",
                a.IsJiggle ? "1" : "0",
                a.IsAutoTaggedJiggle ? "1" : "0",
                a.HasClick ? "1" : "0",
                Csv(a.FilterLabel),
                a.ReleaseTimeMs.ToString("0.###", CultureInfo.InvariantCulture),
                a.OppositeDownTimeMs.ToString("0.###", CultureInfo.InvariantCulture),
                a.CounterDelayMs.ToString("0.###", CultureInfo.InvariantCulture),
                Nullable(a.ClickTimeMs),
                Nullable(a.ClickFromReleaseMs),
                Nullable(a.ClickFromCounterMs),
                Csv(a.Grade.ToString()),
                Csv(a.MistakeLabel),
                Csv(a.ResultLabel),
                Csv(a.Diagnosis),
                a.IsMovingAtClick ? "1" : "0",
                Csv(a.HeldKeysAtClick),
                Csv(a.AimControlLabel),
                q.Total.ToString(CultureInfo.InvariantCulture),
                q.Timing.ToString(CultureInfo.InvariantCulture),
                q.Shot.ToString(CultureInfo.InvariantCulture),
                q.Mouse.ToString(CultureInfo.InvariantCulture),
                q.Consistency.ToString(CultureInfo.InvariantCulture),
                a.TracePoints.ToString(CultureInfo.InvariantCulture),
                a.RawEndX.ToString(CultureInfo.InvariantCulture),
                a.RawEndY.ToString(CultureInfo.InvariantCulture),
                a.HorizontalDegrees.ToString("0.######", CultureInfo.InvariantCulture),
                a.VerticalDegrees.ToString("0.######", CultureInfo.InvariantCulture),
                a.PathLengthDegrees.ToString("0.######", CultureInfo.InvariantCulture),
                a.DisplacementDegrees.ToString("0.######", CultureInfo.InvariantCulture),
                a.PathEfficiency.ToString("0.######", CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }

    private static string MouseTraceCsv(IReadOnlyList<StrafeAttempt> attempts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("attempt_index,point_index,session_ms,time_from_counter_ms,delta_x,delta_y,x_counts,y_counts,x_degrees,y_degrees");
        foreach (var a in attempts)
        {
            for (int i = 0; i < a.MouseTrace.Count; i++)
            {
                var p = a.MouseTrace[i];
                sb.AppendLine(string.Join(',',
                    a.Index.ToString(CultureInfo.InvariantCulture),
                    i.ToString(CultureInfo.InvariantCulture),
                    p.SessionTimeMs.ToString("0.###", CultureInfo.InvariantCulture),
                    p.TimeFromCounterMs.ToString("0.###", CultureInfo.InvariantCulture),
                    p.DeltaX.ToString(CultureInfo.InvariantCulture),
                    p.DeltaY.ToString(CultureInfo.InvariantCulture),
                    p.XCounts.ToString(CultureInfo.InvariantCulture),
                    p.YCounts.ToString(CultureInfo.InvariantCulture),
                    p.XDegrees.ToString("0.######", CultureInfo.InvariantCulture),
                    p.YDegrees.ToString("0.######", CultureInfo.InvariantCulture)));
            }
        }
        return sb.ToString();
    }

    private static string Nullable(double? value) => value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    private static string Csv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
