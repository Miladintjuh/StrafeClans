namespace StrafeLab.Models;

public sealed class AttemptQualityBreakdown
{
    public int Timing { get; set; }
    public int TimingMax { get; set; } = 35;
    public int Shot { get; set; }
    public int ShotMax { get; set; } = 30;
    public int Mouse { get; set; }
    public int MouseMax { get; set; } = 20;
    public int Consistency { get; set; }
    public int ConsistencyMax { get; set; } = 15;
    public int Total => Timing + Shot + Mouse + Consistency;
    public int TotalMax => TimingMax + ShotMax + MouseMax + ConsistencyMax;

    public string Label => $"{Total}/{TotalMax} · timing {Timing}/{TimingMax}, shot {Shot}/{ShotMax}, mouse {Mouse}/{MouseMax}, consistency {Consistency}/{ConsistencyMax}";
}

public sealed class SessionCoachingInsight
{
    public string MainIssue { get; set; } = "Not enough data yet.";
    public string DirectionWeakness { get; set; } = "Record both directions to compare left/right.";
    public string PracticePrescription { get; set; } = "Record at least five click-confirmed attempts.";
    public string LatestAttemptLabel { get; set; } = "No attempt yet.";
    public string ConsistencyLabel { get; set; } = "Consistency: waiting for data.";
    public string PersonalBestLabel { get; set; } = "Best streak: waiting for data.";
    public string FocusFeedback { get; set; } = string.Empty;
    public AttemptQualityBreakdown AverageQuality { get; set; } = new();
}

public sealed class LocalProgress
{
    public int BestCleanStreak { get; set; }
    public int BestAccurateStreak { get; set; }
    public double BestSessionCleanRate { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
