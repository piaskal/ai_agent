using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Globalization;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class GetSuspectLocationsTool : IAgentTool
{
    public const string ToolName = "get_suspect_locations";

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves a list of suspects based on the given criteria.",
            ParametersSchema: new { type = "object", properties = new
            {
                name = new { type = "string", description = "Name of the suspect." },
                surname = new { type = "string", description = "Surname of the suspect." }
            }, required = new[] { "name", "surname" }})
            );

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Tool 'get_suspect_locations' is not implemented yet.");

        //return new ToolExecutionResult(suspects);
    }
}
