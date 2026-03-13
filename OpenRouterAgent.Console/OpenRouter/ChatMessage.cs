using System.Text.Json.Serialization;

namespace OpenRouterAgent.ConsoleApp.OpenRouter;

public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<ChatToolCall>? ToolCalls = null)
{
    public static ChatMessage System(string content) => new(ChatRoles.System, content);

    public static ChatMessage User(string content) => new(ChatRoles.User, content);

    public static ChatMessage Assistant(string content) => new(ChatRoles.Assistant, content);

    public static ChatMessage AssistantWithToolCalls(IReadOnlyList<ChatToolCall> toolCalls, string? content = null) =>
        new(ChatRoles.Assistant, content, ToolCalls: toolCalls);

    public static ChatMessage Tool(string toolCallId, string toolName, string content) =>
        new(ChatRoles.Tool, content, Name: toolName, ToolCallId: toolCallId);
}

public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

public sealed record ChatToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ChatToolCallFunction Function);

public sealed record ChatToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

public sealed record ChatToolDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ChatToolDefinitionFunction Function);

public sealed record ChatToolDefinitionFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] object ParametersSchema);