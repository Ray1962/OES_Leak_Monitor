using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OES_Leak_Monitor;

/// <summary>
/// Loads and saves the dual-OES app settings as JSON in the per-user roaming AppData
/// folder. Synchronous because the file is tiny and we only touch it on startup and on
/// explicit user Save — keeps the call sites and the MainViewModel ctor simple.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ConfigDirectory { get; }
    public string ConfigFilePath  { get; }

    public SettingsService(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            throw new ArgumentException("Config directory is required.", nameof(configDirectory));
        ConfigDirectory = configDirectory;
        ConfigFilePath  = Path.Combine(ConfigDirectory, "settings.json");
    }

    /// <summary>Returns saved settings, or fresh defaults if the file is missing or malformed.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return new AppSettings();
            var json = File.ReadAllText(ConfigFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SettingsService.Load failed: {ex.Message} — falling back to defaults.");
            return new AppSettings();
        }
    }

    /// <summary>Atomic save: write to a temp file then move into place.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = ConfigFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigFilePath, overwrite: true);
    }
}
