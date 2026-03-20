using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.SPK;

public sealed class RailwayApiTool : IAgentTool
{
    public const int MaxRetries = 3;
    public const string ToolName = "railway_api";

private readonly ILogger<RailwayApiTool> _logger;
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "railway";

    private readonly AgentToolOptions _toolOptions;

    public RailwayApiTool(IOptions<AgentToolOptions> options, ILogger<RailwayApiTool> logger)
    {
        _toolOptions = options.Value;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Sends an request to the railway API. use { \"action\": \"help\" } to get the list of available actions.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    body = new { type = "string", description = "JSON body to send to the railway API." }
                },
                required = new[] { "body" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var body = ParseArguments(toolCall.Function.Arguments);

        var payload = new ApiRequest(_toolOptions.ApiKey, TaskName, JsonNode.Parse(body)!.AsObject());

        using var httpClient = new HttpClient();

        string responseBody = string.Empty;
        int retries = MaxRetries;
        while (retries > 0)
        {
            using var response = await httpClient.PostAsJsonAsync(VerifyUrl, payload, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Railway API request failed with status {StatusCode}. Response: {ResponseBody}", response.StatusCode, responseBody);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    retries--;
                    response.Headers.TryGetValues("retry-after", out var retryAfterValues);
                    _logger.LogWarning("Rate limit hit. Retrying after delay. Retries left: {RetriesLeft}", retries);
                    if (retryAfterValues != null && int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfterSeconds))
                    {
                        _logger.LogInformation("Retry-After header value: {RetryAfterSeconds} seconds. Waiting before retrying.", retryAfterSeconds);
                        await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), cancellationToken);
                        continue;
                    }
                    else
                    {
                        _logger.LogWarning("No valid Retry-After header found. Using default delay before retrying.");
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Railway API request failed with status {response.StatusCode}. Response: {responseBody}");
                }
            }
            else
            {
                return new ToolExecutionResult(responseBody);
            }
        }
        return new ToolExecutionResult($"Failed to execute action '{body}' after {MaxRetries} attempts last error: {responseBody}");
    }

    private static string ParseArguments(string argumentsJson)
    {
        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Tool 'railway_api' requires string argument 'body'.");

        return bodyEl.GetString()!;
    }

    private sealed record ApiRequest(
        [property: JsonPropertyName("apikey")] string ApiKey,
        [property: JsonPropertyName("task")] string TaskName,
        [property: JsonPropertyName("answer")] JsonObject Answer);


}
