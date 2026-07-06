using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StrafeLab.Models;
using StrafeLab.Services;

namespace StrafeLab;

public partial class MainWindow : Window
{
    private readonly AnalysisSettings _settings = new();
    private readonly HighResolutionClock _clock = new();
    private readonly StrafeAnalyzer _analyzer;
    private readonly SessionStore _store = new();
    private RawInputListener? _listener;
    private readonly DispatcherTimer _timer;
    private bool _active;
    private double _sessionStartMs;
    private DateTimeOffset _startedAt;

    public MainWindow()
    {
        InitializeComponent();

        _analyzer = new StrafeAnalyzer(_settings);
        AttemptsGrid.ItemsSource = _analyzer.RecentAttempts;
        EventsList.ItemsSource = _analyzer.RecentEvents;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => RefreshStats();
        _timer.Start();

        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _listener?.Dispose();

        RefreshStats();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _listener = new RawInputListener(_clock);
        _listener.Attach(hwnd);
        _listener.InputEdge += OnInputEdge;
        StatusText.Text = "Ready";
        RefreshStats();
    }

    private void OnInputEdge(InputEventRecord raw)
    {
        if (!_active) return;

        var e = new InputEventRecord
        {
            WallTime = raw.WallTime,
            SessionTimeMs = raw.SessionTimeMs - _sessionStartMs,
            Code = raw.Code,
            Kind = raw.Kind,
            DeltaX = raw.DeltaX,
            DeltaY = raw.DeltaY
        };

        _analyzer.Add(e);
        if (e.Kind != InputKind.MouseMove)
        {
            TipText.Text = _analyzer.GetCoachingTip();
            AttemptsGrid.Items.Refresh();
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi();
        _analyzer.Reset();
        _sessionStartMs = _clock.NowMs();
        _startedAt = DateTimeOffset.Now;
        _active = true;
        StatusText.Text = "Live";
        TipText.Text = "Session running. Use A/D, counter-strafe with the opposite key, move/correct your mouse, then click.";
        RefreshStats();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_active && _analyzer.Events.Count == 0) return;
        _active = false;
        StatusText.Text = "Saving";

        var endedAt = DateTimeOffset.Now;
        var summary = _analyzer.BuildSummary(_startedAt == default ? endedAt : _startedAt, endedAt);
        await _store.SaveAsync(summary, _analyzer.Events, _analyzer.Attempts);

        StatusText.Text = "Saved";
        TipText.Text = $"Saved session {summary.SessionId} to {_store.Root}";
        RefreshStats();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true });
    }

    private void TraceOption_Changed(object sender, RoutedEventArgs e)
    {
        RefreshTraceCanvas();
    }

    private void ApplySettingsFromUi()
    {
        _settings.IdealCounterMinMs = ParseBox(CounterMinBox.Text, 0);
        _settings.IdealCounterMaxMs = ParseBox(CounterMaxBox.Text, 80);
        _settings.IdealClickMinAfterCounterMs = ParseBox(ClickMinBox.Text, 0);
        _settings.IdealClickMaxAfterCounterMs = ParseBox(ClickMaxBox.Text, 160);

        _settings.Calibration.Dpi = ParseBox(DpiBox.Text, 800);
        _settings.Calibration.Sensitivity = ParseBox(SensBox.Text, 1.0);
        _settings.Calibration.YawDegreesPerCountAtSensitivityOne = ParseBox(YawBox.Text, 0.022);
        _settings.Calibration.PitchDegreesPerCountAtSensitivityOne = ParseBox(PitchBox.Text, 0.022);
        _settings.Calibration.Multiplier = ParseBox(MultiplierBox.Text, 1.0);
    }

    private static double ParseBox(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : fallback;
    }

    private void RefreshStats()
    {
        ApplySettingsFromUi();

        var now = DateTimeOffset.Now;
        var started = _startedAt == default ? now : _startedAt;
        var summary = _analyzer.BuildSummary(started, now);

        if (_active)
        {
            StatusText.Text = "Live";
            SessionTimeText.Text = TimeSpan.FromMilliseconds(Math.Max(0, _clock.NowMs() - _sessionStartMs)).ToString(@"mm\:ss\.f");
        }
        else
        {
            SessionTimeText.Text = "not recording";
        }

        AttemptsText.Text = summary.Attempts.ToString(CultureInfo.InvariantCulture);
        EventsText.Text = $"{summary.TotalEvents} events";
        PerfectRateText.Text = $"{summary.PerfectRate:0}%";
        PerfectDetailText.Text = $"{summary.Perfect} perfect / {summary.Attempts} total";
        AvgDelayText.Text = $"{summary.AverageCounterDelayMs:0.0} ms";
        SpreadText.Text = $"stdev {summary.StdDevCounterDelayMs:0.0} ms";
        Cm360Text.Text = _settings.Calibration.CmPer360 <= 0 ? "cm/360 unavailable" : $"{_settings.Calibration.CmPer360:0.0} cm/360";
        DegreesPerCountText.Text = $"{_settings.Calibration.HorizontalDegreesPerCount:0.000000} deg/count X";

        var week = _store.Aggregate(TimeSpan.FromDays(7));
        WeekRateText.Text = $"{week.PerfectRate:0}%";
        WeekDetailText.Text = $"{week.Attempts} attempts";

        AttemptsGrid.Items.Refresh();
        RefreshTraceCanvas();
    }

    private void RefreshTraceCanvas()
    {
        if (TraceCanvas == null) return;

        TraceCanvas.Children.Clear();
        double width = TraceCanvas.ActualWidth;
        double height = TraceCanvas.ActualHeight;
        if (width < 20 || height < 20)
        {
            TraceStatsText.Text = "Trace overlay initializes after the window is visible.";
            return;
        }

        int maxTraces = Math.Clamp((int)Math.Round(ParseBox(TraceCountBox.Text, 24)), 1, 200);
        var attempts = _analyzer.RecentAttempts
            .Where(a => a.MouseTrace.Count > 0)
            .Take(maxTraces)
            .ToList();

        if (attempts.Count == 0)
        {
            DrawAxes(width, height, 1.0);
            TraceStatsText.Text = "No mouse trace yet. Start a session, counter-strafe, move the mouse, then click.";
            return;
        }

        var traces = attempts.Select(a => BuildTrace(a)).Where(t => t.Count > 1).ToList();
        if (traces.Count == 0)
        {
            DrawAxes(width, height, 1.0);
            TraceStatsText.Text = "Waiting for movement points between counter key and click.";
            return;
        }

        double maxAbsX = traces.SelectMany(t => t).Select(p => Math.Abs(p.X)).DefaultIfEmpty(1).Max();
        double maxAbsY = traces.SelectMany(t => t).Select(p => Math.Abs(p.Y)).DefaultIfEmpty(1).Max();
        maxAbsX = Math.Max(maxAbsX, 0.05);
        maxAbsY = Math.Max(maxAbsY, 0.05);
        double scale = Math.Min((width - 28) / (maxAbsX * 2.0), (height - 28) / (maxAbsY * 2.0));

        DrawAxes(width, height, scale);

        if (ShowUniqueLinesCheckBox.IsChecked == true)
        {
            for (int i = traces.Count - 1; i >= 0; i--)
            {
                DrawPolyline(traces[i], width, height, scale, TraceBrush(i), 1.4);
                if (ShowClickMarkersCheckBox.IsChecked == true)
                {
                    DrawEndpoint(traces[i][^1], width, height, scale, TraceBrush(i));
                }
            }
        }

        if (ShowAverageLineCheckBox.IsChecked == true && traces.Count >= 2)
        {
            var averageTrace = BuildAverageTrace(traces, 64);
            DrawPolyline(averageTrace, width, height, scale, new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 3.0);
            if (ShowClickMarkersCheckBox.IsChecked == true)
            {
                DrawEndpoint(averageTrace[^1], width, height, scale, new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 4.0);
            }
        }

        var latest = attempts[0];
        TraceStatsText.Text = latest.MouseTrace.Count == 0
            ? "No trace metrics available."
            : $"Latest #{latest.Index}: {latest.TracePoints} pts, path {latest.PathLengthDegrees:0.00} deg, displacement {latest.DisplacementDegrees:0.00} deg, efficiency {latest.PathEfficiency:0.00}, end X {latest.HorizontalDegrees:+0.00;-0.00;0.00} deg / Y {latest.VerticalDegrees:+0.00;-0.00;0.00} deg";
    }

    private static List<TracePoint> BuildTrace(StrafeAttempt attempt)
    {
        var result = new List<TracePoint> { new(0, 0) };
        result.AddRange(attempt.MouseTrace.Select(p => new TracePoint(p.XDegrees, p.YDegrees)));
        return result;
    }

    private static List<TracePoint> BuildAverageTrace(IReadOnlyList<List<TracePoint>> traces, int samples)
    {
        var result = new List<TracePoint>();
        for (int i = 0; i < samples; i++)
        {
            double f = samples == 1 ? 0 : i / (double)(samples - 1);
            double x = 0;
            double y = 0;
            foreach (var trace in traces)
            {
                var p = InterpolateByFraction(trace, f);
                x += p.X;
                y += p.Y;
            }
            result.Add(new TracePoint(x / traces.Count, y / traces.Count));
        }
        return result;
    }

    private static TracePoint InterpolateByFraction(IReadOnlyList<TracePoint> trace, double fraction)
    {
        if (trace.Count == 0) return new TracePoint(0, 0);
        if (trace.Count == 1) return trace[0];
        double pos = fraction * (trace.Count - 1);
        int i = (int)Math.Floor(pos);
        if (i >= trace.Count - 1) return trace[^1];
        double local = pos - i;
        var a = trace[i];
        var b = trace[i + 1];
        return new TracePoint(a.X + (b.X - a.X) * local, a.Y + (b.Y - a.Y) * local);
    }

    private void DrawAxes(double width, double height, double scale)
    {
        var axisBrush = new SolidColorBrush(Color.FromArgb(80, 168, 176, 200));
        double cx = width / 2.0;
        double cy = height / 2.0;
        TraceCanvas.Children.Add(new Line { X1 = 0, Y1 = cy, X2 = width, Y2 = cy, Stroke = axisBrush, StrokeThickness = 1 });
        TraceCanvas.Children.Add(new Line { X1 = cx, Y1 = 0, X2 = cx, Y2 = height, Stroke = axisBrush, StrokeThickness = 1 });

        var origin = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromArgb(180, 0, 224, 255)) };
        Canvas.SetLeft(origin, cx - 3);
        Canvas.SetTop(origin, cy - 3);
        TraceCanvas.Children.Add(origin);

        var label = new TextBlock
        {
            Text = "0,0 counter-key press origin",
            Foreground = new SolidColorBrush(Color.FromArgb(180, 168, 176, 200)),
            FontSize = 11
        };
        Canvas.SetLeft(label, 10);
        Canvas.SetTop(label, 8);
        TraceCanvas.Children.Add(label);
    }

    private void DrawPolyline(IReadOnlyList<TracePoint> points, double width, double height, double scale, Brush brush, double thickness)
    {
        if (points.Count < 2) return;
        double cx = width / 2.0;
        double cy = height / 2.0;
        var polyline = new Polyline
        {
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        foreach (var p in points)
        {
            polyline.Points.Add(new Point(cx + p.X * scale, cy + p.Y * scale));
        }

        TraceCanvas.Children.Add(polyline);
    }

    private void DrawEndpoint(TracePoint point, double width, double height, double scale, Brush brush, double radius = 3.0)
    {
        double cx = width / 2.0;
        double cy = height / 2.0;
        var dot = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = brush,
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
        };
        Canvas.SetLeft(dot, cx + point.X * scale - radius);
        Canvas.SetTop(dot, cy + point.Y * scale - radius);
        TraceCanvas.Children.Add(dot);
    }

    private static Brush TraceBrush(int index)
    {
        Color[] colors =
        {
            Color.FromRgb(0, 224, 255),
            Color.FromRgb(124, 92, 255),
            Color.FromRgb(56, 217, 150),
            Color.FromRgb(255, 206, 69),
            Color.FromRgb(255, 92, 122),
            Color.FromRgb(120, 190, 255)
        };
        var c = colors[index % colors.Length];
        return new SolidColorBrush(Color.FromArgb(115, c.R, c.G, c.B));
    }

    private readonly record struct TracePoint(double X, double Y);
}
