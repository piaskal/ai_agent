using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Windpower;

public sealed class TaskVerifyApiTool : IAgentTool
{
    public const string ToolName = "task_verify_api";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const int MaxRetries = 3;
    private const int QueuePollIntervalMs = 1000;
    private const int MinQueuePollingSeconds = 10;

    private readonly string _apiKey;
    private readonly string _taskName;
    private readonly ILogger<TaskVerifyApiTool> _logger;

    public TaskVerifyApiTool(IOptions<AgentToolOptions> options, ILogger<TaskVerifyApiTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        var configuredTaskName = options.Value.TaskVerifyApiTaskName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuredTaskName))
        {
            throw new InvalidOperationException("AgentTools:TaskVerifyApiTaskName is required.");
        }

        _taskName = configuredTaskName;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Calls the ag3nts verify API using task configured in AgentTools:TaskVerifyApiTaskName. Prefer passing message_body as JSON object/array; stringified JSON is also accepted.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    message_body = new
                    {
                        description = "Body for the configured task API. Recommended: JSON object/array. Also accepts stringified JSON.",
                        oneOf = new object[]
                        {
                            new { type = "object" },
                            new { type = "array" },
                            new { type = "string" }
                        }
                    }
                },
                required = new[] { "message_body" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var messageBody = ParseArguments(toolCall.Function.Arguments);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the Wind Power API.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            apikey = _apiKey,
            task = _taskName,
            answer = messageBody
        });

        using var httpClient = new HttpClient();

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var requestContent = new StringContent(
                payload,
                Encoding.UTF8,
                "application/json");

            using var response = await httpClient.PostAsync(VerifyUrl, requestContent, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                if (!IsQueuedResponse(responseBody))
                {
                    return new ToolExecutionResult(responseBody);
                }

                var queuePollingDeadline = DateTime.UtcNow.AddSeconds(MinQueuePollingSeconds);
                while (DateTime.UtcNow < queuePollingDeadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(QueuePollIntervalMs), cancellationToken);

                    using var queueRequestContent = new StringContent(
                        payload,
                        Encoding.UTF8,
                        "application/json");
                    using var queueResponse = await httpClient.PostAsync(VerifyUrl, queueRequestContent, cancellationToken);
                    responseBody = await queueResponse.Content.ReadAsStringAsync(cancellationToken);

                    if (!queueResponse.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"Wind Power API request failed with status {(int)queueResponse.StatusCode} ({queueResponse.StatusCode}) while waiting for queued response. Response: {responseBody}");
                    }

                    if (!IsQueuedResponse(responseBody))
                    {
                        return new ToolExecutionResult(responseBody);
                    }
                }

                return new ToolExecutionResult(responseBody);
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Wind Power API request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Wind Power API returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in Wind Power tool.");
    }

    private static object? ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'task_verify_api' requires argument 'message_body'.");
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Tool 'task_verify_api' received invalid tool-call arguments JSON. If 'message_body' is large, pass it as JSON object/array instead of escaped string.",
                ex);
        }

        using (json)
        {
        var root = json.RootElement;

        if (!root.TryGetProperty("message_body", out var messageBodyElement))
        {
            throw new InvalidOperationException("Tool 'task_verify_api' requires argument 'message_body'.");
        }

        if (messageBodyElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<object>(messageBodyElement.GetRawText());
        }

        if (messageBodyElement.ValueKind != JsonValueKind.String)
        {
            return JsonSerializer.Deserialize<object>(messageBodyElement.GetRawText());
        }

        var messageBody = messageBodyElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(messageBody))
        {
            throw new InvalidOperationException("Argument 'message_body' cannot be empty.");
        }

        try
        {
            return JsonSerializer.Deserialize<object>(messageBody);
        }
        catch (JsonException)
        {
            return messageBody;
        }
        }
    }

    private static bool IsQueuedResponse(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;
            if (!root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            if (codeElement.GetInt32() != 11)
            {
                return false;
            }

            if (!root.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var message = messageElement.GetString();
            return string.Equals(
                message,
                "No completed queued response is available yet.",
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
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
