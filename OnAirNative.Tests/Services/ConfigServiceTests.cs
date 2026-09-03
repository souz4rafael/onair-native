using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Verifies ConfigService's save/load round-trip and legacy-folder migration — always against
/// an isolated temp directory (via the configDirectory constructor parameter added specifically
/// for this test project), NEVER the real %LocalAppData%\onAIr\config.json. Touching the real
/// path from a test run risked clobbering the developer's own saved settings/API keys — exactly
/// the kind of collision this whole session already hit once with UI Automation tests running
/// against a live app instance.
/// </summary>
public class ConfigServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "OnAirNativeTests_" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_WithNoExistingFile_UsesDefaultAppConfigValues()
    {
        var config = new ConfigService(_tempDir);

        Assert.Equal("azure", config.Current.Provider);
        Assert.Equal("openai", config.Current.TranscriptionProvider);
        Assert.False(config.Current.UseLocalWhisper);
    }

    [Fact]
    public void Constructor_WithNoExistingFile_CreatesTheDirectory()
    {
        Assert.False(Directory.Exists(_tempDir));

        _ = new ConfigService(_tempDir);

        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void SaveThenLoadFromNewInstance_RoundTripsPlainFields()
    {
        var first = new ConfigService(_tempDir);
        first.Current.Provider = "groq";
        first.Current.TranscriptionProvider = "azure";
        first.Current.UseLocalWhisper = true;
        first.Current.Appearance.FontSize = 30;
        first.Save();

        var second = new ConfigService(_tempDir);

        Assert.Equal("groq", second.Current.Provider);
        Assert.Equal("azure", second.Current.TranscriptionProvider);
        Assert.True(second.Current.UseLocalWhisper);
        Assert.Equal(30, second.Current.Appearance.FontSize);
    }

    [Fact]
    public void SaveThenLoadFromNewInstance_RoundTripsSecretApiKeys()
    {
        // Verifies the DPAPI encrypt-on-disk/decrypt-into-memory cycle is fully transparent to
        // callers — Current.Azure.Key should read back as the exact original plaintext, never
        // the "dpapi:v1:..." encrypted form that's only supposed to exist in the JSON on disk.
        var first = new ConfigService(_tempDir);
        first.Current.Azure.Key = "sk-test-secret-value-12345";
        first.Save();

        var second = new ConfigService(_tempDir);

        Assert.Equal("sk-test-secret-value-12345", second.Current.Azure.Key);
    }

    [Fact]
    public void Save_WritesEncryptedFormOnDisk_NeverPlaintext()
    {
        var config = new ConfigService(_tempDir);
        config.Current.OpenAi.Key = "sk-should-not-appear-in-plaintext";
        config.Save();

        var diskContent = File.ReadAllText(config.ConfigPath);

        Assert.DoesNotContain("sk-should-not-appear-in-plaintext", diskContent);
        Assert.Contains("dpapi:v1:", diskContent);
    }

    [Fact]
    public void Save_KeepsPlaintextInMemoryAfterwards()
    {
        // Save() must leave Current with plaintext for the REST of the running process — only
        // what hits disk should be encrypted. This was the exact bug class documented in
        // ConfigService's Save() (the finally block restoring plaintext after writing).
        var config = new ConfigService(_tempDir);
        config.Current.Groq.Key = "sk-in-memory-should-stay-plain";
        config.Save();

        Assert.Equal("sk-in-memory-should-stay-plain", config.Current.Groq.Key);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsLocalLmKeyAndBothModelFields()
    {
        // Key is OPTIONAL and often blank for real local/self-hosted setups, but on the off
        // chance a real secret IS put there (e.g. a reverse-proxy bearer token), it must get the
        // exact same DPAPI encrypt-at-rest/decrypt-into-memory treatment as every other
        // provider's Key field. ChatModel/WhisperModel are plain text (not secrets) but both
        // must round-trip too, since this one config now serves both roles.
        var first = new ConfigService(_tempDir);
        first.Current.Local.BaseUrl      = "http://192.168.1.50:11434/v1";
        first.Current.Local.Key          = "local-secret-value";
        first.Current.Local.ChatModel    = "llama3.2";
        first.Current.Local.WhisperModel = "whisper-1";
        first.Save();

        var diskContent = File.ReadAllText(first.ConfigPath);
        Assert.DoesNotContain("local-secret-value", diskContent);

        var second = new ConfigService(_tempDir);
        Assert.Equal("http://192.168.1.50:11434/v1", second.Current.Local.BaseUrl);
        Assert.Equal("local-secret-value", second.Current.Local.Key);
        Assert.Equal("llama3.2", second.Current.Local.ChatModel);
        Assert.Equal("whisper-1", second.Current.Local.WhisperModel);
    }

    [Fact]
    public void MigrateLegacyConfigIfNeeded_CopiesWhenNewDoesNotExistAndLegacyDoes()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy-config.json");
        var newPath    = Path.Combine(_tempDir, "new-config.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(legacyPath, """{"provider":"mistral"}""");

        ConfigService.MigrateLegacyConfigIfNeeded(legacyPath, newPath);

        Assert.True(File.Exists(newPath));
        Assert.Equal("""{"provider":"mistral"}""", File.ReadAllText(newPath));
        Assert.True(File.Exists(legacyPath)); // original must survive — never deleted
    }

    [Fact]
    public void MigrateLegacyConfigIfNeeded_DoesNotOverwriteWhenNewAlreadyExists()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy-config.json");
        var newPath    = Path.Combine(_tempDir, "new-config.json");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(legacyPath, """{"provider":"legacy-value"}""");
        File.WriteAllText(newPath, """{"provider":"current-value"}""");

        ConfigService.MigrateLegacyConfigIfNeeded(legacyPath, newPath);

        Assert.Equal("""{"provider":"current-value"}""", File.ReadAllText(newPath));
    }

    [Fact]
    public void MigrateLegacyConfigIfNeeded_DoesNothingWhenNeitherFileExists()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy-config.json");
        var newPath    = Path.Combine(_tempDir, "new-config.json");

        // Must not throw even though _tempDir itself doesn't exist yet.
        ConfigService.MigrateLegacyConfigIfNeeded(legacyPath, newPath);

        Assert.False(File.Exists(newPath));
    }
}
