using StrafeLab.Models;

namespace StrafeLab.Services;

public static class CoachingAnalyzer
{
    public static string ClassifyAimControl(StrafeAttempt attempt)
    {
        if (attempt.MouseTrace.Count < 3) return "no trace";
        double eff = attempt.PathEfficiency;
        double path = attempt.PathLengthDegrees;
        double disp = attempt.DisplacementDegrees;
        if (path < 0.03) return "steady";
        if (eff >= 0.82) return "single line";
        if (eff >= 0.55 && path <= Math.Max(0.18, disp * 2.1)) return "micro-adjust";
        if (eff < 0.35 && path > Math.Max(0.18, disp * 3.0)) return "messy";
        if (path > Math.Max(0.35, disp * 2.35)) return "overflick";
        return "corrected";
    }

    public static AttemptQualityBreakdown ScoreAttempt(StrafeAttempt attempt, AnalysisSettings settings, double sessionAvgDelay = 0)
    {
        var score = new AttemptQualityBreakdown();
        double delay = attempt.CounterDelayMs;
        double heTol = Math.Max(0, settings.KeyboardOverlapToleranceMs);
        double counterMin = settings.IdealCounterMinMs;
        double counterMax = settings.IdealCounterMaxMs;
        double clickMin = settings.IdealClickMinAfterCounterMs;
        double clickMax = settings.IdealClickMaxAfterCounterMs;

        if (delay >= counterMin && delay <= counterMax) score.Timing = 35;
        else if (delay < -heTol) score.Timing = Math.Max(0, 24 - (int)Math.Min(24, Math.Abs(delay) / 2.5));
        else if (delay < counterMin) score.Timing = 28;
        else score.Timing = Math.Max(0, 35 - (int)Math.Min(35, (delay - counterMax) / 2.0));

        if (!attempt.HasClick) score.Shot = 0;
        else if (attempt.IsMovingAtClick) score.Shot = 6;
        else if (attempt.ClickFromCounterMs is double click)
        {
            if (click >= clickMin && click <= clickMax) score.Shot = 30;
            else if (click < clickMin) score.Shot = 12;
            else score.Shot = Math.Max(8, 30 - (int)Math.Min(22, (click - clickMax) / 6.0));
        }
        else score.Shot = 10;

        string aim = ClassifyAimControl(attempt);
        score.Mouse = aim switch
        {
            "single line" => 20,
            "steady" => 18,
            "micro-adjust" => 17,
            "corrected" => 14,
            "overflick" => 9,
            "messy" => 5,
            _ => 10
        };

        double spreadFromSession = Math.Abs(delay - sessionAvgDelay);
        score.Consistency = spreadFromSession <= 15 ? 15 : spreadFromSession <= 30 ? 11 : spreadFromSession <= 55 ? 7 : 3;
        return score;
    }

    public static SessionCoachingInsight AnalyzeSession(IReadOnlyList<StrafeAttempt> attempts, AnalysisSettings settings, string focus = "Overall")
    {
        var included = attempts.Where(a => a.HasClick).ToList();
        var insight = new SessionCoachingInsight();
        if (included.Count == 0) return insight;

        double avgDelay = included.Average(a => a.CounterDelayMs);
        double stdev = Math.Sqrt(included.Select(a => Math.Pow(a.CounterDelayMs - avgDelay, 2)).Average());
        var scores = included.Select(a => ScoreAttempt(a, settings, avgDelay)).ToList();
        insight.AverageQuality = new AttemptQualityBreakdown
        {
            Timing = (int)Math.Round(scores.Average(s => s.Timing)),
            Shot = (int)Math.Round(scores.Average(s => s.Shot)),
            Mouse = (int)Math.Round(scores.Average(s => s.Mouse)),
            Consistency = (int)Math.Round(scores.Average(s => s.Consistency))
        };

        int moving = included.Count(a => a.IsMovingAtClick);
        int overlap = included.Count(a => a.Grade == TimingGrade.Overlap);
        int late = included.Count(a => a.Grade == TimingGrade.Late);
        int early = included.Count(a => a.Grade == TimingGrade.EarlyClick);
        var traced = included.Where(a => a.MouseTrace.Count > 2).ToList();
        int overflick = traced.Count(a => ClassifyAimControl(a) == "overflick");
        int messy = traced.Count(a => ClassifyAimControl(a) == "messy");

        var issueCandidates = new List<(string Name, int Count, string Fix)>
        {
            ("moving shots", moving, "Release A/D before M1. In replay, M1 should happen after the movement key warning disappears."),
            ("overlap", overlap, "Release the old key before pressing the counter key, or reduce rapid-trigger overlap."),
            ("late counter", late, "Press the opposite key sooner; make release and counter one rhythm."),
            ("early shot", early, "Delay the click until the counter input has settled."),
            ("overflick/messy mouse", overflick + messy, "Track a little longer before clicking; aim for a cleaner line or one small micro-correction.")
        };
        var main = issueCandidates.OrderByDescending(x => x.Count).First();
        double mainRate = main.Count * 100.0 / included.Count;
        insight.MainIssue = main.Count == 0
            ? "Main issue: none obvious. Work on tighter consistency and fewer outliers."
            : $"Main issue: {main.Name}. {main.Count}/{included.Count} attempts ({mainRate:0}%). {main.Fix}";

        insight.DirectionWeakness = BuildDirectionWeakness(included);
        insight.PracticePrescription = BuildPracticePrescription(main.Name, main.Count, included.Count);
        insight.ConsistencyLabel = stdev <= 18
            ? $"Consistency: stable. Counter timing spread is {stdev:0.0} ms."
            : stdev <= 35
                ? $"Consistency: decent but loose. Counter timing spread is {stdev:0.0} ms; review outliers."
                : $"Consistency: unstable. Counter timing spread is {stdev:0.0} ms; slow down reps until timing clusters.";

        var latest = included.OrderByDescending(a => a.Index).First();
        var latestScore = ScoreAttempt(latest, settings, avgDelay);
        insight.LatestAttemptLabel = $"Latest #{latest.Index}: {latest.ResultLabel}, mistakes: {latest.MistakeLabel}. Aim: {ClassifyAimControl(latest)}. Score {latestScore.Label}.";
        insight.PersonalBestLabel = BuildStreakLabel(included);
        insight.FocusFeedback = BuildFocusFeedback(focus, included, main.Name);
        return insight;
    }

