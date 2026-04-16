using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

public sealed class FoodwarehouseResetTool : IAgentTool
{
    public const string ToolName = "foodwarehouse_reset";

    private readonly string _apiKey;
    private readonly ILogger<FoodwarehouseResetTool> _logger;

    public FoodwarehouseResetTool(IOptions<AgentToolOptions> options, ILogger<FoodwarehouseResetTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Restores the user state to the initial set of seeded orders.",
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
            new { tool = "reset" },
            _logger,
            cancellationToken));
    }
}
