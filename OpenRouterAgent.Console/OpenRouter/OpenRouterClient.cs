using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.OpenRouter;

public sealed class OpenRouterClient : IOpenRouterClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions LogSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<OpenRouterClient> _logger;

    public OpenRouterClient(HttpClient httpClient, IOptions<OpenRouterOptions> options, ILogger<OpenRouterClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        if (!string.IsNullOrWhiteSpace(_options.AppUrl))
        {
            _httpClient.DefaultRequestHeaders.Remove("HTTP-Referer");
            _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", _options.AppUrl);
        }

        if (!string.IsNullOrWhiteSpace(_options.AppName))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-Title");
            _httpClient.DefaultRequestHeaders.Add("X-Title", _options.AppName);
        }
    }

    public async Task<OpenRouterCompletionResult> GetCompletionAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var useTools = tools.Count > 0;
        var request = new OpenRouterChatRequest(
            _options.Model,
            messages,
            _options.Temperature,
            _options.MaxTokens,
            useTools ? tools : null,
            useTools ? "auto" : null);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("OpenRouter request:\n{RequestJson}",
                JsonSerializer.Serialize(request, LogSerializerOptions));
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "api/v1/chat/completions",
            request,
            SerializerOptions,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("OpenRouter response:\n{ResponseJson}",
                FormatJson(responseBody));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenRouter request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}");
        }

        using var json = JsonDocument.Parse(responseBody);
        var root = json.RootElement;

        string? finishReason = null;
        string? content = null;
        IReadOnlyList<ChatToolCall> toolCalls = [];
        int? totalTokens = null;

        if (root.TryGetProperty("usage", out var usageElement) &&
            usageElement.ValueKind == JsonValueKind.Object &&
            usageElement.TryGetProperty("total_tokens", out var totalTokensElement) &&
            totalTokensElement.ValueKind == JsonValueKind.Number)
        {
            totalTokens = totalTokensElement.GetInt32();
        }

        if (root.TryGetProperty("choices", out var choicesElement) &&
            choicesElement.ValueKind == JsonValueKind.Array &&
            choicesElement.GetArrayLength() > 0)
        {
            var choice = choicesElement[0];

            if (choice.TryGetProperty("finish_reason", out var finishReasonElement) &&
                finishReasonElement.ValueKind == JsonValueKind.String)
            {
                finishReason = finishReasonElement.GetString();
            }

            if (choice.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.Object)
            {
                content = ExtractMessageContent(messageElement);

                if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement) &&
                    toolCallsElement.ValueKind == JsonValueKind.Array)
                {
                    toolCalls = JsonSerializer.Deserialize<List<ChatToolCall>>(
                        toolCallsElement.GetRawText(),
                        SerializerOptions) ?? [];
                }
            }
        }

        if (string.IsNullOrWhiteSpace(content) && toolCalls.Count == 0)
        {
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $@"Model reached the token limit before producing a final answer. Tokens consumed: {totalTokens}. Consider setting a higher MaxTokens value in the configuration (for example, 2048) or asking for a shorter response.");
            }

            throw new InvalidOperationException(
                $"OpenRouter returned an empty response (finish_reason: {finishReason ?? "n/a"}).");
        }

        return new OpenRouterCompletionResult(content, toolCalls, totalTokens);
    }

    private static string? ExtractMessageContent(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("content", out var contentElement))
        {
            return null;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString()?.Trim();
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var part in contentElement.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }

            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(textElement.GetString());
            }
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static string FormatJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, LogSerializerOptions);
        }
        catch
        {
            return json;
        }
    }
}