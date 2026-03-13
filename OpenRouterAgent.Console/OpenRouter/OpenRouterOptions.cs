namespace OpenRouterAgent.ConsoleApp.OpenRouter;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "openai/gpt-4o-mini";

    public string SystemPrompt { get; set; } =
        "You are a concise and reliable AI assistant. Ask clarifying questions only when necessary and prefer actionable answers.";

    public decimal Temperature { get; set; } = 0.2m;

    public int? MaxTokens { get; set; }

    public string? AppName { get; set; } = "OpenRouterAgent.Console";

    public string? AppUrl { get; set; }
}