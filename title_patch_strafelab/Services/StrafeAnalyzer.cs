using System.Collections.ObjectModel;
using StrafeLab.Models;

namespace StrafeLab.Services;

public sealed class StrafeAnalyzer
{
    private readonly AnalysisSettings _settings;
    private readonly List<InputEventRecord> _events = new();
    private bool _aDown;
    private bool _dDown;
    private double _lastADownMs = double.NaN;
    private double _lastDDownMs = double.NaN;
    private PendingRelease? _pendingRelease;
    private int _attemptIndex;
    private double _lastDisplayedMoveMs = double.NegativeInfinity;

    public ObservableCollection<InputEventRecord> RecentEvents { get; } = new();
    public ObservableCollection<StrafeAttempt> RecentAttempts { get; } = new();
    public IReadOnlyList<InputEventRecord> Events => _events;
    public IReadOnlyList<StrafeAttempt> Attempts => RecentAttempts;

    public StrafeAnalyzer(AnalysisSettings settings)
    {
        _settings = settings;
    }

    public void Reset()
    {
        _events.Clear();
        RecentEvents.Clear();
        RecentAttempts.Clear();
        _aDown = false;
        _dDown = false;
        _lastADownMs = double.NaN;
        _lastDDownMs = double.NaN;
        _pendingRelease = null;
        _attemptIndex = 0;
        _lastDisplayedMoveMs = double.NegativeInfinity;
    }

    public void Add(InputEventRecord e)
    {
        // Raw Input sends mouse movement continuously while the session is live.
        // Only keep/count movement samples that belong to an active counter-strafe
        // attempt, otherwise an idle session can accumulate thousands of events.
        if (e.Kind == InputKind.MouseMove)
        {
            if (!AttachMouseMovement(e)) return;
        }
        else if (e.Kind is InputKind.KeyDown or InputKind.KeyUp)
        {
            HandleKey(e);
        }
        else if (e.Kind == InputKind.MouseDown && e.Code == "M1")
        {
            AttachClick(e.SessionTimeMs);
        }

        _events.Add(e);
        bool displayEvent = e.Kind != InputKind.MouseMove || e.SessionTimeMs - _lastDisplayedMoveMs >= 16;
        if (displayEvent)
        {
            if (e.Kind == InputKind.MouseMove) _lastDisplayedMoveMs = e.SessionTimeMs;
            RecentEvents.Insert(0, e);
            while (RecentEvents.Count > 160) RecentEvents.RemoveAt(RecentEvents.Count - 1);
        }
    }

    private void HandleKey(InputEventRecord e)
    {
        if (e.Code != "A" && e.Code != "D") return;
        string opposite = e.Code == "A" ? "D" : "A";

        if (e.Kind == InputKind.KeyDown)
        {
            if (e.Code == "A")
            {
                _aDown = true;
                _lastADownMs = e.SessionTimeMs;
            }
            else
            {
                _dDown = true;
                _lastDDownMs = e.SessionTimeMs;
            }

            if (_pendingRelease != null && _pendingRelease.FromKey == opposite)
            {
                double age = e.SessionTimeMs - _pendingRelease.ReleaseTimeMs;
                if (age >= 0 && age <= _settings.MaxAttemptPairWindowMs)
                {
                    AddAttempt(_pendingRelease.FromKey, e.Code, _pendingRelease.ReleaseTimeMs, e.SessionTimeMs);
                    _pendingRelease = null;
                }
            }
        }
        else if (e.Kind == InputKind.KeyUp)
        {
            if (e.Code == "A") _aDown = false;
            else _dDown = false;

            bool oppositeAlreadyDown = opposite == "A" ? _aDown : _dDown;
            double oppositeDownTime = opposite == "A" ? _lastADownMs : _lastDDownMs;

            if (oppositeAlreadyDown && !double.IsNaN(oppositeDownTime) && Math.Abs(e.SessionTimeMs - oppositeDownTime) <= _settings.MaxAttemptPairWindowMs)
            {
                AddAttempt(e.Code, opposite, e.SessionTimeMs, oppositeDownTime);
                _pendingRelease = null;
            }
            else
            {
                _pendingRelease = new PendingRelease(e.Code, e.SessionTimeMs);
            }
        }
    }

