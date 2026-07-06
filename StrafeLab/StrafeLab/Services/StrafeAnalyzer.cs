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

    public int CurrentSessionSerial { get; private set; } = 1;

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
        CurrentSessionSerial = 1;
        _lastDisplayedMoveMs = double.NegativeInfinity;
    }

    public void BeginNewSession(bool preserveAttempts)
    {
        ResetHeldKeyState();
        _lastADownMs = double.NaN;
        _lastDDownMs = double.NaN;
        _lastDisplayedMoveMs = double.NegativeInfinity;

        if (!preserveAttempts)
        {
            Reset();
            return;
        }

        bool hasPriorSessionData = RecentAttempts.Count > 0 || _events.Count > 0;
        if (hasPriorSessionData)
        {
            MarkCurrentTopSessionBoundary();
            CurrentSessionSerial++;
        }
    }

    private void MarkCurrentTopSessionBoundary()
    {
        var latest = RecentAttempts
            .Where(a => a.HasClick && !a.StartsSessionBlock)
            .OrderByDescending(a => a.Index)
            .FirstOrDefault();
        if (latest == null) return;
        latest.StartsSessionBlock = true;
        latest.NotifyChanged();
    }

    public void LoadSessionData(IEnumerable<InputEventRecord> events, IEnumerable<StrafeAttempt> attempts)
    {
        Reset();

        CurrentSessionSerial = 1;

        foreach (var e in events.OrderBy(e => e.SessionTimeMs))
        {
            if (e.SessionSerial <= 0) e.SessionSerial = CurrentSessionSerial;
            _events.Add(e);
        }

        foreach (var e in _events.Where(e => e.Kind != InputKind.MouseMove).Reverse().Take(160).Reverse())
        {
            RecentEvents.Insert(0, e);
        }

        foreach (var attempt in attempts.OrderByDescending(a => a.Index))
        {
            if (attempt.SessionSerial <= 0) attempt.SessionSerial = CurrentSessionSerial;
            attempt.StartsSessionBlock = false;
            RecentAttempts.Add(attempt);
        }

        BackfillCounterKeyUpTimesFromEvents();
        BackfillClickUpTimesFromEvents();
        _attemptIndex = RecentAttempts.Select(a => a.Index).DefaultIfEmpty(0).Max();
        _lastDisplayedMoveMs = _events.LastOrDefault(e => e.Kind == InputKind.MouseMove)?.SessionTimeMs ?? double.NegativeInfinity;
    }

    private void BackfillCounterKeyUpTimesFromEvents()
    {
        foreach (var attempt in RecentAttempts)
        {
            if (attempt.CounterKeyUpTimeMs.HasValue) continue;
            var keyUp = _events
                .Where(e => e.SessionSerial == attempt.SessionSerial && e.Kind == InputKind.KeyUp && e.Code == attempt.ToKey && e.SessionTimeMs >= attempt.OppositeDownTimeMs)
                .OrderBy(e => e.SessionTimeMs)
                .FirstOrDefault();
            if (keyUp != null) attempt.CounterKeyUpTimeMs = keyUp.SessionTimeMs;
        }
    }


    private void BackfillClickUpTimesFromEvents()
    {
        foreach (var attempt in RecentAttempts)
        {
            if (!attempt.ClickTimeMs.HasValue || attempt.ClickUpTimeMs.HasValue) continue;
            var mouseUp = _events
                .Where(e => e.SessionSerial == attempt.SessionSerial && e.Kind == InputKind.MouseUp && e.Code == "M1" && e.SessionTimeMs >= attempt.ClickTimeMs.Value)
                .OrderBy(e => e.SessionTimeMs)
                .FirstOrDefault();
            if (mouseUp != null) attempt.ClickUpTimeMs = mouseUp.SessionTimeMs;
        }
    }

    public void ResetHeldKeyState()
    {
        _aDown = false;
        _dDown = false;
        _pendingRelease = null;
    }

    public void Add(InputEventRecord e)
    {
        e.SessionSerial = CurrentSessionSerial;
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
        else if (e.Kind == InputKind.MouseUp && e.Code == "M1")
        {
            AttachClickRelease(e.SessionTimeMs);
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
            RegisterCounterKeyUpForReplay(e.Code, e.SessionTimeMs);

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
            SessionSerial = CurrentSessionSerial,
            FromKey = fromKey,
            ToKey = toKey,
            ReleaseTimeMs = releaseMs,
            OppositeDownTimeMs = oppositeDownMs
        };
        GradeAttempt(attempt);
        RecentAttempts.Insert(0, attempt);
        while (RecentAttempts.Count > 250) RecentAttempts.RemoveAt(RecentAttempts.Count - 1);
    }

    private void RegisterCounterKeyUpForReplay(string key, double keyUpMs)
    {
        if (key != "A" && key != "D") return;

        var attempt = RecentAttempts.FirstOrDefault(a =>
            a.SessionSerial == CurrentSessionSerial &&
            a.ToKey == key &&
            !a.CounterKeyUpTimeMs.HasValue &&
            keyUpMs >= a.OppositeDownTimeMs &&
            keyUpMs - a.OppositeDownTimeMs <= Math.Max(250, _settings.MouseTraceMaxMs + 750));

        if (attempt == null) return;
        attempt.CounterKeyUpTimeMs = keyUpMs;
        RefreshAttempt(attempt);
    }

    private bool AttachMouseMovement(InputEventRecord e)
    {
        var attempt = RecentAttempts.FirstOrDefault(a =>
            a.SessionSerial == CurrentSessionSerial &&
            !a.ClickTimeMs.HasValue &&
            e.SessionTimeMs >= a.OppositeDownTimeMs &&
            e.SessionTimeMs - a.OppositeDownTimeMs <= Math.Max(60, _settings.MouseTraceMaxMs));

        if (attempt == null) return false;
        if (attempt.MouseTrace.Count >= Math.Max(8, _settings.MouseTraceMaxPoints)) return false;

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
        var attempt = RecentAttempts.FirstOrDefault(a => a.SessionSerial == CurrentSessionSerial && !a.ClickTimeMs.HasValue && clickMs >= a.ReleaseTimeMs - 50 && clickMs - a.ReleaseTimeMs <= 500);
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



    private void AttachClickRelease(double mouseUpMs)
    {
        var attempt = RecentAttempts.FirstOrDefault(a =>
            a.SessionSerial == CurrentSessionSerial &&
            a.ClickTimeMs.HasValue &&
            !a.ClickUpTimeMs.HasValue &&
            mouseUpMs >= a.ClickTimeMs.Value &&
            mouseUpMs - a.ClickTimeMs.Value <= 750);
        if (attempt == null) return;

        attempt.ClickUpTimeMs = mouseUpMs;
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
        double traceEnd = attempt.ClickTimeMs ?? attempt.OppositeDownTimeMs + Math.Max(60, _settings.MouseTraceMaxMs);

        bool IsAssociated(InputEventRecord e)
        {
            if (e.SessionSerial != attempt.SessionSerial) return false;
            if (e.Kind == InputKind.MouseMove && e.SessionTimeMs >= traceStart && e.SessionTimeMs <= traceEnd) return true;
            if (attempt.ClickTimeMs.HasValue && e.Kind == InputKind.MouseDown && e.Code == "M1" && Math.Abs(e.SessionTimeMs - attempt.ClickTimeMs.Value) <= 0.01) return true;
            if (attempt.ClickUpTimeMs.HasValue && e.Kind == InputKind.MouseUp && e.Code == "M1" && Math.Abs(e.SessionTimeMs - attempt.ClickUpTimeMs.Value) <= 0.01) return true;
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

        foreach (var attempt in RecentAttempts.Where(a => a.SessionSerial == CurrentSessionSerial).ToList())
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

    public void RegradeAllAttempts()
    {
        foreach (var attempt in RecentAttempts)
        {
            GradeAttempt(attempt);
            attempt.NotifyChanged();
        }
    }

    private void RefreshAttempt(StrafeAttempt attempt)
    {
        // The DataGrid used to be refreshed by removing/reinserting the same attempt.
        // That worked visually, but it also fired selection changes and cleared the
        // currently selected attempt after follow-up events such as counter-key-up or
        // M1-up. Notify the existing row instead so selection identity is preserved.
        if (!RecentAttempts.Contains(attempt)) return;
        attempt.NotifyChanged();
    }

    private void GradeAttempt(StrafeAttempt attempt)
    {
        double delay = attempt.CounterDelayMs;
        double overlapTolerance = Math.Max(0, _settings.KeyboardOverlapToleranceMs);
        string dir = $"{attempt.FromKey}->{attempt.ToKey}";

        if (!attempt.ClickTimeMs.HasValue)
        {
            attempt.Grade = TimingGrade.MissedClick;
            attempt.CleanTimingLabel = string.Empty;
            attempt.MovingShotTimingLabel = string.Empty;
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

        attempt.CleanTimingLabel = attempt.Grade == TimingGrade.Perfect && !attempt.IsMovingAtClick
            ? BuildCleanTimingLabel(attempt, delay, clickFromCounter)
            : string.Empty;
        attempt.MovingShotTimingLabel = attempt.IsMovingAtClick
            ? BuildMovingShotTimingLabel(attempt)
            : string.Empty;
        attempt.Diagnosis = BuildShortDiagnosis(attempt, dir, delay, clickFromCounter, overlapTolerance);
    }

    private string BuildCleanTimingLabel(StrafeAttempt attempt, double delay, double clickFromCounter)
    {
        double total = attempt.TotalTimeFromReleaseMs ?? delay + clickFromCounter;
        double fastMax = Math.Max(0, _settings.CleanFastMaxTotalMs);
        double perfectMax = Math.Max(fastMax, _settings.CleanPerfectMaxTotalMs);
        double justInTimeMin = Math.Max(perfectMax, _settings.CleanJustInTimeMinTotalMs);

        if (total <= fastMax) return "fast clean";
        if (total <= perfectMax) return "perfect clean";
        if (total >= justInTimeMin) return "just in time clean";
        return "controlled clean";
    }

    private string BuildMovingShotTimingLabel(StrafeAttempt attempt)
    {
        if (!attempt.ClickTimeMs.HasValue) return "clicked while moving";
        if (attempt.ClickTimeMs.Value < attempt.OppositeDownTimeMs) return "shot too soon";
        return "shot too late";
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
            parts.Add($"{attempt.MovingShotTimingLabel}: {held} held at click");
        }

        if (parts.Count == 0) return $"{dir}: {attempt.CleanTimingLabel}.";
        return $"{dir}: " + string.Join("; ", parts) + ".";
    }

    public IReadOnlyList<InputEventRecord> GetEventsForSession(int sessionSerial)
    {
        return _events.Where(e => e.SessionSerial == sessionSerial).ToList();
    }

    public IReadOnlyList<StrafeAttempt> GetAttemptsForSession(int sessionSerial)
    {
        return RecentAttempts.Where(a => a.SessionSerial == sessionSerial).ToList();
    }

    public SessionSummary BuildSummaryForSession(int sessionSerial, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        return BuildSummary(startedAt, endedAt, GetAttemptsForSession(sessionSerial), GetEventsForSession(sessionSerial));
    }

    public SessionSummary BuildSummary(DateTimeOffset startedAt, DateTimeOffset endedAt, IEnumerable<StrafeAttempt>? sourceAttempts = null, IEnumerable<InputEventRecord>? sourceEvents = null)
    {
        var rawAttempts = (sourceAttempts ?? RecentAttempts).Reverse().ToList();
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
            TotalEvents = (sourceEvents ?? _events).Count(),
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
        var latest = RecentAttempts.Where(IsAttemptIncludedInStats).Take(5).ToList();
        if (latest.Count < 5) return "Record at least 5 click-confirmed attempts for a live tip based on your recent pattern.";

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
                return $"Live tip: messy mouse path. Avg path {avgPath:0.00} deg for {avgDisplacement:0.00} deg displacement. Try one smooth correction before M1.";
        }

        double movingRate = latest.Count(a => a.IsMovingAtClick) * 100.0 / latest.Count;
        if (movingRate >= 25)
            return $"Live tip: moving shots. {movingRate:0}% of the last 5 attempts were clicked while A/D was still held. Release movement before M1.";
        if (overlapRate >= 35)
            return $"Live tip: key overlap. The opposite key often lands before the old key is released. Avg counter delay: {avg:0.0} ms.";
        if (lateRate >= 35)
            return $"Live tip: late counter/click timing. Avg counter delay: {avg:0.0} ms. Make release and counter one rhythm.";
        if (earlyClickRate >= 25)
            return "Live tip: early clicks. Delay M1 until after the counter input is settled.";
        return $"Live tip: timing looks stable. Avg counter delay: {avg:0.0} ms. Keep the next reps consistent.";
    }

    private sealed record PendingRelease(string FromKey, double ReleaseTimeMs);
}
