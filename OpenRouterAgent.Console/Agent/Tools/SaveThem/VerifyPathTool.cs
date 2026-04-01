using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.SaveThem;

public sealed class VerifyPathTool : IAgentTool
{
    public const string ToolName = "verify_path";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "savethem";
    private const int MaxRetries = 3;

    private static readonly HashSet<string> AllowedPathSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        "left",
        "right",
        "up",
        "down",
        "dismount"
    };

    private readonly string _apiKey;
    private readonly ILogger<VerifyPathTool> _logger;

    public VerifyPathTool(IOptions<AgentToolOptions> options, ILogger<VerifyPathTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Verifies vehicle route.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    vehicleType = new
                    {
                        type = "string",
                        description = "Vehicle type/name."
                    },
                    path = new
                    {
                        type = "array",
                        description = "Ordered path steps. Allowed values: left, right, up, down, and optional single dismount.",
                        items = new { type = "string" },
                        minItems = 1
                    }
                },
                required = new[] { "vehicleType", "path" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var args = ParseArguments(toolCall.Function.Arguments);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the verify endpoint.");
        }

        var answer = new List<string> { args.VehicleType };
        answer.AddRange(args.Path);

        var payload = new
        {
            apikey = _apiKey,
            task = TaskName,
            answer = answer.ToArray()
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

            _logger.LogDebug(
                "SaveThem verify response with status {StatusCode}: {ResponseBody}",
                (int)response.StatusCode,
                responseBody);

            if (response.IsSuccessStatusCode)
            {
                return new ToolExecutionResult(responseBody);
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"SaveThem verify request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "SaveThem verify returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in verify_path tool.");
    }

    private static (string VehicleType, string[] Path) ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'verify_path' requires arguments 'vehicleType' and 'path'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("vehicleType", out var vehicleTypeElement) || vehicleTypeElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'verify_path' requires string argument 'vehicleType'.");
        }

        var vehicleType = vehicleTypeElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            throw new InvalidOperationException("Argument 'vehicleType' cannot be empty.");
        }

        if (!root.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Tool 'verify_path' requires array argument 'path'.");
        }

        var path = new List<string>();
        var dismountCount = 0;

        foreach (var item in pathElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var step = item.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(step))
            {
                continue;
            }

            if (!AllowedPathSteps.Contains(step))
            {
                throw new InvalidOperationException(
                    $"Invalid path step '{step}'. Allowed values are left, right, up, down, and optional dismount.");
            }

            var normalizedStep = step.ToLowerInvariant();
            if (normalizedStep == "dismount")
            {
                dismountCount++;
                if (dismountCount > 1)
                {
                    throw new InvalidOperationException("Path can contain 'dismount' at most once.");
                }
            }

            path.Add(normalizedStep);
        }

        if (path.Count == 0)
        {
            throw new InvalidOperationException("Argument 'path' must contain at least one valid direction step.");
        }

        return (vehicleType, path.ToArray());
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
