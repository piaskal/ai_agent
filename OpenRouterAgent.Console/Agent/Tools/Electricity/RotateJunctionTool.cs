using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Electricity;

public sealed class RotateTileTool : IAgentTool
{
    public const string ToolName = "rotate_tile";

    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "electricity";

    private readonly AgentToolOptions _toolOptions;

    private readonly ILogger<RotateTileTool> _logger;

    public RotateTileTool(IOptions<AgentToolOptions> options, ILogger<RotateTileTool> logger)
    {
        _toolOptions = options.Value;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Rotates a tile at the given grid coordinates. Coordinates must be in 'Row x Column' format (e.g. '1x2' or '3x2'). Tiles can only be rotated 90 degrees clockwise, the same coordinate can be used to rotate the same tile multiple times.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    coordinate = new
                    {
                        type = "string",
                        description = "Tile coordinate in 'Row x Column' format, e.g. '1x2' or '3x2'."
                    }
                },
                required = new[] { "coordinate" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var coordinate = ParseArguments(toolCall.Function.Arguments);

        _logger.LogInformation("Rotating tile at coordinate '{Coordinate}'.", coordinate);

        var payload = new RotateRequest(_toolOptions.ApiKey, TaskName, new RotateAnswer(coordinate));

        using var httpClient = new HttpClient();
        using var response = await httpClient.PostAsJsonAsync(VerifyUrl, payload, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Rotate tile at '{Coordinate}' failed with status {StatusCode}. Response: {ResponseBody}",
                coordinate, (int)response.StatusCode, responseBody);
            throw new HttpRequestException(
                $"Rotate tile request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
        }

        _logger.LogDebug("Rotate tile at '{Coordinate}' succeeded. Response: {ResponseBody}", coordinate, responseBody);

        return new ToolExecutionResult(responseBody);
    }

    private static string ParseArguments(string argumentsJson)
    {
        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("coordinate", out var coordinateEl) || coordinateEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Tool 'rotate_tile' requires string argument 'coordinate'.");

        var coordinate = coordinateEl.GetString()!;

        if (!System.Text.RegularExpressions.Regex.IsMatch(coordinate, @"^\d+x\d+$"))
            throw new InvalidOperationException($"Tool 'rotate_tile' argument 'coordinate' must be in 'AxB' format (e.g. '1x2'). Got: '{coordinate}'.");

        return coordinate;
    }

    private sealed record RotateRequest(
        [property: JsonPropertyName("apikey")] string ApiKey,
        [property: JsonPropertyName("task")] string Task,
        [property: JsonPropertyName("answer")] RotateAnswer Answer);

    private sealed record RotateAnswer(
        [property: JsonPropertyName("rotate")] string Rotate);
}
