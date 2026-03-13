using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Globalization;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class SumTwoNumbersTool : IAgentTool
{
    public const string ToolName = "sum_two_numbers";

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Calculates the sum of two numbers.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    a = new { type = "number", description = "First number." },
                    b = new { type = "number", description = "Second number." }
                },
                required = new[] { "a", "b" }
            }));

    public Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var (a, b) = ParseArguments(toolCall.Function.Arguments);
        var sum = a + b;
        return Task.FromResult(new ToolExecutionResult(sum.ToString(CultureInfo.InvariantCulture)));
    }

    private static (decimal A, decimal B) ParseArguments(string argumentsJson)
    {
        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("a", out var aElement) || aElement.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("Tool 'sum_two_numbers' requires numeric argument 'a'.");

        if (!root.TryGetProperty("b", out var bElement) || bElement.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("Tool 'sum_two_numbers' requires numeric argument 'b'.");

        return (aElement.GetDecimal(), bElement.GetDecimal());
    }
}
