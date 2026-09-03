using System.Net;
using System.Text.Json;
using OnAirNative.Models;
using OnAirNative.Services;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Covers AiChatService's provider routing (ProviderParams — URL/model/key/isAzure per
/// provider key) and TestConnectionAsync's success/failure mapping + per-provider auth header
/// selection, using a FakeHttpMessageHandler instead of a real network call.
/// </summary>
public class AiChatServiceTests
{
    private static AppConfig MakeConfig(string provider) => new()
    {
        Provider  = provider,
        Azure     = new AzureConfig     { Endpoint = "https://my-resource.openai.azure.com", Key = "azure-key", ChatDeployment = "gpt4o-deployment" },
        OpenAi    = new OpenAiConfig    { Key = "openai-key", ChatModel = "gpt-4o" },
        Groq      = new GroqConfig      { Key = "groq-key", ChatModel = "llama-3.3-70b-versatile" },
        Anthropic = new AnthropicConfig { Key = "anthropic-key", ChatModel = "claude-3-5-haiku-20241022" },
        Gemini    = new GeminiConfig    { Key = "gemini-key", ChatModel = "gemini-2.0-flash" },
        Mistral   = new MistralConfig   { Key = "mistral-key", ChatModel = "mistral-small-latest" },
        Local     = new LocalConfig     { BaseUrl = "http://localhost:11434/v1", ChatModel = "llama3.2", Key = "" },
    };

    [Fact]
    public void ProviderParams_Azure_BuildsDeploymentScopedUrlAndFlagsIsAzure()
    {
        var (url, model, key, isAzure) = AiChatService.ProviderParams(MakeConfig("azure"));

        Assert.Equal("https://my-resource.openai.azure.com/openai/deployments/gpt4o-deployment/chat/completions?api-version=2024-10-21", url);
        Assert.Equal("gpt4o-deployment", model);
        Assert.Equal("azure-key", key);
        Assert.True(isAzure);
    }

    [Theory]
    [InlineData("openai",  "https://api.openai.com/v1/chat/completions",              "gpt-4o",                      "openai-key")]
    [InlineData("groq",    "https://api.groq.com/openai/v1/chat/completions",         "llama-3.3-70b-versatile",     "groq-key")]
    [InlineData("gemini",  "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-2.0-flash", "gemini-key")]
    [InlineData("mistral", "https://api.mistral.ai/v1/chat/completions",              "mistral-small-latest",        "mistral-key")]
    public void ProviderParams_NonAzureOpenAiCompatibleProviders_BuildCorrectUrlModelKey(
        string provider, string expectedUrl, string expectedModel, string expectedKey)
    {
        var (url, model, key, isAzure) = AiChatService.ProviderParams(MakeConfig(provider));

        Assert.Equal(expectedUrl, url);
        Assert.Equal(expectedModel, model);
        Assert.Equal(expectedKey, key);
        Assert.False(isAzure);
    }

