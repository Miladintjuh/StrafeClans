using System.IO;
using System.Text.Json;
using StrafeLab.Models;

namespace StrafeLab.Services;

public sealed class ProgressStore
{
    private readonly string _file;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ProgressStore()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrafeLab");
        Directory.CreateDirectory(root);
        _file = Path.Combine(root, "progress.json");
    }

    public LocalProgress Load()
    {
        try
        {
            if (!File.Exists(_file)) return new LocalProgress();
            return JsonSerializer.Deserialize<LocalProgress>(File.ReadAllText(_file)) ?? new LocalProgress();
        }
        catch
        {
            return new LocalProgress();
        }
    }

    public LocalProgress UpdateFromSession(IReadOnlyList<StrafeAttempt> attempts)
    {
        var progress = Load();
        int currentClean = 0;
        int bestClean = 0;
        int currentAccurate = 0;
        int bestAccurate = 0;
        var included = attempts.Where(a => a.HasClick && !a.IsExcluded).OrderBy(a => a.Index).ToList();
        foreach (var a in included)
        {
            bool clean = a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick;
            bool accurate = a.ResultLabel == "Accurate";
            currentClean = clean ? currentClean + 1 : 0;
            currentAccurate = accurate ? currentAccurate + 1 : 0;
            bestClean = Math.Max(bestClean, currentClean);
            bestAccurate = Math.Max(bestAccurate, currentAccurate);
        }
        progress.BestCleanStreak = Math.Max(progress.BestCleanStreak, bestClean);
        progress.BestAccurateStreak = Math.Max(progress.BestAccurateStreak, bestAccurate);
        if (included.Count > 0)
        {
            double cleanRate = included.Count(a => a.Grade == TimingGrade.Perfect && !a.IsMovingAtClick) * 100.0 / included.Count;
            progress.BestSessionCleanRate = Math.Max(progress.BestSessionCleanRate, cleanRate);
        }
        progress.UpdatedAt = DateTimeOffset.Now;
        File.WriteAllText(_file, JsonSerializer.Serialize(progress, _json));
        return progress;
    }
}
