using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.Agent.Tools;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent;

public sealed class AgentService
{
    private readonly ConcurrentDictionary<string, ConversationState> _sessions = new(StringComparer.Ordinal);
    private readonly IOpenRouterClient _openRouterClient;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        IOpenRouterClient openRouterClient,
        IAgentToolRegistry toolRegistry,
        IOptions<OpenRouterOptions> options,
        ILogger<AgentService> logger)
    {
        _openRouterClient = openRouterClient;
        _toolRegistry = toolRegistry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AgentReply> ChatAsync(string sessionId, string message, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreateSession(sessionId);
        await state.Gate.WaitAsync(cancellationToken);

        try
        {
            state.AddUserMessage(message);
            return await GetAssistantReplyAsync(state, cancellationToken);
        }
        catch
        {
            state.RemoveLastUserMessage();
            throw;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public void ResetSession(string sessionId, string? systemPrompt = null)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public string CreateSession()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        GetOrCreateSession(sessionId);
        return sessionId;
    }

    private ConversationState GetOrCreateSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ =>
        {
            var state = new ConversationState();
            state.Reset(_options.SystemPrompt);
            return state;
        });
    }

    private async Task<AgentReply> GetAssistantReplyAsync(ConversationState state, CancellationToken cancellationToken)
    {
        const int maxToolRounds = 20;
        var tools = _toolRegistry.GetToolDefinitions();
        var totalTokensConsumed = 0;

        for (var round = 0; round < maxToolRounds; round++)
        {
            var completion = await _openRouterClient.GetCompletionAsync(
                state.Messages,
                tools,
                cancellationToken);

            totalTokensConsumed += completion.TotalTokens ?? 0;

            if (completion.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(completion.Content))
                    throw new InvalidOperationException("Model response was empty.");

                state.AddAssistantMessage(completion.Content);
                return new AgentReply(completion.Content, totalTokensConsumed);
            }

            state.AddAssistantToolCallMessage(completion.ToolCalls, completion.Content);

            foreach (var toolCall in completion.ToolCalls)
            {
                var toolResult = await ExecuteToolCallSafelyAsync(toolCall, cancellationToken);
                state.AddToolMessage(toolCall.Id, toolCall.Function.Name, toolResult);
            }
        }

        throw new InvalidOperationException("Exceeded the maximum tool-calling rounds.");
    }

    private async Task<string> ExecuteToolCallSafelyAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Executing tool call '{ToolName}' (id: {ToolCallId}) with arguments: {ToolArguments}",
                toolCall.Function.Name,
                toolCall.Id,
                toolCall.Function.Arguments);

            var result = await _toolRegistry.ExecuteAsync(toolCall, cancellationToken);
            return result.Content;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tool execution failed for '{ToolName}'.", toolCall.Function.Name);
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"Tool '{toolCall.Function.Name}' failed: {exception.Message}"
            });
        }
    }
}

public sealed record AgentReply(string Reply, int TotalTokensConsumed);
