using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

public sealed class FoodwarehouseDatabaseTool : IAgentTool
{
    public const string ToolName = "foodwarehouse_database";

    private readonly string _apiKey;
    private readonly ILogger<FoodwarehouseDatabaseTool> _logger;

    public FoodwarehouseDatabaseTool(IOptions<AgentToolOptions> options, ILogger<FoodwarehouseDatabaseTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Read-only SQL access to foodwarehouse task database. Allowed examples: SHOW TABLES, .tables, .schema, SELECT ...",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "SQL query string. Write operations are blocked by API." }
                },
                required = new[] { "query" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var query = ParseArguments(toolCall.Function.Arguments);

        return new ToolExecutionResult(await FoodwarehouseApiClient.SendAsync(
            _apiKey,
            new
            {
                tool = "database",
                query
            },
            _logger,
            cancellationToken));
    }

    private static string ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'foodwarehouse_database' requires argument 'query'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'foodwarehouse_database' requires string argument 'query'.");
        }

        var query = queryElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Argument 'query' cannot be empty.");
        }

        return query;
    }
}
