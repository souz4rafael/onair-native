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
        // OPTIONAL and often blank for local/self-hosted servers with no auth — still encrypted
        // at rest like every other key field on the off chance a real secret (e.g. a
        // reverse-proxy bearer token) is ever put here.
        (c => c.Local.Key,     (c, v) => c.Local.Key = v),
    ];

    /// <param name="configDirectory">Overrides where config.json lives — used by
    /// OnAirNative.Tests so test runs read/write an isolated temp directory instead of the
    /// real %LocalAppData%\onAIr\ (which would otherwise risk clobbering the developer's own
    /// saved settings/API keys during a test run). Legacy-folder migration only applies to the
    /// real default location; a test directory has no "old install" to migrate from.</param>
    public ConfigService(string? configDirectory = null)
    {
        if (configDirectory is not null)
        {
            Directory.CreateDirectory(configDirectory);
            ConfigPath = Path.Combine(configDirectory, "config.json");
            Load();
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "onAIr");
        Directory.CreateDirectory(dir);
        ConfigPath = Path.Combine(dir, "config.json");

        // One-time migration: versions up to 1.0.5 stored config.json under the old
        // "onAIr Native" folder name. Copy it over (don't delete the original) so
        // existing installs keep their settings and API keys after upgrading.
        MigrateLegacyConfigIfNeeded(Path.Combine(localAppData, "onAIr Native", "config.json"), ConfigPath);

        Load();
    }

    /// <summary>Copies a legacy config.json to the new location if the new one doesn't already
    /// exist and the legacy one does — extracted as its own pure, testable static method (takes
    /// explicit paths rather than reading %LocalAppData% itself) so OnAirNative.Tests can verify
    /// this exact behavior against two arbitrary temp paths, without touching any real installed
    /// config location. Never deletes the original legacy file. Swallows I/O errors — a failed
    /// migration should never prevent the app from starting with fresh defaults.</summary>
    internal static void MigrateLegacyConfigIfNeeded(string legacyConfigPath, string newConfigPath)
    {
        if (File.Exists(newConfigPath) || !File.Exists(legacyConfigPath)) return;
        try { File.Copy(legacyConfigPath, newConfigPath); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Config] Legacy migration failed: {ex.Message}");
        }
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
