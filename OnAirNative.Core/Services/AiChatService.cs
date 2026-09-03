using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OnAirNative.Models;

namespace OnAirNative.Services;

public record AiResult(bool Success, string Text = "", string? Error = null, int PromptTokens = 0, int CompletionTokens = 0);

/// <summary>One prior question/answer exchange — used to give the AI conversational memory
/// across consecutive Q&amp;A recordings (see OverlayViewModel's conversation history list).</summary>
public record ChatTurn(string UserText, string AssistantText);

/// <summary>
/// Sends chat completion requests to any of the 7 supported AI providers.
/// Also exposes a <see cref="TestConnectionAsync"/> for the credential dialog.
///
/// Provider routing:
///   azure     → Azure OpenAI (custom endpoint, api-key header)
///   openai    → api.openai.com (Bearer)
///   groq      → api.groq.com  (Bearer, OpenAI-compatible)
///   gemini    → generativelanguage.googleapis.com OpenAI compat endpoint
///   mistral   → api.mistral.ai (Bearer, OpenAI-compatible)
///   anthropic → api.anthropic.com (x-api-key, Messages API format)
///   local     → self-hosted OpenAI-compatible server (Ollama/LM Studio/llama-server/LocalAI),
///               user-configured base URL — same machine or another one on the LAN. Bearer
///               header is OMITTED entirely when the key is blank (see SendOpenAiCompatibleAsync)
///               rather than sent empty, since local servers vary in how they react to a
///               malformed empty Authorization header.
/// </summary>
public sealed class AiChatService : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _json = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="httpClient">Overrides the HTTP client — used by OnAirNative.Tests to inject
    /// one backed by a fake handler (so provider-routing/message-building logic can be verified
    /// without a real network call). Defaults to a real client when omitted, exactly as
    /// before this parameter existed.</param>
    public AiChatService(HttpClient? httpClient = null) =>
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    // ── Usage tracking (in-memory, resets on app restart) ────────────────────
    // Deliberately token counts only, not an estimated $ cost — per-model pricing tables change
    // too often and vary too much to hardcode reliably; showing a stale/wrong $ figure would be
    // worse than not showing one. Runs for the lifetime of this singleton service instance
    // (App.AiChat), i.e. resets naturally each time the app restarts — matches "how much have I
    // used this session" for a live broadcast, which is what actually matters to reason about.

    public int TotalPromptTokens     { get; private set; }
    public int TotalCompletionTokens { get; private set; }
    public int TotalCalls            { get; private set; }

    /// <summary>Raised after every successful call that reports usage, and after
    /// <see cref="ResetUsage"/> — lets AiTabViewModel refresh a live display without polling.</summary>
    public event EventHandler? UsageChanged;

    private void RecordUsage(int promptTokens, int completionTokens)
    {
        TotalPromptTokens     += promptTokens;
        TotalCompletionTokens += completionTokens;
        TotalCalls++;
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Manually zeroes the running usage counters — bound to a "Reset" button next to
    /// the usage display (e.g. starting a new stream/show).</summary>
    public void ResetUsage()
    {
        TotalPromptTokens     = 0;
        TotalCompletionTokens = 0;
        TotalCalls            = 0;
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Chat completion ───────────────────────────────────────────────────────

    /// <param name="history">Prior question/answer turns to give the model conversational
    /// memory — omit (or pass null/empty) for a single-shot question with no prior context,
    /// the original behavior before multi-turn support existed.</param>
    /// <param name="knowledgeBaseContext">Pre-searched, already-relevant excerpts from the
    /// user's attached reference documents for THIS specific question (see
    /// KnowledgeBaseService.BuildContextForQuestion) — omit/pass null or blank when no knowledge
    /// base is configured or nothing relevant was found. Unlike cfg.Glossary (static, always
    /// included when non-blank), this is per-question and computed by the caller BEFORE calling
    /// here, since searching the knowledge base is a separate concern from sending a chat
    /// request.</param>
    public Task<AiResult> GetAnswerAsync(string question, AppConfig cfg, IReadOnlyList<ChatTurn>? history = null, string? knowledgeBaseContext = null) =>
        cfg.Provider switch
        {
            "anthropic" => CallAnthropicAsync(question, cfg, history, knowledgeBaseContext),
            _           => CallOpenAiCompatibleAsync(question, cfg, history, knowledgeBaseContext),
        };

    private Task<AiResult> CallOpenAiCompatibleAsync(string question, AppConfig cfg, IReadOnlyList<ChatTurn>? history, string? knowledgeBaseContext) =>
        SendOpenAiCompatibleAsync(BuildMessages(question, cfg, history, knowledgeBaseContext), cfg.MaxTokens, cfg);

    private Task<AiResult> CallAnthropicAsync(string question, AppConfig cfg, IReadOnlyList<ChatTurn>? history, string? knowledgeBaseContext)
    {
        var systemText = BuildSystemText(cfg, knowledgeBaseContext);

        var messages = new List<object>();
        if (history is not null)
        {
            foreach (var turn in history)
            {
                messages.Add(new { role = "user", content = turn.UserText });
                messages.Add(new { role = "assistant", content = turn.AssistantText });
            }
        }
        messages.Add(new { role = "user", content = question });

        return SendAnthropicAsync(systemText, messages, cfg.MaxTokens, cfg);
    }

    /// <summary>Composes the full system-prompt text shared by both provider shapes:
    /// SystemPrompt + (optional) PresentationContext + (optional) Glossary + (optional) the
    /// per-question knowledge-base excerpts. Anthropic sends this as one "system" string field;
    /// OpenAI-compatible providers send the equivalent pieces as separate "system"-role messages
    /// (see BuildMessages) — same content, different wire shape.</summary>
    private static string BuildSystemText(AppConfig cfg, string? knowledgeBaseContext) =>
        cfg.SystemPrompt +
        (string.IsNullOrWhiteSpace(cfg.PresentationContext) ? ""
            : $"\n\nPresentation context:\n{cfg.PresentationContext}") +
        (string.IsNullOrWhiteSpace(cfg.Glossary) ? ""
            : $"\n\nGlossary / vocabulary (use these exact terms and spellings when relevant):\n{cfg.Glossary}") +
        (string.IsNullOrWhiteSpace(knowledgeBaseContext) ? ""
            : $"\n\nReference material (use only if relevant to the question; ignore otherwise):\n{knowledgeBaseContext}");

    // ── Follow-up question suggestions ───────────────────────────────────────

    /// <summary>Number of tokens requested for a follow-up-suggestions call — small and fixed
    /// (NOT the user's configurable MaxTokens) since suggestions are meant to be a few short
    /// questions, not full answers.</summary>
    private const int FollowUpSuggestionsMaxTokens = 150;

    /// <summary>Generates 2-3 short suggested questions the PRESENTER can ask THEIR CLIENT
    /// next, to keep the conversation flowing naturally after answering the client's question
    /// (a sales/conversation-flow aid — e.g. after answering a pricing question, suggest asking
    /// the client about their budget or timeline). Deliberately a SEPARATE, minimal call — no
    /// system prompt, no presentation context, no conversation history — rather than
    /// piggybacking on the main answer call, so the "suggest questions" instruction never
    /// competes with (or gets diluted/"dirtied" by) everything else already injected into the
    /// main question. Fails soft: any error or unparseable response yields an empty list rather
    /// than surfacing an error, since a missing set of suggestions must never be treated as a
    /// failure of the (already-succeeded) main answer.
    ///
    /// IMPORTANT framing (corrected after initial confusion): these are questions FOR THE
    /// PRESENTER TO ASK THE CLIENT — not questions the client might ask, and not questions to
    /// send to the AI. Rendered as plain, non-interactive text on the TP (see OverlayWindow),
    /// never as clickable buttons — the TP is frequently click-through/locked during live use,
    /// and the presenter reads/says these aloud themselves rather than "activating" them.</summary>
    public async Task<List<string>> GetFollowUpSuggestionsAsync(string question, string answer, AppConfig cfg)
    {
        var prompt =
            $"A presenter/salesperson was just asked by their client: \"{question}\"\n" +
            $"They answered: \"{answer}\"\n\n" +
            "Suggest exactly 3 short, natural questions the PRESENTER could ask the CLIENT next, " +
            "to keep the conversation flowing (e.g. qualifying needs, uncovering next steps, " +
            "building on what was just discussed). Reply with ONLY the 3 questions, one per line, " +
            "no numbering, no extra commentary.";

        var result = cfg.Provider == "anthropic"
            ? await SendAnthropicAsync("", [new { role = "user", content = prompt }], FollowUpSuggestionsMaxTokens, cfg)
            : await SendOpenAiCompatibleAsync([new { role = "user", content = prompt }], FollowUpSuggestionsMaxTokens, cfg);

        if (!result.Success) return [];

        return [.. result.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .Take(3)];
    }

    // ── Low-level per-provider senders (shared by GetAnswerAsync and
    // GetFollowUpSuggestionsAsync — the only difference between a full answer and a
    // suggestions call is which messages/max_tokens get sent in, not how the HTTP call or
    // response/usage parsing works) ────────────────────────────────────────────

    private async Task<AiResult> SendOpenAiCompatibleAsync(object[] messages, int maxTokens, AppConfig cfg)
    {
        try
        {
            var (url, model, key, isAzure) = ProviderParams(cfg);
            var body = JsonSerializer.Serialize(new { model, messages, max_tokens = maxTokens }, _json);

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            ApplyOpenAiCompatibleAuth(req, key, isAzure);

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

            var (promptTokens, completionTokens) = ParseOpenAiUsage(doc.RootElement);
            RecordUsage(promptTokens, completionTokens);
            return new AiResult(true, content, PromptTokens: promptTokens, CompletionTokens: completionTokens);
        }
        catch (Exception ex) { return new AiResult(false, Error: ex.Message); }
    }

    private async Task<AiResult> SendAnthropicAsync(string systemText, List<object> messages, int maxTokens, AppConfig cfg)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model      = cfg.Anthropic.ChatModel,
                max_tokens = maxTokens,
                system     = systemText,
                messages,
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

            var (promptTokens, completionTokens) = ParseAnthropicUsage(doc.RootElement);
            RecordUsage(promptTokens, completionTokens);
            return new AiResult(true, content, PromptTokens: promptTokens, CompletionTokens: completionTokens);
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
                "local"    => $"{cfg.Local.BaseUrl.TrimEnd('/')}/models",
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
                        "local"   => cfg.Local.Key,
                        _         => "",
                    };
                    // Local servers routinely have no auth at all — sending a malformed empty
                    // Bearer token can behave unpredictably depending on the server/reverse
                    // proxy, so the header is simply omitted when there's no real key (see
                    // ApplyOpenAiCompatibleAuth, used the same way for the actual chat calls).
                    if (!string.IsNullOrEmpty(key))
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

    /// <summary>Applies the request-auth header for an OpenAI-compatible chat call — shared by
    /// SendOpenAiCompatibleAsync (real answers/suggestions) so the "skip Bearer when the key is
    /// blank" relaxation only needs to live in one place. Azure always uses api-key regardless of
    /// whether it's blank (an Azure deployment always requires a real key in practice, and
    /// omitting the header there would just turn a clear 401 into a confusing "header missing"
    /// error instead).</summary>
    private static void ApplyOpenAiCompatibleAuth(HttpRequestMessage req, string key, bool isAzure)
    {
        if (isAzure) req.Headers.Add("api-key", key);
        else if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // internal (not private) + [InternalsVisibleTo] below so OnAirNative.Tests can verify
    // provider routing (URL/model/key/isAzure per provider key) directly and precisely,
    // rather than only indirectly through a mocked HTTP call.
    internal static (string Url, string Model, string Key, bool IsAzure) ProviderParams(AppConfig cfg) =>
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
            "local"  => ($"{cfg.Local.BaseUrl.TrimEnd('/')}/chat/completions",
                         cfg.Local.ChatModel, cfg.Local.Key, false),
            _        => throw new InvalidOperationException($"Unknown provider: {cfg.Provider}"),
        };

    private static object[] BuildMessages(string question, AppConfig cfg, IReadOnlyList<ChatTurn>? history, string? knowledgeBaseContext)
    {
        var msgs = new List<object> { new { role = "system", content = cfg.SystemPrompt } };
        if (!string.IsNullOrWhiteSpace(cfg.PresentationContext))
            msgs.Add(new { role = "system", content = $"Presentation context:\n{cfg.PresentationContext}" });
        if (!string.IsNullOrWhiteSpace(cfg.Glossary))
            msgs.Add(new { role = "system", content = $"Glossary / vocabulary (use these exact terms and spellings when relevant):\n{cfg.Glossary}" });
        if (!string.IsNullOrWhiteSpace(knowledgeBaseContext))
            msgs.Add(new { role = "system", content = $"Reference material (use only if relevant to the question; ignore otherwise):\n{knowledgeBaseContext}" });
        if (history is not null)
        {
            foreach (var turn in history)
            {
                msgs.Add(new { role = "user", content = turn.UserText });
                msgs.Add(new { role = "assistant", content = turn.AssistantText });
            }
        }
        msgs.Add(new { role = "user", content = question });
        return [.. msgs];
    }

    /// <summary>Parses the standard OpenAI-compatible top-level "usage" object
    /// ({"prompt_tokens":N,"completion_tokens":N,"total_tokens":N}) — shared by OpenAI, Azure
    /// OpenAI, Groq, Mistral, and Gemini's OpenAI-compat endpoint. Defensive: a provider that
    /// omits usage (or a shape we don't expect) yields (0, 0) rather than throwing — a missing
    /// usage figure must never fail an otherwise-successful answer.</summary>
    internal static (int PromptTokens, int CompletionTokens) ParseOpenAiUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return (0, 0);
        var prompt     = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
        return (prompt, completion);
    }

    /// <summary>Parses Anthropic's own top-level "usage" object
    /// ({"input_tokens":N,"output_tokens":N} — no combined total field, unlike OpenAI's shape).</summary>
    internal static (int PromptTokens, int CompletionTokens) ParseAnthropicUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return (0, 0);
        var input  = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
        var output = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;
        return (input, output);
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}
