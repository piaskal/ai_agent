namespace OpenRouterAgent.ConsoleApp.OpenRouter;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";
    public const string ProviderOpenRouter = "openrouter";
    public const string ProviderLlmRouter = "llmrouter";

    public string Provider { get; set; } = ProviderLlmRouter;

    public string BaseUrl { get; set; } = string.Empty;

    public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/";

    public string LlmRouterBaseUrl { get; set; } = "https://llmrouter.gft.com/openai/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public string LlmRouterApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "openai/gpt-4o-mini";

    public string ToolModel { get; set; } = "openrouter/free";

    public string SystemPrompt { get; set; } =
        "You are a concise and reliable AI assistant. Ask clarifying questions only when necessary and prefer actionable answers.";

    public decimal? Temperature { get; set; } = null;

    public int? MaxTokens { get; set; }

    public string? AppName { get; set; } = "OpenRouterAgent.Console";

    public string? AppUrl { get; set; }

    public string GetNormalizedProvider()
    {
        return (Provider ?? string.Empty).Trim().ToLowerInvariant();
    }

    public bool IsProviderValid()
    {
        var provider = GetNormalizedProvider();
        return provider is ProviderOpenRouter or ProviderLlmRouter;
    }

    public bool UseLlmRouter()
    {
        return GetNormalizedProvider() != ProviderOpenRouter;
    }

    public string GetEffectiveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            return BaseUrl;
        }

        return UseLlmRouter() ? LlmRouterBaseUrl : OpenRouterBaseUrl;
    }

    public string GetEffectiveApiKey()
    {
        return UseLlmRouter() ? LlmRouterApiKey : ApiKey;
    }

    public string GetResponsesPath()
    {
        return UseLlmRouter() ? "responses" : "api/v1/responses";
    }
}