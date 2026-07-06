namespace StrafeLab.Models;

public enum TimingGrade
{
    Perfect,
    Overlap,
    Late,
    MissedClick,
    EarlyClick,
    Unrated
}

public sealed class StrafeAttempt
{
    public int Index { get; set; }
    public string FromKey { get; set; } = string.Empty;
    public string ToKey { get; set; } = string.Empty;
    public double ReleaseTimeMs { get; set; }
    public double OppositeDownTimeMs { get; set; }
    public double CounterDelayMs => OppositeDownTimeMs - ReleaseTimeMs;
    public double? ClickTimeMs { get; set; }
    public double? ClickFromReleaseMs => ClickTimeMs.HasValue ? ClickTimeMs.Value - ReleaseTimeMs : null;
    public double? ClickFromCounterMs => ClickTimeMs.HasValue ? ClickTimeMs.Value - OppositeDownTimeMs : null;
    public TimingGrade Grade { get; set; } = TimingGrade.Unrated;
    public List<MouseTracePoint> MouseTrace { get; set; } = new();

    public string Direction => $"{FromKey} -> {ToKey}";
    public string CounterDelayLabel => $"{CounterDelayMs:+0.0;-0.0;0.0} ms";
    public string ClickLabel => ClickFromCounterMs.HasValue ? $"{ClickFromCounterMs.Value:+0.0;-0.0;0.0} ms" : "-";
    public int TracePoints => MouseTrace.Count;
    public string TracePointsLabel => TracePoints == 0 ? "-" : TracePoints.ToString();

    public int RawEndX => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].XCounts;
    public int RawEndY => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].YCounts;
    public double HorizontalDegrees => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].XDegrees;
    public double VerticalDegrees => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].YDegrees;
    public double DisplacementCounts => Math.Sqrt(RawEndX * RawEndX + RawEndY * RawEndY);
    public double DisplacementDegrees => Math.Sqrt(HorizontalDegrees * HorizontalDegrees + VerticalDegrees * VerticalDegrees);
    public double PathLengthCounts => SumPath((a, b) => Math.Sqrt(Math.Pow(b.XCounts - a.XCounts, 2) + Math.Pow(b.YCounts - a.YCounts, 2)));
    public double PathLengthDegrees => SumPath((a, b) => Math.Sqrt(Math.Pow(b.XDegrees - a.XDegrees, 2) + Math.Pow(b.YDegrees - a.YDegrees, 2)));
    public double PathEfficiency => PathLengthCounts <= 0 ? 0 : DisplacementCounts / PathLengthCounts;
    public double LastPointTimeFromCounterMs => MouseTrace.Count == 0 ? 0 : MouseTrace[^1].TimeFromCounterMs;

    public string AimDeltaLabel => MouseTrace.Count == 0 ? "-" : $"X {HorizontalDegrees:+0.00;-0.00;0.00} deg, Y {VerticalDegrees:+0.00;-0.00;0.00} deg";
    public string PathLabel => MouseTrace.Count == 0 ? "-" : $"{PathLengthDegrees:0.00} deg / eff {PathEfficiency:0.00}";

    private double SumPath(Func<MouseTracePoint, MouseTracePoint, double> segment)
    {
        if (MouseTrace.Count < 2) return 0;
        double total = 0;
        for (int i = 1; i < MouseTrace.Count; i++) total += segment(MouseTrace[i - 1], MouseTrace[i]);
        return total;
    }
}
