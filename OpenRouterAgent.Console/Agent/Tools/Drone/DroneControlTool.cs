using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Drone;

public sealed class DroneControlTool : IAgentTool
{
    public const string ToolName = "drone_api";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "drone";
    private const int MaxRetries = 3;

    private readonly string _apiKey;
    private readonly ILogger<DroneControlTool> _logger;

    public DroneControlTool(IOptions<AgentToolOptions> options, ILogger<DroneControlTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Controls the DRN-BMB7 drone API at hub.ag3nts.org using an ordered list of instruction strings.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    instructions = new
                    {
                        type = "array",
                        description = "Ordered list of drone instructions, e.g. ['selfCheck', 'setName(Fox 21)', 'getConfig'].",
                        items = new { type = "string" },
                        minItems = 1
                    }
                },
                required = new[] { "instructions" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var instructions = ParseArguments(toolCall.Function.Arguments);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the drone API.");
        }

        var payload = new
        {
            apikey = _apiKey,
            task = TaskName,
            answer = new
            {
                instructions
            }
        };

        using var httpClient = new HttpClient();

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var requestContent = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await httpClient.PostAsync(VerifyUrl, requestContent, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new ToolExecutionResult(responseBody);
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Drone API request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Drone API returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in drone_api tool.");
    }

    private static string[] ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'drone_api' requires argument 'instructions'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("instructions", out var instructionsElement) || instructionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Tool 'drone_api' requires array argument 'instructions'.");
        }

        var instructions = new List<string>();
        foreach (var item in instructionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                instructions.Add(value);
            }
        }

        if (instructions.Count == 0)
        {
            throw new InvalidOperationException("Argument 'instructions' must contain at least one non-empty instruction string.");
        }

        return instructions.ToArray();
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues))
        {
            var retryAfter = retryAfterValues.FirstOrDefault();
            if (int.TryParse(retryAfter, out var retryAfterSeconds) && retryAfterSeconds > 0)
            {
                return TimeSpan.FromSeconds(retryAfterSeconds);
            }
        }

        return TimeSpan.FromSeconds(5 * Math.Min(2 * attempt, 10));
    }
}