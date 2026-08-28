using System.Text.Json;
using System.Text.Json.Serialization;
using OnAirNative.Models;

namespace OnAirNative.Services;

/// <summary>
/// Loads and saves config.json to %LocalAppData%\onAIr\.
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

    // API-key fields encrypted at rest via DPAPI. Plaintext is kept in memory.
    private static (Func<AppConfig, string> get, Action<AppConfig, string> set)[] Secrets =>
    [
        (c => c.Azure.Key,     (c, v) => c.Azure.Key = v),
        (c => c.OpenAi.Key,    (c, v) => c.OpenAi.Key = v),
        (c => c.Groq.Key,      (c, v) => c.Groq.Key = v),
        (c => c.Anthropic.Key, (c, v) => c.Anthropic.Key = v),
        (c => c.Gemini.Key,    (c, v) => c.Gemini.Key = v),
        (c => c.Mistral.Key,   (c, v) => c.Mistral.Key = v),
    ];

    public ConfigService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "onAIr");
        Directory.CreateDirectory(dir);
        ConfigPath = Path.Combine(dir, "config.json");

        // One-time migration: versions up to 1.0.5 stored config.json under the old
        // "onAIr Native" folder name. Copy it over (don't delete the original) so
        // existing installs keep their settings and API keys after upgrading.
        var legacyConfigPath = Path.Combine(localAppData, "onAIr Native", "config.json");
        if (!File.Exists(ConfigPath) && File.Exists(legacyConfigPath))
        {
            try { File.Copy(legacyConfigPath, ConfigPath); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Legacy migration failed: {ex.Message}");
            }
        }

        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = File.ReadAllText(ConfigPath);
            Current = JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? new AppConfig();
            // Decrypt secrets into memory (disk stays encrypted)
            foreach (var (get, set) in Secrets) set(Current, SecretProtector.Unprotect(get(Current)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Load failed: {ex.Message}");
            Current = new AppConfig();
        }
    }

    public void Save()
    {
        // Encrypt secrets on disk while keeping plaintext in the in-memory model.
        var plaintext = Secrets.Select(s => s.get(Current)).ToArray();
        try
        {
            for (int i = 0; i < Secrets.Length; i++)
                Secrets[i].set(Current, SecretProtector.Protect(plaintext[i]));
            var json = JsonSerializer.Serialize(Current, _opts);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Save failed: {ex.Message}");
        }
        finally
        {
            for (int i = 0; i < Secrets.Length; i++)
                Secrets[i].set(Current, plaintext[i]);
        }
    }
}
