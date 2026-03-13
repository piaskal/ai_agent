namespace OpenRouterAgent.ConsoleApp.OpenRouter;

public interface IOpenRouterClient
{
    Task<OpenRouterCompletionResult> GetCompletionAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken cancellationToken = default);
}