    private static string BuildDirectionWeakness(IReadOnlyList<StrafeAttempt> attempts)
    {
        var ad = attempts.Where(a => a.FromKey == "A" && a.ToKey == "D").ToList();
        var da = attempts.Where(a => a.FromKey == "D" && a.ToKey == "A").ToList();
        if (ad.Count == 0 || da.Count == 0) return "Direction compare: record both A>D and D>A to find side bias.";
        double adMoving = Pct(ad.Count(a => a.IsMovingAtClick), ad.Count);
        double daMoving = Pct(da.Count(a => a.IsMovingAtClick), da.Count);
        double adAvg = ad.Average(a => a.CounterDelayMs);
        double daAvg = da.Average(a => a.CounterDelayMs);
        if (Math.Abs(adMoving - daMoving) >= 12)
            return adMoving > daMoving ? $"Direction weakness: A>D has more moving shots ({adMoving:0}% vs {daMoving:0}%)." : $"Direction weakness: D>A has more moving shots ({daMoving:0}% vs {adMoving:0}%).";
        if (Math.Abs(adAvg - daAvg) >= 15)
            return adAvg > daAvg ? $"Direction weakness: A>D is slower ({adAvg:0.0} ms vs {daAvg:0.0} ms)." : $"Direction weakness: D>A is slower ({daAvg:0.0} ms vs {adAvg:0.0} ms).";
        return $"Direction compare: balanced enough. A>D {adAvg:0.0} ms, D>A {daAvg:0.0} ms.";
    }

    private static string BuildPracticePrescription(string mainIssue, int count, int total)
    {
        if (count == 0) return "Next 20 reps: keep the same rhythm and try to keep every counter delay inside the target band.";
        return mainIssue switch
        {
            "moving shots" => "Next drill: 20 reps where M1 is only allowed after A/D is released. Ignore speed until moving shots are under 10%.",
            "overlap" => "Next drill: 20 slow reps watching the key replay. Goal: old key turns off before counter key turns on.",
            "late counter" => "Next drill: 20 rhythm reps. Say 'release-counter-click' and make release/counter one motion.",
            "early shot" => "Next drill: wait 80-120 ms after counter before M1 for 20 reps, then speed up without losing clean shots.",
            _ => "Next drill: 20 tracking reps. Match target speed longer, then use one small correction before M1."
        };
    }

    private static string BuildFocusFeedback(string focus, IReadOnlyList<StrafeAttempt> attempts, string mainIssue)
    {
        if (string.IsNullOrWhiteSpace(focus) || focus == "Overall") return string.Empty;
        int adCount = attempts.Count(a => a.FromKey == "A" && a.ToKey == "D");
        int daCount = attempts.Count(a => a.FromKey == "D" && a.ToKey == "A");
        double avgClick = attempts.Where(a => a.ClickFromCounterMs.HasValue).Select(a => a.ClickFromCounterMs!.Value).DefaultIfEmpty(0).Average();
        return focus switch
        {
            "Stop before shot" => $"Focus check: moving-shot rate {Pct(attempts.Count(a => a.IsMovingAtClick), attempts.Count):0}%.",
            "Fast clean shot" => $"Focus check: average click delay {avgClick:0.0} ms.",
            "A>D only" => $"Focus check: A>D attempts {adCount}/{attempts.Count}.",
            "D>A only" => $"Focus check: D>A attempts {daCount}/{attempts.Count}.",
            "Mouse control" => $"Focus check: main issue currently {mainIssue}.",
            _ => string.Empty
        };
    }

    private static string BuildStreakLabel(IReadOnlyList<StrafeAttempt> attempts)
    {
        int currentClean = 0;
        int bestClean = 0;
        foreach (var a in attempts.OrderBy(a => a.Index))
        {
            bool clean = a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick;
            currentClean = clean ? currentClean + 1 : 0;
            bestClean = Math.Max(bestClean, currentClean);
        }
        return $"Session streak: best clean streak {bestClean}. Current clean streak {currentClean}.";
    }

    private static double Pct(int n, int total) => total == 0 ? 0 : n * 100.0 / total;
}
