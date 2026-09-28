using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public sealed class PhonecallSolveTool : IAgentTool
{
    public const string ToolName = "phonecall_solve";

    private readonly PhonecallOrchestrator _orchestrator;

    public PhonecallSolveTool(PhonecallOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Places the phonecall to the OKO operator: introduces Tymon Gajewski, asks which of RD224, RD472 and RD820 is passable for Zygfryd's transport, requests monitoring shutdown on that road, and gives the operator password. Returns the hub flag.",
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
