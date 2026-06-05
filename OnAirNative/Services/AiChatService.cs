using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OnAirNative.Models;

namespace OnAirNative.Services;

public record AiResult(bool Success, string Text = "", string? Error = null);

/// <summary>
/// Sends chat completion requests to any of the 6 supported AI providers.
/// Also exposes a <see cref="TestConnectionAsync"/> for the credential dialog.
///
/// Provider routing:
///   azure     → Azure OpenAI (custom endpoint, api-key header)
///   openai    → api.openai.com (Bearer token)
///   groq      → api.groq.com  (Bearer token, OpenAI-compatible)
///   gemini    → generativelanguage.googleapis.com OpenAI compat endpoint
///   mistral   → api.mistral.ai (Bearer token, OpenAI-compatible)
///   anthropic → api.anthropic.com (x-api-key, Messages API format)
/// </summary>
public sealed class AiChatService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions _json = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ── Chat completion ───────────────────────────────────────────────────────

    public Task<AiResult> GetAnswerAsync(string question, AppConfig cfg) =>
        cfg.Provider switch
        {
            "anthropic" => CallAnthropicAsync(question, cfg),
            _           => CallOpenAiCompatibleAsync(question, cfg),
        };

    private async Task<AiResult> CallOpenAiCompatibleAsync(string question, AppConfig cfg)
    {
        try
        {
            var (url, model, key, isAzure) = ProviderParams(cfg);
            var messages = BuildMessages(question, cfg);
            var body     = JsonSerializer.Serialize(new { model, messages, max_tokens = 400 }, _json);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (isAzure) req.Headers.Add("api-key", key);
            else         req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return new AiResult(false, Error: $"HTTP {(int)resp.StatusCode}: {Clip(json)}");

            using var doc     = JsonDocument.Parse(json);
            var content = doc.RootElement
                             .GetProperty("choices")[0]
                             .GetProperty("message")
                             .GetProperty("content")
                             .GetString()?.Trim() ?? "";
            return new AiResult(true, content);
        }
        catch (Exception ex) { return new AiResult(false, Error: ex.Message); }
    }

    private async Task<AiResult> CallAnthropicAsync(string question, AppConfig cfg)
    {
        try
        {
            var systemText = cfg.SystemPrompt +
                (string.IsNullOrWhiteSpace(cfg.PresentationContext) ? ""
                    : $"\n\nPresentation context:\n{cfg.PresentationContext}");

            var body = JsonSerializer.Serialize(new
            {
                model      = cfg.Anthropic.ChatModel,
                max_tokens = 400,
                system     = systemText,
                messages   = new[] { new { role = "user", content = question } },
            }, _json);

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("x-api-key", cfg.Anthropic.Key);
            req.Headers.Add("anthropic-version", "2023-06-01");

            using var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return new AiResult(false, Error: $"HTTP {(int)resp.StatusCode}: {Clip(json)}");

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement.GetProperty("content")[0]
                             .GetProperty("text").GetString()?.Trim() ?? "";
            return new AiResult(true, content);
        }
        catch (Exception ex) { return new AiResult(false, Error: ex.Message); }
    }

    // ── Connection test ───────────────────────────────────────────────────────

    public async Task<AiResult> TestConnectionAsync(string provider, AppConfig cfg)
    {
        try
        {
            string testUrl;
            bool   isAzure = provider == "azure";

            testUrl = provider switch
            {
                "azure"    => $"{cfg.Azure.Endpoint.TrimEnd('/')}/openai/deployments?api-version=2024-02-01",
                "openai"   => "https://api.openai.com/v1/models",
                "groq"     => "https://api.groq.com/openai/v1/models",
                "anthropic"=> "https://api.anthropic.com/v1/models",
                "gemini"   => "https://generativelanguage.googleapis.com/v1beta/openai/models",
                "mistral"  => "https://api.mistral.ai/v1/models",
                _          => throw new InvalidOperationException($"Unknown provider: {provider}"),
            };

            using var req = new HttpRequestMessage(HttpMethod.Get, testUrl);

            switch (provider)
            {
                case "azure":
                    req.Headers.Add("api-key", cfg.Azure.Key);
                    break;
                case "anthropic":
                    req.Headers.Add("x-api-key", cfg.Anthropic.Key);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    break;
                default:
                    var key = provider switch
                    {
                        "openai"  => cfg.OpenAi.Key,
                        "groq"    => cfg.Groq.Key,
                        "gemini"  => cfg.Gemini.Key,
                        "mistral" => cfg.Mistral.Key,
                        _         => "",
                    };
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    break;
            }

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return resp.IsSuccessStatusCode
                ? new AiResult(true, $"Connected ✓ (HTTP {(int)resp.StatusCode})")
                : new AiResult(false, Error: $"HTTP {(int)resp.StatusCode} — check credentials");
        }
        catch (Exception ex) { return new AiResult(false, Error: ex.Message); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string Url, string Model, string Key, bool IsAzure) ProviderParams(AppConfig cfg) =>
        cfg.Provider switch
        {
            "azure"  => ($"{cfg.Azure.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(cfg.Azure.ChatDeployment)}/chat/completions?api-version=2024-10-21",
                         cfg.Azure.ChatDeployment, cfg.Azure.Key, true),
            "openai" => ("https://api.openai.com/v1/chat/completions",
                         cfg.OpenAi.ChatModel, cfg.OpenAi.Key, false),
            "groq"   => ("https://api.groq.com/openai/v1/chat/completions",
                         cfg.Groq.ChatModel, cfg.Groq.Key, false),
            "gemini" => ("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                         cfg.Gemini.ChatModel, cfg.Gemini.Key, false),
            "mistral"=> ("https://api.mistral.ai/v1/chat/completions",
                         cfg.Mistral.ChatModel, cfg.Mistral.Key, false),
            _        => throw new InvalidOperationException($"Unknown provider: {cfg.Provider}"),
        };

    private static object[] BuildMessages(string question, AppConfig cfg)
    {
        var msgs = new List<object> { new { role = "system", content = cfg.SystemPrompt } };
        if (!string.IsNullOrWhiteSpace(cfg.PresentationContext))
            msgs.Add(new { role = "system", content = $"Presentation context:\n{cfg.PresentationContext}" });
        msgs.Add(new { role = "user", content = question });
        return [.. msgs];
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}
