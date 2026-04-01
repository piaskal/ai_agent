using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.SaveThem;

public sealed class SaveThemApiTool : IAgentTool
{
    public const string ToolName = "generic_tools_api";
    private const string BaseUrl = "https://hub.ag3nts.org";
    private const string DefaultEndpointPath = "api/toolsearch";
    private const int MaxRetries = 3;

    private readonly string _apiKey;
    private readonly ILogger<SaveThemApiTool> _logger;

    public SaveThemApiTool(IOptions<AgentToolOptions> options, ILogger<SaveThemApiTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Calls tool endpoints. Provide api/toolname (or /api/toolsearch by default). If endpoint is not provided, api/toolsearch is used.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    query = new
                    {
                        type = "string",
                        description = "Required query sent to the selected tool endpoint."
                    },
                    endpoint = new
                    {
                        type = "string",
                        description = "Optional endpoint path in form api/toolname (or /api/toolname). If omitted, defaults to api/toolsearch."
                    }
                },
                required = new[] { "query" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var args = ParseArguments(toolCall.Function.Arguments);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call SaveThem endpoints.");
        }

        using var httpClient = new HttpClient();

        var endpoint = NormalizeEndpoint(args.Endpoint ?? DefaultEndpointPath);

        var body = new
        {
            apikey = _apiKey,
            query = args.Query
        };

        var apiResponse = await PostWithRetriesAsync(httpClient, endpoint!, body, cancellationToken);

        var result = new JsonObject
        {
            ["endpoint"] = endpoint,
            ["query"] = args.Query,
            ["response"] = TryParseJsonNode(apiResponse) ?? apiResponse
        };

        return new ToolExecutionResult(result.ToJsonString());
    }

    private async Task<string> PostWithRetriesAsync(HttpClient httpClient, string endpoint, object body, CancellationToken cancellationToken)
    {
        string responseBody = string.Empty;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var response = await httpClient.PostAsJsonAsync(endpoint, body, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogDebug(
                "SaveThem response from {Endpoint} with status {StatusCode}: {ResponseBody}",
                endpoint,
                (int)response.StatusCode,
                responseBody);

            if (response.IsSuccessStatusCode)
            {
                return responseBody;
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"SaveThem endpoint call failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "SaveThem endpoint returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in save_them_api tool.");
    }

    private static (string Query, string? Endpoint) ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'save_them_api' requires argument 'query'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("query", out var queryElement) || queryElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'save_them_api' requires string argument 'query'.");
        }

        var query = queryElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("Argument 'query' cannot be empty.");
        }

        string? endpoint = null;
        if (root.TryGetProperty("endpoint", out var endpointElement) && endpointElement.ValueKind == JsonValueKind.String)
        {
            endpoint = endpointElement.GetString()?.Trim();
        }

        return (query, endpoint);
    }


    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            return $"{BaseUrl}/{trimmed}";
        }

        if (trimmed.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return $"{BaseUrl}{trimmed}";
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.Equals(uri.Host, "hub.ag3nts.org", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        throw new InvalidOperationException(
            "Endpoint must be in form 'api/toolname' (or '/api/toolname') and is always called under https://hub.ag3nts.org.");
    }

    private static JsonNode? TryParseJsonNode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
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