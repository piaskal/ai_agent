using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class RadiomonitoringSolveTool : IAgentTool
{
    public const string ToolName = "radiomonitoring_solve";

    private readonly RadiomonitoringOrchestrator _orchestrator;

    public RadiomonitoringSolveTool(RadiomonitoringOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Runs the radiomonitoring pipeline: start listening, route captures locally, extract the Syjon report via OpenRouter, and transmit it to Centrala. Returns the report and hub response. Do not pass raw attachments.",
            ParametersSchema: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var result = await _orchestrator.RunAsync(cancellationToken);
        return new ToolExecutionResult(result);
    }
}
