using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

public sealed class FoodwarehouseSignatureGeneratorTool : IAgentTool
{
    public const string ToolName = "foodwarehouse_signature_generator";

    private readonly string _apiKey;
    private readonly ILogger<FoodwarehouseSignatureGeneratorTool> _logger;

    public FoodwarehouseSignatureGeneratorTool(IOptions<AgentToolOptions> options, ILogger<FoodwarehouseSignatureGeneratorTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Generates signature for a user and destination in foodwarehouse.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    login = new { type = "string", description = "User login that creates order." },
                    birthday = new { type = "string", description = "Birthday in YYYY-MM-DD format." },
                    destination = new { type = "integer", description = "Numeric city destination code." }
                },
                required = new[] { "login", "birthday", "destination" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var args = ParseArguments(toolCall.Function.Arguments);

        return new ToolExecutionResult(await FoodwarehouseApiClient.SendAsync(
            _apiKey,
            new
            {
                tool = "signatureGenerator",
                action = "generate",
                login = args.Login,
                birthday = args.Birthday,
                destination = args.Destination
            },
            _logger,
            cancellationToken));
    }

    private static SignatureArgs ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'foodwarehouse_signature_generator' requires arguments login, birthday, and destination.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        var login = GetRequiredString(root, "login", ToolName);
        var birthday = GetRequiredString(root, "birthday", ToolName);
        var destination = GetRequiredInt(root, "destination", ToolName);

        return new SignatureArgs(login, birthday, destination);
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

    private static int GetRequiredInt(JsonElement root, string fieldName, string toolName)
    {
        if (!root.TryGetProperty(fieldName, out var valueElement) || valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetInt32(out var value))
        {
            throw new InvalidOperationException($"Tool '{toolName}' requires integer argument '{fieldName}'.");
        }

        return value;
    }

    private sealed record SignatureArgs(string Login, string Birthday, int Destination);
}
