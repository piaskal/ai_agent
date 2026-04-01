using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent;

public sealed class ConversationState
{
    private readonly List<ChatMessage> _messages = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public SemaphoreSlim Gate => _gate;

    public void Reset(string systemPrompt)
    {
        _messages.Clear();
        _messages.Add(ChatMessage.System(systemPrompt));
    }

    public void AddUserMessage(string content) => _messages.Add(ChatMessage.User(content));

    public void AddAssistantMessage(string content) => _messages.Add(ChatMessage.Assistant(content));

    public void AddAssistantToolCallMessage(IReadOnlyList<ChatToolCall> toolCalls, string? content = null) =>
        _messages.Add(ChatMessage.AssistantWithToolCalls(toolCalls, content));

    public void AddToolMessage(string toolCallId, string toolName, string content) =>
        _messages.Add(ChatMessage.Tool(toolCallId, toolName, content));

    public void RemoveLastUserMessage()
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var lastMessage = _messages[^1];
        if (lastMessage.Role.Equals(ChatRoles.User, StringComparison.OrdinalIgnoreCase))
        {
            _messages.RemoveAt(_messages.Count - 1);
        }
    }
}