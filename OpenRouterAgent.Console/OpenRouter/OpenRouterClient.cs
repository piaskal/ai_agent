using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
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

        _httpClient.BaseAddress = new Uri(_options.GetEffectiveBaseUrl());
        _httpClient.Timeout = TimeSpan.FromSeconds(600);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.GetEffectiveApiKey());

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
        var request = new
        {
            model = _options.Model,
            input = BuildResponsesInput(messages),
            temperature = _options.Temperature,
            max_output_tokens = _options.MaxTokens,
            tools = useTools ? BuildResponsesTools(tools) : null,
            tool_choice = useTools ? "auto" : null
        };

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("OpenRouter request:\n{RequestJson}",
                JsonSerializer.Serialize(request, LogSerializerOptions));
        }

        using var response = await _httpClient.PostAsJsonAsync(
            _options.GetResponsesPath(),
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
        var toolCalls = new List<ChatToolCall>();
        int? totalTokens = null;

        if (root.TryGetProperty("usage", out var usageElement) &&
            usageElement.ValueKind == JsonValueKind.Object &&
            usageElement.TryGetProperty("total_tokens", out var totalTokensElement) &&
            totalTokensElement.ValueKind == JsonValueKind.Number)
        {
            totalTokens = totalTokensElement.GetInt32();
        }

        if (root.TryGetProperty("status", out var statusElement) &&
            statusElement.ValueKind == JsonValueKind.String)
        {
            finishReason = statusElement.GetString();
        }

        if (root.TryGetProperty("incomplete_details", out var incompleteDetailsElement) &&
            incompleteDetailsElement.ValueKind == JsonValueKind.Object &&
            incompleteDetailsElement.TryGetProperty("reason", out var incompleteReasonElement) &&
            incompleteReasonElement.ValueKind == JsonValueKind.String)
        {
            finishReason = incompleteReasonElement.GetString();
        }

        if (root.TryGetProperty("output", out var outputElement) &&
            outputElement.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();

            foreach (var outputItem in outputElement.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var outputType = typeElement.GetString();

                if (string.Equals(outputType, "message", StringComparison.OrdinalIgnoreCase))
                {
                    var text = ExtractResponseMessageContent(outputItem);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        builder.Append(text);
                    }
                    continue;
                }

                if (!string.Equals(outputType, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var functionName = GetOptionalString(outputItem, "name");
                var arguments = GetOptionalString(outputItem, "arguments");
                var callId = GetOptionalString(outputItem, "call_id") ?? GetOptionalString(outputItem, "id");

                if (string.IsNullOrWhiteSpace(functionName) || string.IsNullOrWhiteSpace(callId))
                {
                    continue;
                }

                toolCalls.Add(new ChatToolCall(
                    callId,
                    "function",
                    new ChatToolCallFunction(functionName, arguments ?? "{}")));
            }

            var textContent = builder.ToString().Trim();
            content = textContent.Length == 0 ? null : textContent;
        }

        if (string.IsNullOrWhiteSpace(content) && toolCalls.Count == 0)
        {
            if (IsTokenLimitFinishReason(finishReason))
            {
                throw new InvalidOperationException(
                    $@"Model reached the token limit before producing a final answer. Tokens consumed: {totalTokens}. Consider setting a higher MaxTokens value in the configuration (for example, 2048) or asking for a shorter response.");
            }

            throw new InvalidOperationException(
                $"OpenRouter returned an empty response (finish_reason: {finishReason ?? "n/a"}).");
        }

        return new OpenRouterCompletionResult(content, toolCalls, totalTokens);
    }

    private static object[] BuildResponsesInput(IReadOnlyList<ChatMessage> messages)
    {
        var inputItems = new List<object>(messages.Count * 2);

        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Content) &&
                !string.Equals(message.Role, ChatRoles.Tool, StringComparison.OrdinalIgnoreCase))
            {
                inputItems.Add(new
                {
                    role = message.Role,
                    content = message.Content
                });
            }

            if (string.Equals(message.Role, ChatRoles.Assistant, StringComparison.OrdinalIgnoreCase) &&
                message.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    inputItems.Add(new
                    {
                        type = "function_call",
                        call_id = toolCall.Id,
                        name = toolCall.Function.Name,
                        arguments = toolCall.Function.Arguments
                    });
                }
            }

            if (string.Equals(message.Role, ChatRoles.Tool, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                inputItems.Add(new
                {
                    type = "function_call_output",
                    call_id = message.ToolCallId,
                    output = message.Content ?? string.Empty
                });
            }
        }

        return inputItems.ToArray();
    }

    private static object[] BuildResponsesTools(IReadOnlyList<ChatToolDefinition> tools)
    {
        return tools.Select(tool => new
        {
            type = tool.Type,
            name = tool.Function.Name,
            description = tool.Function.Description,
            parameters = tool.Function.ParametersSchema
        }).Cast<object>().ToArray();
    }

    private static string? ExtractResponseMessageContent(JsonElement messageElement)
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
                part.TryGetProperty("text", out var textElement))
            {
                if (textElement.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textElement.GetString());
                }
                continue;
            }

            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String &&
                string.Equals(typeElement.GetString(), "output_text", StringComparison.OrdinalIgnoreCase) &&
                part.TryGetProperty("text", out var outputTextElement) &&
                outputTextElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(outputTextElement.GetString());
            }
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsTokenLimitFinishReason(string? finishReason)
    {
        if (string.IsNullOrWhiteSpace(finishReason))
        {
            return false;
        }

        return string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(finishReason, "max_output_tokens", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(finishReason, "max_tokens", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return propertyElement.GetString();
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