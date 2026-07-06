namespace StrafeLab.Models;

public sealed class MouseTracePoint
{
    public double SessionTimeMs { get; set; }
    public double TimeFromCounterMs { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public int XCounts { get; set; }
    public int YCounts { get; set; }
    public double XDegrees { get; set; }
    public double YDegrees { get; set; }
}
