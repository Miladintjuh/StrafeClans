using System.Globalization;
using System.IO;
using System.Text.Json;
using StrafeLab.Models;

namespace StrafeLab.Services;

public sealed class DemoSessionData
{
    public string SessionId { get; set; } = "demo-session";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now.AddSeconds(-35);
    public DateTimeOffset EndedAt { get; set; } = DateTimeOffset.Now;
    public List<InputEventRecord> Events { get; } = new();
    public List<StrafeAttempt> Attempts { get; } = new();
    public GameCalibration Calibration { get; set; } = new();
}

public static class DemoSessionDataLoader
{
    public static DemoSessionData LoadBundled()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "DemoData");
        if (!Directory.Exists(root))
        {
            root = Path.Combine(Directory.GetCurrentDirectory(), "DemoData");
        }

        var data = new DemoSessionData();
        LoadSummary(Path.Combine(root, "summary.json"), data);
        LoadAttempts(Path.Combine(root, "attempts.csv"), data);
        LoadMouseTraces(Path.Combine(root, "mouse_traces.csv"), data);
        LoadEvents(Path.Combine(root, "events.csv"), data);
        BackfillCounterKeyUpTimes(data);
        BackfillClickUpTimes(data);
        return data;
    }

    private static void LoadSummary(string path, DemoSessionData data)
    {
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        data.SessionId = GetString(root, "SessionId", data.SessionId);
        if (DateTimeOffset.TryParse(GetString(root, "StartedAt", string.Empty), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)) data.StartedAt = started;
        if (DateTimeOffset.TryParse(GetString(root, "EndedAt", string.Empty), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ended)) data.EndedAt = ended;
        if (root.TryGetProperty("Calibration", out var calibration))
        {
            data.Calibration = new GameCalibration
            {
                Dpi = GetDouble(calibration, "Dpi", 1600),
                Sensitivity = GetDouble(calibration, "Sensitivity", 0.4),
                YawDegreesPerCountAtSensitivityOne = GetDouble(calibration, "YawDegreesPerCountAtSensitivityOne", 0.022),
                PitchDegreesPerCountAtSensitivityOne = GetDouble(calibration, "PitchDegreesPerCountAtSensitivityOne", 0.022),
                Multiplier = GetDouble(calibration, "Multiplier", 1.0)
            };
        }
    }

    private static void LoadAttempts(string path, DemoSessionData data)
    {
        if (!File.Exists(path)) return;
        foreach (var row in ReadCsv(path))
        {
            string direction = Get(row, "direction");
            var parts = direction.Split("->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            string fromKey = parts.Length > 0 ? parts[0] : "A";
            string toKey = parts.Length > 1 ? parts[1] : (fromKey == "A" ? "D" : "A");
            bool hasClick = GetBool(row, "has_click");
            var attempt = new StrafeAttempt
            {
                Index = GetInt(row, "index"),
                FromKey = fromKey,
                ToKey = toKey,
                ReleaseTimeMs = GetDouble(row, "release_ms"),
                OppositeDownTimeMs = GetDouble(row, "opposite_down_ms"),
                CounterKeyUpTimeMs = GetNullableDouble(row, "counter_key_up_ms"),
                ClickTimeMs = hasClick ? GetDouble(row, "click_ms") : null,
                ClickUpTimeMs = hasClick ? (GetNullableDouble(row, "click_up_ms") ?? GetNullableDouble(row, "m1_up_ms")) : null,
                Grade = ParseGrade(Get(row, "grade")),
                Diagnosis = Get(row, "diagnosis"),
                IsMovingAtClick = GetBool(row, "moving_at_click"),
                HeldKeysAtClick = Get(row, "held_keys_at_click"),
                IsExcluded = GetBool(row, "manual_excluded"),
                IsJiggle = GetBool(row, "is_jiggle"),
                IsAutoTaggedJiggle = GetBool(row, "auto_tagged_jiggle")
            };
            data.Attempts.Add(attempt);
        }
    }


    private static void BackfillClickUpTimes(DemoSessionData data)
    {
        foreach (var attempt in data.Attempts)
        {
            if (!attempt.ClickTimeMs.HasValue || attempt.ClickUpTimeMs.HasValue) continue;
            var mouseUp = data.Events
                .Where(e => e.Kind == InputKind.MouseUp && e.Code == "M1" && e.SessionTimeMs >= attempt.ClickTimeMs.Value)
                .OrderBy(e => e.SessionTimeMs)
                .FirstOrDefault();
            if (mouseUp != null) attempt.ClickUpTimeMs = mouseUp.SessionTimeMs;
        }
    }

    private static void LoadMouseTraces(string path, DemoSessionData data)
    {
        if (!File.Exists(path)) return;
        var byAttempt = data.Attempts.ToDictionary(a => a.Index);
        foreach (var group in ReadCsv(path).GroupBy(row => GetInt(row, "attempt_index")))
        {
            if (!byAttempt.TryGetValue(group.Key, out var attempt)) continue;
            attempt.MouseTrace.Clear();
            foreach (var row in group.OrderBy(row => GetInt(row, "point_index")))
            {
                attempt.MouseTrace.Add(new MouseTracePoint
                {
                    SessionTimeMs = GetDouble(row, "session_ms"),
                    TimeFromCounterMs = GetDouble(row, "time_from_counter_ms"),
                    DeltaX = GetInt(row, "delta_x"),
                    DeltaY = GetInt(row, "delta_y"),
                    XCounts = GetInt(row, "x_counts"),
                    YCounts = GetInt(row, "y_counts"),
                    XDegrees = GetDouble(row, "x_degrees"),
                    YDegrees = GetDouble(row, "y_degrees")
                });
            }
        }
    }

    private static void LoadEvents(string path, DemoSessionData data)
    {
        if (!File.Exists(path)) return;
        foreach (var row in ReadCsv(path))
        {
            if (!Enum.TryParse<InputKind>(Get(row, "kind"), ignoreCase: true, out var kind)) continue;
            DateTimeOffset.TryParse(Get(row, "wall_time"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var wallTime);
            data.Events.Add(new InputEventRecord
            {
                WallTime = wallTime == default ? data.StartedAt.AddMilliseconds(GetDouble(row, "session_ms")) : wallTime,
                SessionTimeMs = GetDouble(row, "session_ms"),
                Code = Get(row, "code"),
                Kind = kind,
                DeltaX = GetInt(row, "delta_x"),
                DeltaY = GetInt(row, "delta_y")
            });
        }
    }

    private static void BackfillCounterKeyUpTimes(DemoSessionData data)
    {
        foreach (var attempt in data.Attempts)
        {
            if (attempt.CounterKeyUpTimeMs.HasValue) continue;
            var keyUp = data.Events
                .Where(e => e.Kind == InputKind.KeyUp && e.Code == attempt.ToKey && e.SessionTimeMs >= attempt.OppositeDownTimeMs)
                .OrderBy(e => e.SessionTimeMs)
                .FirstOrDefault();
            if (keyUp != null) attempt.CounterKeyUpTimeMs = keyUp.SessionTimeMs;
        }
    }

    private static TimingGrade ParseGrade(string value) => value.Trim().ToLowerInvariant() switch
    {
        "perfect" => TimingGrade.Perfect,
        "clean" => TimingGrade.Perfect,
        "overlap" => TimingGrade.Overlap,
        "late" => TimingGrade.Late,
        "slow" => TimingGrade.Late,
        "earlyclick" => TimingGrade.EarlyClick,
        "early shot" => TimingGrade.EarlyClick,
        "missedclick" => TimingGrade.MissedClick,
        "no shot" => TimingGrade.MissedClick,
        _ => TimingGrade.Unrated
    };

    private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);
        string? headerLine = reader.ReadLine();
        if (headerLine == null) yield break;
        var headers = SplitCsvLine(headerLine);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SplitCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++)
            {
                row[headers[i]] = i < cells.Count ? cells[i] : string.Empty;
            }
            yield return row;
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        cells.Add(current.ToString());
        return cells;
    }

    private static string Get(Dictionary<string, string> row, string key, string fallback = "") => row.TryGetValue(key, out var value) ? value : fallback;
    private static int GetInt(Dictionary<string, string> row, string key, int fallback = 0) => int.TryParse(Get(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private static double GetDouble(Dictionary<string, string> row, string key, double fallback = 0) => double.TryParse(Get(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
    private static double? GetNullableDouble(Dictionary<string, string> row, string key) => double.TryParse(Get(row, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    private static bool GetBool(Dictionary<string, string> row, string key) => Get(row, key).Trim() is "1" or "true" or "True" or "TRUE";
    private static string GetString(JsonElement element, string key, string fallback) => element.TryGetProperty(key, out var property) ? property.GetString() ?? fallback : fallback;
    private static double GetDouble(JsonElement element, string key, double fallback) => element.TryGetProperty(key, out var property) && property.TryGetDouble(out double value) ? value : fallback;
}
