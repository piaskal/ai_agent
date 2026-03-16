using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Globalization;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.FindHim;

public sealed class GetSuspectsTool : IAgentTool
{
    public const string ToolName = "get_suspects";

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves a list of suspects based on the given criteria.",
            ParametersSchema: new { type = "object", properties = new { }, required = Array.Empty<string>() })
            );

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var suspects = await File.ReadAllTextAsync("data_files/suspects.json", cancellationToken);

        return new ToolExecutionResult(suspects);
    }
}
