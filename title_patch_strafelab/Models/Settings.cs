namespace StrafeLab.Models;

public sealed class AnalysisSettings
{
    public double IdealCounterMinMs { get; set; } = 0;
    public double IdealCounterMaxMs { get; set; } = 80;
    public double MaxAttemptPairWindowMs { get; set; } = 400;
    public double IdealClickMinAfterCounterMs { get; set; } = 0;
    public double IdealClickMaxAfterCounterMs { get; set; } = 160;
    public double KeyboardOverlapToleranceMs { get; set; } = 8;
    public bool AutoTagJiggles { get; set; } = true;
    public bool ExcludeJigglesFromStats { get; set; } = true;
    public bool ExcludeNoClickFromStats { get; set; } = true;
    public double JiggleAutoTagAfterMs { get; set; } = 260;
    public double JiggleMaxMousePathDegrees { get; set; } = 0.08;
    public GameCalibration Calibration { get; set; } = new();
}

public sealed class GameCalibration
{
    public double Dpi { get; set; } = 1600;
    public double Sensitivity { get; set; } = 0.4;
    public double YawDegreesPerCountAtSensitivityOne { get; set; } = 0.022;
    public double PitchDegreesPerCountAtSensitivityOne { get; set; } = 0.022;
    public double Multiplier { get; set; } = 1.0;

    public double HorizontalDegreesPerCount => Sensitivity * YawDegreesPerCountAtSensitivityOne * Multiplier;
    public double VerticalDegreesPerCount => Sensitivity * PitchDegreesPerCountAtSensitivityOne * Multiplier;
    public double CountsPer360 => HorizontalDegreesPerCount <= 0 ? 0 : 360.0 / HorizontalDegreesPerCount;
    public double CmPer360 => Dpi <= 0 || CountsPer360 <= 0 ? 0 : CountsPer360 / Dpi * 2.54;

    public GameCalibration Clone() => new()
    {
        Dpi = Dpi,
        Sensitivity = Sensitivity,
        YawDegreesPerCountAtSensitivityOne = YawDegreesPerCountAtSensitivityOne,
        PitchDegreesPerCountAtSensitivityOne = PitchDegreesPerCountAtSensitivityOne,
        Multiplier = Multiplier
    };
}
