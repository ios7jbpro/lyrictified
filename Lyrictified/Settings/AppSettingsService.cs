using System.IO;
using System.Text.Json;

namespace Lyrictified.Settings;

public sealed class AppSettingsService
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, _serializerOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lyrictified");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
}
