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
        _events.Add(e);
        bool displayEvent = e.Kind != InputKind.MouseMove || e.SessionTimeMs - _lastDisplayedMoveMs >= 16;
        if (displayEvent)
        {
            if (e.Kind == InputKind.MouseMove) _lastDisplayedMoveMs = e.SessionTimeMs;
            RecentEvents.Insert(0, e);
            while (RecentEvents.Count > 160) RecentEvents.RemoveAt(RecentEvents.Count - 1);
        }

        if (e.Kind is InputKind.KeyDown or InputKind.KeyUp)
        {
            HandleKey(e);
        }
        else if (e.Kind == InputKind.MouseMove)
        {
            AttachMouseMovement(e);
        }
        else if (e.Kind == InputKind.MouseDown && e.Code == "M1")
        {
            AttachClick(e.SessionTimeMs);
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

    private void AttachMouseMovement(InputEventRecord e)
    {
        var attempt = RecentAttempts.FirstOrDefault(a =>
            !a.ClickTimeMs.HasValue &&
            e.SessionTimeMs >= a.OppositeDownTimeMs &&
            e.SessionTimeMs - a.OppositeDownTimeMs <= 750);

        if (attempt == null) return;

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

    }

    private void AttachClick(double clickMs)
    {
        var attempt = RecentAttempts.FirstOrDefault(a => !a.ClickTimeMs.HasValue && clickMs >= a.ReleaseTimeMs - 50 && clickMs - a.ReleaseTimeMs <= 500);
        if (attempt == null) return;
        attempt.ClickTimeMs = clickMs;
        GradeAttempt(attempt);
        RefreshAttempt(attempt);
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
        if (delay < _settings.IdealCounterMinMs)
        {
            attempt.Grade = TimingGrade.Overlap;
            return;
        }

        if (delay > _settings.IdealCounterMaxMs)
        {
            attempt.Grade = TimingGrade.Late;
            return;
        }

        if (!attempt.ClickTimeMs.HasValue)
        {
            attempt.Grade = TimingGrade.MissedClick;
            return;
        }

        double clickFromCounter = attempt.ClickFromCounterMs ?? 0;
        if (clickFromCounter < _settings.IdealClickMinAfterCounterMs)
        {
            attempt.Grade = TimingGrade.EarlyClick;
            return;
        }

        if (clickFromCounter > _settings.IdealClickMaxAfterCounterMs)
        {
            attempt.Grade = TimingGrade.Late;
            return;
        }

        attempt.Grade = TimingGrade.Perfect;
    }

    public SessionSummary BuildSummary(DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var attempts = RecentAttempts.Reverse().ToList();
        double[] delays = attempts.Select(a => a.CounterDelayMs).ToArray();
        double[] clickDeltas = attempts.Where(a => a.ClickFromCounterMs.HasValue).Select(a => a.ClickFromCounterMs!.Value).ToArray();
        var traced = attempts.Where(a => a.MouseTrace.Count > 0).ToList();
        double avgDelay = delays.Length == 0 ? 0 : delays.Average();
        double std = delays.Length == 0 ? 0 : Math.Sqrt(delays.Select(x => Math.Pow(x - avgDelay, 2)).Average());

        return new SessionSummary
        {
            SessionId = startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss"),
            StartedAt = startedAt,
            EndedAt = endedAt,
            TotalEvents = _events.Count,
            Attempts = attempts.Count,
            Perfect = attempts.Count(a => a.Grade == TimingGrade.Perfect),
            Overlap = attempts.Count(a => a.Grade == TimingGrade.Overlap),
            Late = attempts.Count(a => a.Grade == TimingGrade.Late),
            EarlyClick = attempts.Count(a => a.Grade == TimingGrade.EarlyClick),
            MissedClick = attempts.Count(a => a.Grade == TimingGrade.MissedClick),
            AverageCounterDelayMs = avgDelay,
            StdDevCounterDelayMs = std,
            AverageClickFromCounterMs = clickDeltas.Length == 0 ? 0 : clickDeltas.Average(),
            AttemptsWithMouseTrace = traced.Count,
            AverageMousePathDegrees = traced.Count == 0 ? 0 : traced.Average(a => a.PathLengthDegrees),
            AverageMouseDisplacementDegrees = traced.Count == 0 ? 0 : traced.Average(a => a.DisplacementDegrees),
            AverageMousePathEfficiency = traced.Count == 0 ? 0 : traced.Average(a => a.PathEfficiency),
            Calibration = _settings.Calibration.Clone()
        };
    }

    public string GetCoachingTip()
    {
        var latest = RecentAttempts.Take(40).ToList();
        if (latest.Count < 5) return "Start a session, strafe A/D, counter-strafe, and click. Tips appear after a few attempts.";

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

        if (overlapRate >= 35)
            return $"Main issue: overlap. Opposite key is often pressed before releasing the current key. Avg counter delay: {avg:0.0} ms. Aim for a small positive delay.";
        if (lateRate >= 35)
            return $"Main issue: late counter input. Avg counter delay: {avg:0.0} ms. Release and tap the opposite key sooner.";
        if (earlyClickRate >= 25)
            return "Main issue: clicking before the counter input has landed. Delay the shot until just after the opposite key press.";
        return $"Timing looks stable. Avg counter delay: {avg:0.0} ms. Focus on consistency and keeping outliers low.";
    }

    private sealed record PendingRelease(string FromKey, double ReleaseTimeMs);
}
