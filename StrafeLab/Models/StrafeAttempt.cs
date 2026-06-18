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
    public string Diagnosis { get; set; } = "Waiting for shot";
    public bool IsMovingAtClick { get; set; }
    public string HeldKeysAtClick { get; set; } = string.Empty;
    public bool IsExcluded { get; set; }
    public bool IsJiggle { get; set; }
    public bool IsAutoTaggedJiggle { get; set; }
    public List<MouseTracePoint> MouseTrace { get; set; } = new();

    public bool HasClick => ClickTimeMs.HasValue;

    public bool IsIncluded
    {
        get => !IsExcluded;
        set => IsExcluded = !value;
    }

    public string Direction => $"{FromKey} -> {ToKey}";
    public string GradeLabel => Grade switch
    {
        TimingGrade.Perfect => "Clean",
        TimingGrade.Overlap => "Overlap",
        TimingGrade.Late => "Slow timing",
        TimingGrade.EarlyClick => "Early shot",
        TimingGrade.MissedClick => "No shot",
        _ => "Unrated"
    };

    public string MistakeLabel
    {
        get
        {
            var mistakes = new List<string>();
            if (Grade == TimingGrade.Overlap) mistakes.Add("Overlap");
            if (Grade == TimingGrade.Late) mistakes.Add("Slow");
            if (Grade == TimingGrade.EarlyClick) mistakes.Add("Early shot");
            if (IsMovingAtClick) mistakes.Add("Moving");
            return mistakes.Count == 0 ? "None" : string.Join(", ", mistakes);
        }
    }

    public string ResultLabel
    {
        get
        {
            if (!HasClick) return "No shot";
            if (IsMovingAtClick) return "Moving";
            if (Grade == TimingGrade.EarlyClick) return "Inaccurate";
            return "Accurate";
        }
    }
    public string CounterDelayLabel => $"{CounterDelayMs:+0.0;-0.0;0.0} ms";
    public string ClickLabel => ClickFromCounterMs.HasValue ? $"{ClickFromCounterMs.Value:+0.0;-0.0;0.0} ms" : "no click";
    public int TracePoints => MouseTrace.Count;
    public string TracePointsLabel => TracePoints == 0 ? "-" : TracePoints.ToString();
    public string FilterLabel => IsExcluded ? "Manual exclude" : IsJiggle ? "Jiggle" : !HasClick ? "No click" : "Included";

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
    public string AimControlLabel
    {
        get
        {
            if (MouseTrace.Count < 3) return "no trace";
            double eff = PathEfficiency;
            double path = PathLengthDegrees;
            double disp = DisplacementDegrees;
            if (path < 0.03) return "steady";
            if (eff >= 0.82) return "single line";
            if (eff >= 0.55 && path <= Math.Max(0.18, disp * 2.1)) return "micro-adjust";
            if (eff < 0.35 && path > Math.Max(0.18, disp * 3.0)) return "messy";
            if (path > Math.Max(0.35, disp * 2.35)) return "overflick";
            return "corrected";
        }
    }


    private double SumPath(Func<MouseTracePoint, MouseTracePoint, double> segment)
    {
        if (MouseTrace.Count < 2) return 0;
        double total = 0;
        for (int i = 1; i < MouseTrace.Count; i++) total += segment(MouseTrace[i - 1], MouseTrace[i]);
        return total;
    }
}
