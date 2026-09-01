using System.Net.Http;
using System.Net.Http.Headers;
using OnAirNative.Models;
using Whisper.net;

namespace OnAirNative.Services;

public record TranscriptionResult(bool Success, string Text = "", string? Error = null);

/// <summary>
/// Transcribes audio to text using either:
///   1. Whisper.net (in-process ggml model) — fast, no network, model file required
///   2. Cloud API (Azure / OpenAI / Groq Whisper)
///
/// Which one is used is an explicit choice (<see cref="AppConfig.UseLocalWhisper"/>, set via the
/// Q&amp;A tab's "Use local Whisper model" checkbox) — NOT automatically inferred from whether a
/// model happens to be loaded. Call <see cref="LoadModelAsync"/> with a path to a whisper.cpp-
/// format .bin/.gguf model file (download from huggingface.co/ggerganov/whisper.cpp) to make the
/// local model AVAILABLE; that alone doesn't make it the one actually used.
/// </summary>
public sealed class WhisperService : IDisposable
{
    private WhisperFactory?   _factory;
    private WhisperProcessor? _processor;
    private string?           _loadedPath;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    // whisper.net's native WhisperProcessor (whisper.cpp under the hood) is not safe to
    // invoke concurrently from two callers on the same instance — e.g. a live-preview
    // tick (OverlayViewModel.LivePreviewTick) racing the final post-recording
    // transcription. A concurrent second call into the native context can corrupt its
    // internal state and crash the whole process natively, with no managed exception
    // to catch and no entry in crash.log/launch.log — exactly the silent-death symptom
    // this gate fixes. Every TranscribeAsync call — local or cloud — is serialized
    // through it, since there's no benefit to parallelising HTTP calls here either.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsLocalModelLoaded => _factory is not null;

    // ── Local model management ────────────────────────────────────────────────

    public async Task<bool> LoadModelAsync(string modelPath)
    {
        if (_loadedPath == modelPath && _factory is not null) return true;
        try
        {
            _processor?.Dispose();
            _factory?.Dispose();

            _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath));
            _processor = _factory.CreateBuilder()
                .WithLanguage("auto")
                .Build();

