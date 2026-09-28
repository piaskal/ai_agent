using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Timetravel;

public sealed class TimetravelSolveTool : IAgentTool
{
    public const string ToolName = "timetravel_solve";

    private readonly TimetravelSolver _solver;

    public TimetravelSolveTool(TimetravelSolver solver)
    {
        _solver = solver;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Runs the CHRONOS-P1 timetravel sequence: jump to 5 November 2238 for batteries, return to the device date, then open a tunnel to 12 November 2024. Returns the flag.",
            ParametersSchema: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var flag = await _solver.SolveAsync(cancellationToken);
        return new ToolExecutionResult(flag);
    }
}
