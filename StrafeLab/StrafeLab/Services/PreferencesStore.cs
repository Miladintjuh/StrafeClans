using System.IO;
using System.Text.Json;
using StrafeLab.Models;

namespace StrafeLab.Services;

public sealed class PreferencesStore
{
    private readonly string _file;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public PreferencesStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrafeLab");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "preferences.json");
    }

    public AppPreferences Load()
    {
        try
        {
            if (!File.Exists(_file)) return new AppPreferences();
            var loaded = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(_file));
            return loaded ?? new AppPreferences();
        }
        catch
        {
            return new AppPreferences();
        }
    }

    public void Save(AppPreferences preferences)
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(preferences, _json));
        }
        catch
        {
            // Preference saving should never block training.
        }
    }
}