    private void AddAttempt(string fromKey, string toKey, double releaseMs, double oppositeDownMs)
    {
        var attempt = new StrafeAttempt
        {
            Index = ++_attemptIndex,
            FromKey = fromKey,
            ToKey = toKey,
            ReleaseTimeMs = releaseMs,
            OppositeDownTimeMs = oppositeDownMs
        };
        GradeAttempt(attempt);
        RecentAttempts.Insert(0, attempt);
        while (RecentAttempts.Count > 250) RecentAttempts.RemoveAt(RecentAttempts.Count - 1);
    }

    private bool AttachMouseMovement(InputEventRecord e)
    {
        var attempt = RecentAttempts.FirstOrDefault(a =>
            !a.ClickTimeMs.HasValue &&
            e.SessionTimeMs >= a.OppositeDownTimeMs &&
            e.SessionTimeMs - a.OppositeDownTimeMs <= 750);

        if (attempt == null) return false;

        int x = e.DeltaX;
        int y = e.DeltaY;
        if (attempt.MouseTrace.Count > 0)
        {
            x += attempt.MouseTrace[^1].XCounts;
            y += attempt.MouseTrace[^1].YCounts;
        }

        attempt.MouseTrace.Add(new MouseTracePoint
        {
            SessionTimeMs = e.SessionTimeMs,
            TimeFromCounterMs = e.SessionTimeMs - attempt.OppositeDownTimeMs,
            DeltaX = e.DeltaX,
            DeltaY = e.DeltaY,
            XCounts = x,
            YCounts = y,
            XDegrees = x * _settings.Calibration.HorizontalDegreesPerCount,
            YDegrees = y * _settings.Calibration.VerticalDegreesPerCount
        });

        return true;
    }

    private void AttachClick(double clickMs)
    {
        var attempt = RecentAttempts.FirstOrDefault(a => !a.ClickTimeMs.HasValue && clickMs >= a.ReleaseTimeMs - 50 && clickMs - a.ReleaseTimeMs <= 500);
        if (attempt == null) return;

        attempt.ClickTimeMs = clickMs;
        var held = new List<string>();
        if (_aDown) held.Add("A");
        if (_dDown) held.Add("D");
        attempt.HeldKeysAtClick = string.Join("+", held);
        attempt.IsMovingAtClick = held.Count > 0;

        GradeAttempt(attempt);
        RefreshAttempt(attempt);
    }


    public StrafeAttempt? RemoveLatestAttempt()
    {
        var attempt = RecentAttempts.FirstOrDefault(a => a.HasClick);
        if (attempt == null) return null;

        RemoveAttempts([attempt]);
        return attempt;
    }

    public int RemoveAttempts(IEnumerable<StrafeAttempt> attempts)
    {
        int removed = 0;
        foreach (var attempt in attempts.Distinct().ToList())
        {
            if (!RecentAttempts.Contains(attempt)) continue;
            RecentAttempts.Remove(attempt);
            RemoveAssociatedEvents(attempt);
            removed++;
        }
        return removed;
    }

    private void RemoveAssociatedEvents(StrafeAttempt attempt)
    {
        double traceStart = attempt.OppositeDownTimeMs;
        double traceEnd = attempt.ClickTimeMs ?? attempt.OppositeDownTimeMs + 750;

        bool IsAssociated(InputEventRecord e)
        {
            if (e.Kind == InputKind.MouseMove && e.SessionTimeMs >= traceStart && e.SessionTimeMs <= traceEnd) return true;
            if (attempt.ClickTimeMs.HasValue && e.Kind == InputKind.MouseDown && e.Code == "M1" && Math.Abs(e.SessionTimeMs - attempt.ClickTimeMs.Value) <= 0.01) return true;
            return false;
        }

        _events.RemoveAll(IsAssociated);

        for (int i = RecentEvents.Count - 1; i >= 0; i--)
        {
            if (IsAssociated(RecentEvents[i])) RecentEvents.RemoveAt(i);
        }
    }

    public void ApplyAutoJiggleTags(double currentSessionMs)
    {
        if (!_settings.AutoTagJiggles) return;

        foreach (var attempt in RecentAttempts.ToList())
        {
            if (attempt.IsJiggle || attempt.ClickTimeMs.HasValue) continue;
            if (currentSessionMs - attempt.OppositeDownTimeMs < _settings.JiggleAutoTagAfterMs) continue;

            bool tinyMouseMovement = attempt.MouseTrace.Count == 0 || attempt.PathLengthDegrees <= _settings.JiggleMaxMousePathDegrees;
            bool clicklessNoShot = !attempt.ClickTimeMs.HasValue;
            if (clicklessNoShot && tinyMouseMovement)
            {
                attempt.IsJiggle = true;
                attempt.IsAutoTaggedJiggle = true;
                RefreshAttempt(attempt);
            }
        }
    }

