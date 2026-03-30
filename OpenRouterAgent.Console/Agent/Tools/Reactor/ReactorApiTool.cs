using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Reactor;

public sealed class ReactorApiTool : IAgentTool
{
    public const string ToolName = "reactor_game_api";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "reactor";
    private const int MaxRetries = 3;

    private static readonly HashSet<string> AllowedCommands =
    [
        "start",
        "reset",
        "left",
        "wait",
        "right"
    ];

    private readonly string _apiKey;
    private readonly ILogger<ReactorApiTool> _logger;

    public ReactorApiTool(IOptions<AgentToolOptions> options, ILogger<ReactorApiTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Sends one turn command to the reactor game API. Allowed commands: start, reset, left, wait, right. Return a JSON representation of the game state after the command is executed. The map symbols are: \nP - current position of the player (robot),\nG - goal that the player needs to reach,\nB - reactor blocks,\n. - empty fields (a dot means there is nothing on that field).",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    command = new
                    {
                        type = "string",
                        description = "Single command for the player (robot) for this turn: start, reset, left, wait, or right."
                    }
                },
                required = new[] { "command" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var command = ParseArguments(toolCall.Function.Arguments);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the reactor API.");
        }

        var payload = new
        {
            apikey = _apiKey,
            task = TaskName,
            answer = new
            {
                command
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
                    $"Reactor API request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Reactor API returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in reactor_api tool.");
    }

    private static string ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'reactor_api' requires argument 'command'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'reactor_api' requires string argument 'command'.");
        }

        var command = commandElement.GetString()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Argument 'command' cannot be empty.");
        }

        if (!AllowedCommands.Contains(command))
        {
            throw new InvalidOperationException(
                $"Unsupported reactor command '{command}'. Allowed commands: {string.Join(", ", AllowedCommands.OrderBy(c => c))}.");
        }

        return command;
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
