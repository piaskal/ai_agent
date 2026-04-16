using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

public sealed class FoodwarehouseDoneTool : IAgentTool
{
    public const string ToolName = "foodwarehouse_done";

    private readonly string _apiKey;
    private readonly ILogger<FoodwarehouseDoneTool> _logger;

    public FoodwarehouseDoneTool(IOptions<AgentToolOptions> options, ILogger<FoodwarehouseDoneTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Validates whether one order fully satisfies all city needs and returns the flag on success.",
            ParametersSchema: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        return new ToolExecutionResult(await FoodwarehouseApiClient.SendAsync(
            _apiKey,
            new { tool = "done" },
            _logger,
            cancellationToken));
    }
}
