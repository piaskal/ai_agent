namespace OpenRouterAgent.ConsoleApp.OpenRouter;

public sealed record OpenRouterCompletionResult(
    string? Content,
    IReadOnlyList<ChatToolCall> ToolCalls,
    int? TotalTokens = null);