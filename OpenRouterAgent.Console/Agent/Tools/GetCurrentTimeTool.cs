using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Globalization;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class GetCurrentTimeTool : IAgentTool
{
    public const string ToolName = "get_current_time";

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Returns the current UTC time as a Unix timestamp (seconds since epoch).",
            ParametersSchema: new { type = "object", properties = new { }, required = Array.Empty<string>() }));

    public Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Task.FromResult(new ToolExecutionResult(timestamp.ToString(CultureInfo.InvariantCulture)));
    }
}
