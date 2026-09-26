using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.OkoEditor;

public sealed class OkoEditorTool : IAgentTool
{
    public const string ToolName = "oko_editor";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "okoeditor";
    private const int MaxRetries = 3;

    private readonly string _apiKey;
    private readonly ILogger<OkoEditorTool> _logger;

    public OkoEditorTool(IOptions<AgentToolOptions> options, ILogger<OkoEditorTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Sends answer body to the Oko Editor API. To get list of commands send '{ \"action\": \"help\" }'",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    answer_body = new
                    {
                        type = "string",
                        description = "Answer body for the Oko Editor API. To get list of commands send '{ \"action\": \"help\" }'."
                    }
                },
                required = new[] { "answer_body" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var answerBody = ParseArguments(toolCall.Function.Arguments);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the Oko Editor API.");
        }

        var payload = $@"
        {{
            ""apikey"": ""{_apiKey}"",
            ""task"": ""{TaskName}"",
            ""answer"": {answerBody}
        }} ";;

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
                return new ToolExecutionResult(responseBody);
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Oko Editor API request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Oko Editor API returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in Oko Editor tool.");
    }

    private static string ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'oko_editor' requires argument 'answer_body'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("answer_body", out var answerBodyElement) || answerBodyElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'oko_editor' requires string argument 'answer_body'.");
        }

        var answerBody = answerBodyElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(answerBody))
        {
            throw new InvalidOperationException("Argument 'answer_body' cannot be empty.");
        }

        return answerBody;
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
