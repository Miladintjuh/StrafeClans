using System.Diagnostics;

namespace StrafeLab.Services;

public sealed class HighResolutionClock
{
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private readonly double _tickToMs = 1000.0 / Stopwatch.Frequency;

    public double NowMs() => (Stopwatch.GetTimestamp() - _startTicks) * _tickToMs;
}
