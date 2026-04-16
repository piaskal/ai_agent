using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

public sealed class FoodwarehouseOrdersTool : IAgentTool
{
    public const string ToolName = "foodwarehouse_orders";

    private static readonly HashSet<string> AllowedActions =
    [
        "get",
        "create",
        "append",
        "delete"
    ];

    private readonly string _apiKey;
    private readonly ILogger<FoodwarehouseOrdersTool> _logger;

    public FoodwarehouseOrdersTool(IOptions<AgentToolOptions> options, ILogger<FoodwarehouseOrdersTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Reads and modifies foodwarehouse orders. Actions: get, create, append, delete.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "get", "create", "append", "delete" } },
                    id = new { type = "string", description = "Order id. Optional for get, required for append/delete." },
                    title = new { type = "string", description = "Required for create." },
                    creator_id = new { type = "integer", description = "Required for create. Alias for creatorID." },
                    creatorID = new { type = "integer", description = "Required for create." },
                    destination = new { type = "integer", description = "Required for create." },
                    signature = new { type = "string", description = "Required for create." },
                    name = new { type = "string", description = "Item name (single form for append)." },
                    items = new
                    {
                        description = "For append: single quantity number OR batch object OR batch array.",
                        oneOf = new object[]
                        {
                            new { type = "integer" },
                            new { type = "number" },
                            new { type = "object" },
                            new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        name = new { type = "string" },
                                        items = new { type = "integer" }
                                    },
                                    required = new[] { "name", "items" }
                                }
                            }
                        }
                    }
                },
                required = new[] { "action" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var answer = ParseArguments(toolCall.Function.Arguments);

        var response = await FoodwarehouseApiClient.SendAsync(
            _apiKey,
            answer,
            _logger,
            cancellationToken);

        return new ToolExecutionResult(response);
    }

    private static OrdersAnswer ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'foodwarehouse_orders' requires at least argument 'action'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        var action = GetRequiredString(root, "action", ToolName).ToLowerInvariant();
        if (!AllowedActions.Contains(action))
        {
            throw new InvalidOperationException("Argument 'action' must be one of: get, create, append, delete.");
        }

        return action switch
        {
            "get" => ParseGet(root),
            "create" => ParseCreate(root),
            "append" => ParseAppend(root),
            "delete" => ParseDelete(root),
            _ => throw new InvalidOperationException("Unsupported orders action.")
        };
    }

    private static OrdersAnswer ParseGet(JsonElement root)
    {
        var id = GetOptionalString(root, "id");
        return new OrdersAnswer("orders", "get", id, null, null, null, null, null, null);
    }

    private static OrdersAnswer ParseCreate(JsonElement root)
    {
        var title = GetRequiredString(root, "title", ToolName);
        var creatorId = GetRequiredIntWithAlias(root, "creatorID", "creator_id", ToolName);
        var destination = GetRequiredInt(root, "destination", ToolName);
        var signature = GetRequiredString(root, "signature", ToolName);

        return new OrdersAnswer("orders", "create", null, title, creatorId, destination, signature, null, null);
    }

    private static OrdersAnswer ParseAppend(JsonElement root)
    {
        var id = GetRequiredString(root, "id", ToolName);
        var name = GetOptionalString(root, "name");
        var items = GetOptionalRaw(root, "items");

        if (items is null)
        {
            throw new InvalidOperationException("Action 'append' requires argument 'items'.");
        }

        var itemsValue = items.Value;

        if (itemsValue.ValueKind is JsonValueKind.Number && string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Action 'append' requires 'name' when 'items' is a single number.");
        }

        return new OrdersAnswer("orders", "append", id, null, null, null, null, name, JsonSerializer.Deserialize<object>(itemsValue.GetRawText()));
    }

    private static OrdersAnswer ParseDelete(JsonElement root)
    {
        var id = GetRequiredString(root, "id", ToolName);
        return new OrdersAnswer("orders", "delete", id, null, null, null, null, null, null);
    }

    private static string GetRequiredString(JsonElement root, string fieldName, string toolName)
    {
        if (!root.TryGetProperty(fieldName, out var valueElement) || valueElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Tool '{toolName}' requires string argument '{fieldName}'.");
        }

        var value = valueElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Argument '{fieldName}' cannot be empty.");
        }

        return value;
    }

    private static int GetRequiredIntWithAlias(JsonElement root, string primaryField, string aliasField, string toolName)
    {
        if (TryGetInt(root, primaryField, out var primaryValue))
        {
            return primaryValue;
        }

        if (TryGetInt(root, aliasField, out var aliasValue))
        {
            return aliasValue;
        }

        throw new InvalidOperationException($"Tool '{toolName}' requires integer argument '{primaryField}' (or alias '{aliasField}').");
    }

    private static int GetRequiredInt(JsonElement root, string fieldName, string toolName)
    {
        if (!TryGetInt(root, fieldName, out var value))
        {
            throw new InvalidOperationException($"Tool '{toolName}' requires integer argument '{fieldName}'.");
        }

        return value;
    }

    private static bool TryGetInt(JsonElement root, string fieldName, out int value)
    {
        value = default;
        if (!root.TryGetProperty(fieldName, out var valueElement) || valueElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return valueElement.TryGetInt32(out value);
    }

    private static string? GetOptionalString(JsonElement root, string fieldName)
    {
        if (!root.TryGetProperty(fieldName, out var valueElement) || valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (valueElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Argument '{fieldName}' must be a string when provided.");
        }

        var value = valueElement.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static JsonElement? GetOptionalRaw(JsonElement root, string fieldName)
    {
        if (!root.TryGetProperty(fieldName, out var valueElement) || valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return valueElement;
    }

    private sealed record OrdersAnswer(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("creatorID")] int? CreatorId,
        [property: JsonPropertyName("destination")] int? Destination,
        [property: JsonPropertyName("signature")] string? Signature,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("items")] object? Items);
}
