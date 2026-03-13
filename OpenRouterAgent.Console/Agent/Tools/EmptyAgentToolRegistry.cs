using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class EmptyAgentToolRegistry : IAgentToolRegistry
{
    private static readonly IReadOnlyList<ChatToolDefinition> NoTools = [];

    public IReadOnlyList<ChatToolDefinition> GetToolDefinitions() => NoTools;

    public Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            $"Tool '{toolCall.Function.Name}' is not available. Register tools in the tool registry first.");
    }
}