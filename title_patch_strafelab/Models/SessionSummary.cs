namespace StrafeLab.Models;

public sealed class SessionSummary
{
    public string SessionId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public int TotalEvents { get; set; }
    public int Attempts { get; set; }
    public int RawAttempts { get; set; }
    public int FilteredAttempts { get; set; }
    public int JiggleAttempts { get; set; }
    public int NoClickAttempts { get; set; }
    public int Perfect { get; set; }
    public int Overlap { get; set; }
    public int Late { get; set; }
    public int EarlyClick { get; set; }
    public int MissedClick { get; set; }
    public int MovingAtClick { get; set; }
    public double AverageCounterDelayMs { get; set; }
    public double StdDevCounterDelayMs { get; set; }
    public double AverageClickFromCounterMs { get; set; }
    public int AttemptsWithMouseTrace { get; set; }
    public double AverageMousePathDegrees { get; set; }
    public double AverageMouseDisplacementDegrees { get; set; }
    public double AverageMousePathEfficiency { get; set; }
    public double AverageQualityScore { get; set; }
    public double AverageTimingScore { get; set; }
    public double AverageShotScore { get; set; }
    public double AverageMouseScore { get; set; }
    public double AverageConsistencyScore { get; set; }
    public int BestCleanStreak { get; set; }
    public string MainIssue { get; set; } = string.Empty;
    public string PracticePrescription { get; set; } = string.Empty;
    public GameCalibration Calibration { get; set; } = new();
    public double PerfectRate => Attempts == 0 ? 0 : Perfect * 100.0 / Attempts;
    public double MovingRate => Attempts == 0 ? 0 : MovingAtClick * 100.0 / Attempts;
}