    public bool IsAttemptIncludedInStats(StrafeAttempt attempt)
    {
        if (!attempt.HasClick) return false;
        if (attempt.IsExcluded) return false;
        if (_settings.ExcludeJigglesFromStats && attempt.IsJiggle) return false;
        return true;
    }

    private void RefreshAttempt(StrafeAttempt attempt)
    {
        int idx = RecentAttempts.IndexOf(attempt);
        if (idx < 0) return;
        RecentAttempts.RemoveAt(idx);
        RecentAttempts.Insert(idx, attempt);
    }

    private void GradeAttempt(StrafeAttempt attempt)
    {
        double delay = attempt.CounterDelayMs;
        double overlapTolerance = Math.Max(0, _settings.KeyboardOverlapToleranceMs);
        string dir = $"{attempt.FromKey}->{attempt.ToKey}";

        if (!attempt.ClickTimeMs.HasValue)
        {
            attempt.Grade = TimingGrade.MissedClick;
            attempt.Diagnosis = "No shot; hidden from reports.";
            return;
        }

        double clickFromCounter = attempt.ClickFromCounterMs ?? 0;

        if (clickFromCounter < _settings.IdealClickMinAfterCounterMs)
        {
            attempt.Grade = TimingGrade.EarlyClick;
        }
        else if (delay < _settings.IdealCounterMinMs - overlapTolerance)
        {
            attempt.Grade = TimingGrade.Overlap;
        }
        else if (delay > _settings.IdealCounterMaxMs || clickFromCounter > _settings.IdealClickMaxAfterCounterMs)
        {
            attempt.Grade = TimingGrade.Late;
        }
        else
        {
            attempt.Grade = TimingGrade.Perfect;
        }

        attempt.Diagnosis = BuildShortDiagnosis(attempt, dir, delay, clickFromCounter, overlapTolerance);
    }

    private string BuildShortDiagnosis(StrafeAttempt attempt, string dir, double delay, double clickFromCounter, double overlapTolerance)
    {
        var parts = new List<string>();

        if (delay < _settings.IdealCounterMinMs - overlapTolerance)
        {
            parts.Add($"overlap: {attempt.ToKey} down before {attempt.FromKey} up");
        }
        else if (delay < _settings.IdealCounterMinMs)
        {
            parts.Add($"tiny overlap inside HE tolerance");
        }
        else if (delay > _settings.IdealCounterMaxMs)
        {
            parts.Add($"late: {delay:0} ms gap before {attempt.ToKey}");
        }

        if (clickFromCounter < _settings.IdealClickMinAfterCounterMs)
        {
            parts.Add($"shot too early");
        }
        else if (clickFromCounter > _settings.IdealClickMaxAfterCounterMs)
        {
            parts.Add($"slow shot: {clickFromCounter:0} ms after counter");
        }

        if (attempt.IsMovingAtClick)
        {
            string held = string.IsNullOrWhiteSpace(attempt.HeldKeysAtClick) ? "movement key" : attempt.HeldKeysAtClick;
            parts.Add($"moving at shot: {held} held");
        }

        if (parts.Count == 0) return $"{dir}: clean.";
        return $"{dir}: " + string.Join("; ", parts) + ".";
    }

