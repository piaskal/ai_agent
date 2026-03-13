using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public interface IAgentToolRegistry
{
    IReadOnlyList<ChatToolDefinition> GetToolDefinitions();

    Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default);
}

public sealed record ToolExecutionResult(string Content);