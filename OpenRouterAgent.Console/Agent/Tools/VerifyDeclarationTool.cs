using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class VerifyDeclarationTool : IAgentTool
{
    public const string ToolName = "verify_declaration";

    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string Task = "sendit";

    private readonly AgentToolOptions _toolOptions;

    public VerifyDeclarationTool(IOptions<AgentToolOptions> options)
    {
        _toolOptions = options.Value;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Sends a declaration to the verification endpoint (https://hub.ag3nts.org/verify) and returns the response.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    declaration = new { type = "string", description = "Full declaration text to verify." }
                },
                required = new[] { "declaration" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var declaration = ParseArguments(toolCall.Function.Arguments);

        var payload = new VerifyRequest(_toolOptions.ApiKey, Task, new VerifyAnswer(declaration));

        using var httpClient = new HttpClient();
        using var response = await httpClient.PostAsJsonAsync(VerifyUrl, payload, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Verification request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");

        return new ToolExecutionResult(responseBody);
    }

    private static string ParseArguments(string argumentsJson)
    {
        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("declaration", out var declarationEl) || declarationEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Tool 'verify_declaration' requires string argument 'declaration'.");

        return declarationEl.GetString()!;
    }

    private sealed record VerifyRequest(
        [property: JsonPropertyName("apikey")] string ApiKey,
        [property: JsonPropertyName("task")] string Task,
        [property: JsonPropertyName("answer")] VerifyAnswer Answer);

    private sealed record VerifyAnswer(
        [property: JsonPropertyName("declaration")] string Declaration);
}
