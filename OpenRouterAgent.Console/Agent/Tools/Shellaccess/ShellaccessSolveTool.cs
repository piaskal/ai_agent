using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Shellaccess;

public sealed class ShellaccessSolveTool : IAgentTool
{
    public const string ToolName = "shellaccess_solve";

    private readonly ShellaccessSolver _solver;

    public ShellaccessSolveTool(ShellaccessSolver solver)
    {
        _solver = solver;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Searches the /data time archive on the shellaccess host, finds where Rafał's body was discovered, and prints the meeting point for the day before as JSON. Returns the hub flag.",
            ParametersSchema: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var result = await _solver.RunAsync(cancellationToken);
        return new ToolExecutionResult(result);
    }
}
