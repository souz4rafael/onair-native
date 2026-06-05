using System.Net.Http;
using System.Net.Http.Headers;
using OnAirNative.Models;
using Whisper.net;

namespace OnAirNative.Services;

public record TranscriptionResult(bool Success, string Text = "", string? Error = null);

/// <summary>
/// Transcribes audio to text using either:
///   1. Whisper.net (in-process ggml model) — fast, no network, model file required
///   2. Cloud API (Azure / OpenAI / Groq Whisper) — fallback when no local model
///
/// To use the local model, call <see cref="LoadModelAsync"/> with a path to a
/// whisper.cpp-format .bin/.gguf model file (download from huggingface.co/ggerganov/whisper.cpp).
/// </summary>
public sealed class WhisperService : IDisposable
{
    private WhisperFactory?   _factory;
    private WhisperProcessor? _processor;
    private string?           _loadedPath;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public bool IsLocalModelLoaded => _factory is not null;

    // ── Local model management ────────────────────────────────────────────────

    public async Task<bool> LoadModelAsync(string modelPath)
    {
        if (_loadedPath == modelPath) return true;
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
            _factory   = null;
            _processor = null;
            return false;
        }
    }

    // ── Public transcription entry point ──────────────────────────────────────

    public Task<TranscriptionResult> TranscribeAsync(byte[] wavData, AppConfig cfg)
    {
        if (wavData.Length == 0)
            return Task.FromResult(new TranscriptionResult(false, Error: "No audio was recorded."));

        return IsLocalModelLoaded
            ? TranscribeLocalAsync(wavData)
            : TranscribeViaApiAsync(wavData, cfg);
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

    private static string ResolveProvider(AppConfig cfg)
    {
        string[] whisperCapable = ["azure", "openai", "groq"];
        return whisperCapable.Contains(cfg.Provider) ? cfg.Provider : cfg.TranscriptionProvider;
    }

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
    }
}