    public SessionSummary BuildSummary(DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var rawAttempts = RecentAttempts.Reverse().ToList();
        var attempts = rawAttempts.Where(IsAttemptIncludedInStats).ToList();
        double[] delays = attempts.Select(a => a.CounterDelayMs).ToArray();
        double[] clickDeltas = attempts.Where(a => a.ClickFromCounterMs.HasValue).Select(a => a.ClickFromCounterMs!.Value).ToArray();
        var traced = attempts.Where(a => a.MouseTrace.Count > 0).ToList();
        double avgDelay = delays.Length == 0 ? 0 : delays.Average();
        double std = delays.Length == 0 ? 0 : Math.Sqrt(delays.Select(x => Math.Pow(x - avgDelay, 2)).Average());
        var coaching = CoachingAnalyzer.AnalyzeSession(attempts, _settings);
        var qualityScores = attempts.Select(a => CoachingAnalyzer.ScoreAttempt(a, _settings, avgDelay)).ToList();

        return new SessionSummary
        {
            SessionId = startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss"),
            StartedAt = startedAt,
            EndedAt = endedAt,
            TotalEvents = _events.Count,
            Attempts = attempts.Count,
            RawAttempts = rawAttempts.Count,
            FilteredAttempts = rawAttempts.Count - attempts.Count,
            JiggleAttempts = rawAttempts.Count(a => a.IsJiggle),
            NoClickAttempts = rawAttempts.Count(a => !a.HasClick),
            Perfect = attempts.Count(a => a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick),
            Overlap = attempts.Count(a => a.Grade == TimingGrade.Overlap),
            Late = attempts.Count(a => a.Grade == TimingGrade.Late),
            EarlyClick = attempts.Count(a => a.Grade == TimingGrade.EarlyClick),
            MissedClick = attempts.Count(a => a.Grade == TimingGrade.MissedClick),
            MovingAtClick = attempts.Count(a => a.IsMovingAtClick),
            AverageCounterDelayMs = avgDelay,
            StdDevCounterDelayMs = std,
            AverageClickFromCounterMs = clickDeltas.Length == 0 ? 0 : clickDeltas.Average(),
            AttemptsWithMouseTrace = traced.Count,
            AverageMousePathDegrees = traced.Count == 0 ? 0 : traced.Average(a => a.PathLengthDegrees),
            AverageMouseDisplacementDegrees = traced.Count == 0 ? 0 : traced.Average(a => a.DisplacementDegrees),
            AverageMousePathEfficiency = traced.Count == 0 ? 0 : traced.Average(a => a.PathEfficiency),
            AverageQualityScore = qualityScores.Count == 0 ? 0 : qualityScores.Average(s => s.Total),
            AverageTimingScore = qualityScores.Count == 0 ? 0 : qualityScores.Average(s => s.Timing),
            AverageShotScore = qualityScores.Count == 0 ? 0 : qualityScores.Average(s => s.Shot),
            AverageMouseScore = qualityScores.Count == 0 ? 0 : qualityScores.Average(s => s.Mouse),
            AverageConsistencyScore = qualityScores.Count == 0 ? 0 : qualityScores.Average(s => s.Consistency),
            MainIssue = coaching.MainIssue,
            PracticePrescription = coaching.PracticePrescription,
            Calibration = _settings.Calibration.Clone()
        };
    }

    public string GetCoachingTip()
    {
        var latest = RecentAttempts.Where(IsAttemptIncludedInStats).Take(40).ToList();
        if (latest.Count < 5) return "Start a session, strafe A/D, counter-strafe, and click. Tips use click-confirmed attempts when no-click filtering is enabled.";

        double overlapRate = latest.Count(a => a.Grade == TimingGrade.Overlap) * 100.0 / latest.Count;
        double lateRate = latest.Count(a => a.Grade == TimingGrade.Late) * 100.0 / latest.Count;
        double earlyClickRate = latest.Count(a => a.Grade == TimingGrade.EarlyClick) * 100.0 / latest.Count;
        double avg = latest.Average(a => a.CounterDelayMs);
        var traced = latest.Where(a => a.MouseTrace.Count > 2).ToList();

        if (traced.Count >= 5)
        {
            double avgEfficiency = traced.Average(a => a.PathEfficiency);
            double avgPath = traced.Average(a => a.PathLengthDegrees);
            double avgDisplacement = traced.Average(a => a.DisplacementDegrees);
            if (avgEfficiency > 0 && avgEfficiency < 0.45)
                return $"Mouse issue: pre-click path is inefficient. Avg path {avgPath:0.00} deg for {avgDisplacement:0.00} deg displacement. Try a cleaner line and slower final correction.";
        }

        double movingRate = latest.Count(a => a.IsMovingAtClick) * 100.0 / latest.Count;
        if (movingRate >= 25)
            return $"Main issue: moving shots. {movingRate:0}% of attempts were clicked while A/D was still held. Tap and release the counter key before shooting.";
        if (overlapRate >= 35)
            return $"Main issue: overlap. The opposite key often lands before the old key is released. Avg counter delay: {avg:0.0} ms.";
        if (lateRate >= 35)
            return $"Main issue: slow counter timing. Avg counter delay: {avg:0.0} ms. Press the opposite key sooner after release.";
        if (earlyClickRate >= 25)
            return "Main issue: early shots. Delay the click until after the counter input is settled.";
        return $"Timing looks stable. Avg counter delay: {avg:0.0} ms. Focus on consistency and keeping outliers low.";
    }

    private sealed record PendingRelease(string FromKey, double ReleaseTimeMs);
}