            _loadedPath = modelPath;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Whisper] Model load failed: {ex.Message}");
            _factory    = null;
            _processor  = null;
            _loadedPath = null; // so retrying the same path doesn't short-circuit on the stale hit above
            return false;
        }
    }

    /// <summary>
    /// Unloads the local model, if any — used when the user clears the model path (switching
    /// back to the cloud API) or the configured file is no longer found. Without this,
    /// <see cref="IsLocalModelLoaded"/> stayed permanently true after the first successful
    /// local load: nothing ever reset <see cref="_factory"/> back to null when the path was
    /// cleared, so the app kept reporting "local" even after the user switched back to cloud.
    /// </summary>
    public void UnloadModel()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _processor  = null;
        _factory    = null;
        _loadedPath = null;
    }

    // ── Public transcription entry point ──────────────────────────────────────

    public async Task<TranscriptionResult> TranscribeAsync(byte[] wavData, AppConfig cfg)
    {
        if (wavData.Length == 0)
            return new TranscriptionResult(false, Error: "No audio was recorded.");

        await _gate.WaitAsync();
        try
        {
            // cfg.UseLocalWhisper is the explicit, persisted choice from the Q&A tab's "Use
            // local Whisper model" checkbox — the deciding factor, full stop. Previously this
            // checked IsLocalModelLoaded instead, meaning simply loading a model in Settings (or
            // it auto-loading from a saved path at startup) silently switched real
            // transcriptions to local even if the user never asked for that; and unloading it
            // would just as silently switch back to cloud. Now loading/unloading only affects
            // what's AVAILABLE — using it is a separate, explicit decision.
            if (cfg.UseLocalWhisper)
            {
                return IsLocalModelLoaded
                    ? await TranscribeLocalAsync(wavData)
                    : new TranscriptionResult(false, Error:
                        "Local Whisper model isn't loaded — load one in Settings → WHISPER MODEL, " +
                        "or uncheck \"Use local Whisper model\" in the Q&A tab to use a cloud provider instead.");
            }
            return await TranscribeViaApiAsync(wavData, cfg);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Local (whisper.net) ───────────────────────────────────────────────────

    private async Task<TranscriptionResult> TranscribeLocalAsync(byte[] wavData)
    {
        try
        {
            using var ms = new MemoryStream(wavData);
            var sb = new System.Text.StringBuilder();

            await foreach (var segment in _processor!.ProcessAsync(ms))
                sb.Append(segment.Text);

            return new TranscriptionResult(true, sb.ToString().Trim());
        }
        catch (Exception ex)
        {
            return new TranscriptionResult(false, Error: ex.Message);
        }
    }

    // ── Cloud API (OpenAI-compatible multipart/form-data) ─────────────────────

    private async Task<TranscriptionResult> TranscribeViaApiAsync(byte[] wavData, AppConfig cfg)
    {
        var provider = ResolveProvider(cfg);
        try
        {
            string url;
            string? modelName = null;
            bool    isAzure   = provider == "azure";

            if (isAzure)
            {
                var a = cfg.Azure;
                if (string.IsNullOrEmpty(a.Endpoint) || string.IsNullOrEmpty(a.Key) || string.IsNullOrEmpty(a.WhisperDeployment))
                    return new TranscriptionResult(false, Error: "Azure: endpoint, API key, and Whisper deployment are required.");
                url = $"{a.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(a.WhisperDeployment)}/audio/transcriptions?api-version=2024-06-01";
            }
            else
            {
                url       = provider == "groq" ? "https://api.groq.com/openai/v1/audio/transcriptions"
                                               : "https://api.openai.com/v1/audio/transcriptions";
                modelName = provider == "groq" ? cfg.Groq.WhisperModel : cfg.OpenAi.WhisperModel;
            }

            using var form = new MultipartFormDataContent();

            // Audio file part
            var audioContent = new ByteArrayContent(wavData);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", "audio.wav");

            if (modelName is not null)
                form.Add(new StringContent(modelName), "model");

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            ApplyAuth(req, provider, cfg);

            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return new TranscriptionResult(false, Error: $"HTTP {(int)resp.StatusCode}: {Truncate(json)}");

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
            return new TranscriptionResult(true, text);
        }
        catch (Exception ex)
        {
            return new TranscriptionResult(false, Error: ex.Message);
        }
    }

    /// <summary>Resolves which provider key actually handles cloud transcription — always
    /// <see cref="AppConfig.TranscriptionProvider"/>, i.e. whatever the Transcription dropdown
    /// shows, full stop.
    ///
    /// Previously this silently substituted the CHAT provider instead whenever it happened to
    /// be Whisper-capable (azure/openai/groq), completely ignoring an explicit Transcription
    /// selection — e.g. Chat=Groq + Transcription="Azure (Whisper)" actually called Groq for
    /// transcription, contradicting what the UI showed. Real bug, caught via the Q&amp;A tab's
    /// "Test connection" reporting "Whisper: same as Chat (Groq)" when the user had deliberately
    /// picked Azure. Public so callers outside this class (e.g. "Test connection") test the
    /// exact provider that transcription will actually use.</summary>
    public static string ResolveProvider(AppConfig cfg) => cfg.TranscriptionProvider;

    private static void ApplyAuth(HttpRequestMessage req, string provider, AppConfig cfg)
    {
        if (provider == "azure")
            req.Headers.Add("api-key", cfg.Azure.Key);
        else
        {
            var key = provider == "groq" ? cfg.Groq.Key : cfg.OpenAi.Key;
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _http.Dispose();
        _gate.Dispose();
    }
}
