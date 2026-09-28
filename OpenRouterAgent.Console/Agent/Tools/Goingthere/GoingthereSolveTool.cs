using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Goingthere;

public sealed class GoingthereSolveTool : IAgentTool
{
    public const string ToolName = "goingthere_solve";

    private readonly GoingthereSolver _solver;

    public GoingthereSolveTool(GoingthereSolver solver)
    {
        _solver = solver;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Flies the goingthere rocket to the Grudziądz base. Scans for OKO radar locks, disarms them, and dodges rocks described by the radio hints. Returns the hub flag.",
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
