using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class GetCurrentTimeToolRegistry : IAgentToolRegistry
{
    private const string ToolName = "get_current_time";

    private static readonly IReadOnlyList<ChatToolDefinition> Tools =
    [
        new ChatToolDefinition(
            Type: "function",
            Function: new ChatToolDefinitionFunction(
                Name: ToolName,
                Description: "Returns the current UTC time as a Unix timestamp (seconds since epoch).",
                ParametersSchema: new { type = "object", properties = new { }, required = Array.Empty<string>() }))
    ];

    public IReadOnlyList<ChatToolDefinition> GetToolDefinitions() => Tools;

    public Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        if (toolCall.Function.Name != ToolName)
            throw new InvalidOperationException($"Unknown tool '{toolCall.Function.Name}'.");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Task.FromResult(new ToolExecutionResult(timestamp.ToString()));
    }
}