    [Fact]
    public void ProviderParams_UnknownProvider_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => AiChatService.ProviderParams(MakeConfig("not-a-real-provider")));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1",  "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")] // trailing slash trimmed, same as Azure's Endpoint
    [InlineData("http://192.168.1.50:11434/v1", "http://192.168.1.50:11434/v1/chat/completions")] // a LAN IP works exactly the same as localhost
    public void ProviderParams_Local_AppendsChatCompletionsToConfiguredBaseUrl(string baseUrl, string expectedUrl)
    {
        var cfg = MakeConfig("local");
        cfg.Local.BaseUrl = baseUrl;

        var (url, model, key, isAzure) = AiChatService.ProviderParams(cfg);

        Assert.Equal(expectedUrl, url);
        Assert.Equal("llama3.2", model);
        Assert.Equal("", key);
        Assert.False(isAzure);
    }

    [Fact]
    public async Task TestConnectionAsync_HttpSuccess_ReturnsSuccessResult()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new AiChatService(new HttpClient(handler));

        var result = await service.TestConnectionAsync("openai", MakeConfig("openai"));

        Assert.True(result.Success);
        Assert.Contains("200", result.Text);
    }

    [Fact]
    public async Task TestConnectionAsync_HttpUnauthorized_ReturnsFailureWithStatusCodeInError()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var service = new AiChatService(new HttpClient(handler));

        var result = await service.TestConnectionAsync("openai", MakeConfig("openai"));

        Assert.False(result.Success);
        Assert.Contains("401", result.Error);
    }

    [Fact]
    public async Task TestConnectionAsync_Azure_UsesApiKeyHeaderNotBearerAuth()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new AiChatService(new HttpClient(handler));

        await service.TestConnectionAsync("azure", MakeConfig("azure"));

        Assert.Null(handler.LastRequest!.Headers.Authorization);
        Assert.Equal("azure-key", handler.LastRequest.Headers.GetValues("api-key").Single());
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("groq")]
    [InlineData("gemini")]
    [InlineData("mistral")]
    public async Task TestConnectionAsync_OpenAiCompatibleProviders_UseBearerAuthHeader(string provider)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new AiChatService(new HttpClient(handler));

        await service.TestConnectionAsync(provider, MakeConfig(provider));

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.False(handler.LastRequest.Headers.Contains("api-key"));
    }

    [Fact]
    public async Task TestConnectionAsync_Anthropic_UsesXApiKeyAndVersionHeaders()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new AiChatService(new HttpClient(handler));

        await service.TestConnectionAsync("anthropic", MakeConfig("anthropic"));

        Assert.Null(handler.LastRequest!.Headers.Authorization);
        Assert.Equal("anthropic-key", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
    }

    // ── Local LM provider (Block 4) ───────────────────────────────────────────

    [Fact]
    public async Task TestConnectionAsync_Local_HitsModelsEndpointOnConfiguredBaseUrl()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("local");
        cfg.Local.BaseUrl = "http://192.168.1.50:11434/v1";

        var result = await service.TestConnectionAsync("local", cfg);

        Assert.True(result.Success);
        Assert.Equal("http://192.168.1.50:11434/v1/models", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task TestConnectionAsync_Local_BlankKey_OmitsAuthorizationHeaderEntirely()
    {
        // Real-world local servers (Ollama, LM Studio, llama-server with no auth configured)
        // routinely have no credentials at all — sending a malformed empty Bearer token can
        // behave unpredictably depending on the server/any reverse proxy in front of it, so the
        // header must be OMITTED entirely rather than sent as "Bearer " (empty).
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("local"); // Local.Key defaults to "" in MakeConfig

        await service.TestConnectionAsync("local", cfg);

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task TestConnectionAsync_Local_NonBlankKey_UsesBearerAuth()
    {
        // Some local setups DO put a real secret behind a reverse proxy — must still work.
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("local");
        cfg.Local.Key = "a-real-bearer-token";

        await service.TestConnectionAsync("local", cfg);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("a-real-bearer-token", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task GetAnswerAsync_Local_BlankKey_OmitsAuthorizationHeaderEntirely()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("hi", 5, 5));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("local"); // Local.Key defaults to "" in MakeConfig

        var result = await service.GetAnswerAsync("question", cfg);

        Assert.True(result.Success);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task GetAnswerAsync_Local_NonBlankKey_UsesBearerAuth()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("hi", 5, 5));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("local");
        cfg.Local.Key = "a-real-bearer-token";

        await service.GetAnswerAsync("question", cfg);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task GetAnswerAsync_OpenAiWithRealKey_StillSendsBearerAuth_RegressionCheck()
    {
        // The empty-key relaxation added for "local" must NOT affect providers that always have
        // a real, non-blank key configured (every cloud provider in normal use) — this is the
        // exact same assertion TestConnectionAsync_OpenAiCompatibleProviders_UseBearerAuthHeader
        // already covers for TestConnectionAsync; this one covers the actual chat-call path.
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("hi", 5, 5));
        var service = new AiChatService(new HttpClient(handler));

        await service.GetAnswerAsync("question", MakeConfig("openai"));

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("openai-key", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    // ── GetAnswerAsync: max_tokens threading ─────────────────────────────────

    [Fact]
    public async Task GetAnswerAsync_OpenAiCompatible_SendsConfiguredMaxTokensNotHardcoded400()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("hi", 10, 5));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("openai");
        cfg.MaxTokens = 900;

        await service.GetAnswerAsync("question", cfg);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(900, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task GetAnswerAsync_Anthropic_SendsConfiguredMaxTokensNotHardcoded400()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, AnthropicResponseJson("hi", 10, 5));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("anthropic");
        cfg.MaxTokens = 250;

        await service.GetAnswerAsync("question", cfg);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(250, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    // ── GetAnswerAsync: multi-turn history ───────────────────────────────────

    [Fact]
    public async Task GetAnswerAsync_OpenAiCompatible_WithHistory_InterleavesUserAssistantTurnsBeforeNewQuestion()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("answer", 1, 1));
        var service = new AiChatService(new HttpClient(handler));
        var history = new List<ChatTurn> { new("first question", "first answer") };

        await service.GetAnswerAsync("second question", MakeConfig("openai"), history);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        // [0]=system prompt, [1]=history user, [2]=history assistant, [3]=new question
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("first question", messages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("first answer", messages[2].GetProperty("content").GetString());
        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Equal("second question", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetAnswerAsync_OpenAiCompatible_NoHistory_SendsOnlySystemAndQuestion()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("answer", 1, 1));
        var service = new AiChatService(new HttpClient(handler));

        await service.GetAnswerAsync("only question", MakeConfig("openai"));

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength()); // system + user only
    }

    // ── GetAnswerAsync: glossary & knowledge base (Block 3) ──────────────────

    [Fact]
    public async Task GetAnswerAsync_OpenAiCompatible_WithGlossary_AddsGlossarySystemMessage()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("answer", 1, 1));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("openai");
        cfg.Glossary = "Contoso, Northwind Traders, SKU-4471";

        await service.GetAnswerAsync("question", cfg);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength()); // system prompt + glossary + question
        Assert.Equal("system", messages[1].GetProperty("role").GetString());
        Assert.Contains("SKU-4471", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetAnswerAsync_OpenAiCompatible_WithKnowledgeBaseContext_AddsReferenceMaterialSystemMessage()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("answer", 1, 1));
        var service = new AiChatService(new HttpClient(handler));

        await service.GetAnswerAsync("question", MakeConfig("openai"), knowledgeBaseContext: "Widget Pro costs $499/year.");

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength()); // system prompt + reference material + question
        Assert.Contains("Widget Pro costs $499/year", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetAnswerAsync_OpenAiCompatible_GlossaryAndKnowledgeBaseAndHistory_AllComposeTogetherInOrder()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("answer", 1, 1));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("openai");
        cfg.Glossary = "Contoso";
        var history = new List<ChatTurn> { new("prior q", "prior a") };

        await service.GetAnswerAsync("new question", cfg, history, knowledgeBaseContext: "Some KB excerpt.");

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        // [0]=system prompt, [1]=glossary, [2]=reference material, [3]=history user, [4]=history assistant, [5]=new question
        Assert.Equal(6, messages.GetArrayLength());
        Assert.Contains("Contoso", messages[1].GetProperty("content").GetString());
        Assert.Contains("Some KB excerpt.", messages[2].GetProperty("content").GetString());
        Assert.Equal("prior q", messages[3].GetProperty("content").GetString());
        Assert.Equal("new question", messages[5].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetAnswerAsync_Anthropic_WithGlossaryAndKnowledgeBaseContext_AppendsToSystemText()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, AnthropicResponseJson("answer", 1, 1));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("anthropic");
        cfg.Glossary = "Northwind Traders";

        await service.GetAnswerAsync("question", cfg, knowledgeBaseContext: "Reference excerpt here.");

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var systemText = doc.RootElement.GetProperty("system").GetString();
        Assert.Contains("Northwind Traders", systemText);
        Assert.Contains("Reference excerpt here.", systemText);
    }

    [Fact]
    public async Task GetAnswerAsync_Anthropic_WithHistory_InterleavesUserAssistantTurns()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, AnthropicResponseJson("answer", 1, 1));
        var service = new AiChatService(new HttpClient(handler));
        var history = new List<ChatTurn> { new("prior q", "prior a") };

        await service.GetAnswerAsync("new q", MakeConfig("anthropic"), history);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength()); // history user + history assistant + new user
        Assert.Equal("prior q", messages[0].GetProperty("content").GetString());
        Assert.Equal("prior a", messages[1].GetProperty("content").GetString());
        Assert.Equal("new q", messages[2].GetProperty("content").GetString());
    }

    // ── Follow-up question suggestions (Block 2) ─────────────────────────────

    [Fact]
    public async Task GetFollowUpSuggestionsAsync_SuccessfulCall_ParsesOneSuggestionPerLine()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            OpenAiResponseJson("What's the pricing?\nHow long is the trial?\nIs support included?", 20, 15));
        var service = new AiChatService(new HttpClient(handler));

        var suggestions = await service.GetFollowUpSuggestionsAsync("q", "a", MakeConfig("openai"));

        Assert.Equal(["What's the pricing?", "How long is the trial?", "Is support included?"], suggestions);
    }

    [Fact]
    public async Task GetFollowUpSuggestionsAsync_DoesNotSendSystemPromptOrPresentationContext()
    {
        // The whole point of a separate call (per the user's own reasoning) is to NOT dilute
        // the suggestion request with everything already injected into the main answer call.
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("q1\nq2\nq3", 1, 1));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("openai");
        cfg.SystemPrompt = "You are a helpful assistant.";
        cfg.PresentationContext = "Presenting to Contoso.";

        await service.GetFollowUpSuggestionsAsync("q", "a", cfg);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength()); // only the suggestions prompt itself
        Assert.DoesNotContain("helpful assistant", messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain("Contoso", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetFollowUpSuggestionsAsync_UsesSmallFixedMaxTokensNotUserConfiguredValue()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("q1\nq2\nq3", 1, 1));
        var service = new AiChatService(new HttpClient(handler));
        var cfg = MakeConfig("openai");
        cfg.MaxTokens = 1500; // the user's configured value for full answers

        await service.GetFollowUpSuggestionsAsync("q", "a", cfg);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        Assert.NotEqual(1500, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(doc.RootElement.GetProperty("max_tokens").GetInt32() < 400); // small, suggestion-sized budget
    }

    [Fact]
    public async Task GetFollowUpSuggestionsAsync_HttpFailure_ReturnsEmptyListRatherThanThrowing()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "{}");
        var service = new AiChatService(new HttpClient(handler));

        var suggestions = await service.GetFollowUpSuggestionsAsync("q", "a", MakeConfig("openai"));

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task GetFollowUpSuggestionsAsync_Anthropic_RoutesToAnthropicEndpoint()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, AnthropicResponseJson("q1\nq2\nq3", 1, 1));
        var service = new AiChatService(new HttpClient(handler));

        var suggestions = await service.GetFollowUpSuggestionsAsync("q", "a", MakeConfig("anthropic"));

        Assert.Equal(["q1", "q2", "q3"], suggestions);
        Assert.Equal("anthropic-key", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task GetFollowUpSuggestionsAsync_MoreThanThreeLinesReturned_TakesOnlyFirstThree()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("q1\nq2\nq3\nq4\nq5", 1, 1));
        var service = new AiChatService(new HttpClient(handler));

        var suggestions = await service.GetFollowUpSuggestionsAsync("q", "a", MakeConfig("openai"));

        Assert.Equal(3, suggestions.Count);
    }

    // ── Usage parsing + running totals ───────────────────────────────────────

    [Fact]
    public void ParseOpenAiUsage_UsageFieldPresent_ReturnsPromptAndCompletionTokens()
    {
        using var doc = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":123,\"completion_tokens\":45,\"total_tokens\":168}}");
        var (prompt, completion) = AiChatService.ParseOpenAiUsage(doc.RootElement);
        Assert.Equal(123, prompt);
        Assert.Equal(45, completion);
    }

    [Fact]
    public void ParseOpenAiUsage_UsageFieldMissing_ReturnsZeroZeroRatherThanThrowing()
    {
        using var doc = JsonDocument.Parse("{\"choices\":[]}");
        var (prompt, completion) = AiChatService.ParseOpenAiUsage(doc.RootElement);
        Assert.Equal(0, prompt);
        Assert.Equal(0, completion);
    }

    [Fact]
    public void ParseAnthropicUsage_UsageFieldPresent_ReturnsInputAndOutputTokensAsPromptCompletion()
    {
        using var doc = JsonDocument.Parse("{\"usage\":{\"input_tokens\":80,\"output_tokens\":20}}");
        var (prompt, completion) = AiChatService.ParseAnthropicUsage(doc.RootElement);
        Assert.Equal(80, prompt);
        Assert.Equal(20, completion);
    }

    [Fact]
    public void ParseAnthropicUsage_UsageFieldMissing_ReturnsZeroZeroRatherThanThrowing()
    {
        using var doc = JsonDocument.Parse("{\"content\":[]}");
        var (prompt, completion) = AiChatService.ParseAnthropicUsage(doc.RootElement);
        Assert.Equal(0, prompt);
        Assert.Equal(0, completion);
    }

    [Fact]
    public async Task GetAnswerAsync_SuccessfulCall_AccumulatesRunningUsageTotalsAndFiresUsageChanged()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("hi", 100, 40));
        var service = new AiChatService(new HttpClient(handler));
        var changeCount = 0;
        service.UsageChanged += (_, _) => changeCount++;

        var result = await service.GetAnswerAsync("q", MakeConfig("openai"));

        Assert.Equal(100, result.PromptTokens);
        Assert.Equal(40, result.CompletionTokens);
        Assert.Equal(100, service.TotalPromptTokens);
        Assert.Equal(40, service.TotalCompletionTokens);
        Assert.Equal(1, service.TotalCalls);
        Assert.Equal(1, changeCount);

        // A second call accumulates on top of the first rather than replacing it.
        await service.GetAnswerAsync("q2", MakeConfig("openai"));
        Assert.Equal(200, service.TotalPromptTokens);
        Assert.Equal(80, service.TotalCompletionTokens);
        Assert.Equal(2, service.TotalCalls);
        Assert.Equal(2, changeCount);
    }

    [Fact]
    public async Task GetAnswerAsync_FailedCall_DoesNotAccumulateUsage()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "{}");
        var service = new AiChatService(new HttpClient(handler));

        await service.GetAnswerAsync("q", MakeConfig("openai"));

        Assert.Equal(0, service.TotalCalls);
        Assert.Equal(0, service.TotalPromptTokens);
    }

    [Fact]
    public async Task ResetUsage_ZeroesCountersAndFiresUsageChanged()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, OpenAiResponseJson("hi", 100, 40));
        var service = new AiChatService(new HttpClient(handler));
        await service.GetAnswerAsync("q", MakeConfig("openai"));
        var changeCount = 0;
        service.UsageChanged += (_, _) => changeCount++;

        service.ResetUsage();

        Assert.Equal(0, service.TotalPromptTokens);
        Assert.Equal(0, service.TotalCompletionTokens);
        Assert.Equal(0, service.TotalCalls);
        Assert.Equal(1, changeCount);
    }

    // ── Fake response builders ───────────────────────────────────────────────

    private static string OpenAiResponseJson(string content, int promptTokens, int completionTokens) =>
        $$"""
        {
          "choices": [{ "message": { "content": {{JsonSerializer.Serialize(content)}} } }],
          "usage": { "prompt_tokens": {{promptTokens}}, "completion_tokens": {{completionTokens}}, "total_tokens": {{promptTokens + completionTokens}} }
        }
        """;

    private static string AnthropicResponseJson(string content, int inputTokens, int outputTokens) =>
        $$"""
        {
          "content": [{ "type": "text", "text": {{JsonSerializer.Serialize(content)}} }],
          "usage": { "input_tokens": {{inputTokens}}, "output_tokens": {{outputTokens}} }
        }
        """;
}

