using System.Text.Json.Serialization;

namespace OpenRouterAgent.ConsoleApp.OpenRouter;

internal sealed record OpenRouterChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("temperature")] decimal Temperature,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("tools")] IReadOnlyList<ChatToolDefinition>? Tools = null,
    [property: JsonPropertyName("tool_choice")] string? ToolChoice = null);

internal sealed record OpenRouterChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenRouterChoice>? Choices);

internal sealed record OpenRouterChoice(
    [property: JsonPropertyName("finish_reason")] string? FinishReason,
    [property: JsonPropertyName("message")] OpenRouterResponseMessage? Message);

internal sealed record OpenRouterResponseMessage(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<ChatToolCall>? ToolCalls);