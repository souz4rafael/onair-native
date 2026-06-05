using System.Text.Json;
using System.Text.Json.Serialization;
using OnAirNative.Models;

namespace OnAirNative.Services;

/// <summary>
/// Loads and saves config.json to %LocalAppData%\onAIr Native\.
/// Config format is intentionally close to the Electron v1.3 config.json
/// to allow manual migration of credentials.
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string     ConfigPath { get; }
    public AppConfig  Current    { get; private set; } = new();

    public ConfigService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "onAIr Native");
        Directory.CreateDirectory(dir);
        ConfigPath = Path.Combine(dir, "config.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
            Current = JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Load failed: {ex.Message}");
            Current = new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, _opts);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Save failed: {ex.Message}");
        }
    }
}
