namespace StrafeLab.Models;

public enum InputKind
{
    KeyDown,
    KeyUp,
    MouseDown,
    MouseUp,
    MouseMove
}

public sealed class InputEventRecord
{
    public DateTimeOffset WallTime { get; set; }
    public double SessionTimeMs { get; set; }
    public string Code { get; set; } = string.Empty;
    public InputKind Kind { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public int SessionSerial { get; set; } = 1;

    public string Label => Kind == InputKind.MouseMove
        ? $"{SessionTimeMs,9:0.0} ms  MOVE   dx {DeltaX,5} dy {DeltaY,5}"
        : $"{SessionTimeMs,9:0.0} ms  {Code,-6} {Kind}";
}
