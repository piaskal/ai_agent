using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

public sealed class FoodwarehouseHelpTool : IAgentTool
{
    public const string ToolName = "foodwarehouse_help";

    private readonly string _apiKey;
    private readonly ILogger<FoodwarehouseHelpTool> _logger;

    public FoodwarehouseHelpTool(IOptions<AgentToolOptions> options, ILogger<FoodwarehouseHelpTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Returns foodwarehouse API usage and available actions.",
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
            new { tool = "help" },
            _logger,
            cancellationToken));
    }
}
