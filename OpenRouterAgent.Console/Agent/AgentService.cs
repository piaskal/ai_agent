using System.Collections.Concurrent;
using System.Text;
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
        var maxToolRounds = _options.MaxToolRounds > 0 ? _options.MaxToolRounds : 20;
        var maxConsecutiveIdenticalToolCallBatches = _options.MaxConsecutiveIdenticalToolCallBatches > 0
            ? _options.MaxConsecutiveIdenticalToolCallBatches
            : 4;

        var tools = _toolRegistry.GetToolDefinitions();
        var totalTokensConsumed = 0;
        string? previousToolBatchSignature = null;
        var consecutiveIdenticalToolBatchCount = 0;

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

            var currentToolBatchSignature = BuildToolBatchSignature(completion.ToolCalls);
            if (string.Equals(currentToolBatchSignature, previousToolBatchSignature, StringComparison.Ordinal))
            {
                consecutiveIdenticalToolBatchCount++;
                if (consecutiveIdenticalToolBatchCount >= maxConsecutiveIdenticalToolCallBatches)
                {
                    throw new InvalidOperationException(
                        $"Detected repeated identical tool-call batches for {consecutiveIdenticalToolBatchCount} consecutive rounds. " +
                        $"Stopping early to avoid an infinite loop. You can tune OpenRouter:MaxConsecutiveIdenticalToolCallBatches " +
                        $"(current: {maxConsecutiveIdenticalToolCallBatches}) if needed.");
                }
            }
            else
            {
                previousToolBatchSignature = currentToolBatchSignature;
                consecutiveIdenticalToolBatchCount = 1;
            }

            state.AddAssistantToolCallMessage(completion.ToolCalls, completion.Content);

            foreach (var toolCall in completion.ToolCalls)
            {
                var toolResult = await ExecuteToolCallSafelyAsync(toolCall, cancellationToken);
                state.AddToolMessage(toolCall.Id, toolCall.Function.Name, toolResult);
            }
        }

        throw new InvalidOperationException(
            $"Exceeded the maximum tool-calling rounds ({maxToolRounds}). " +
            "Increase OpenRouter:MaxToolRounds or adjust tool behavior/prompt to reduce loops.");
    }

    private static string BuildToolBatchSignature(IReadOnlyList<ChatToolCall> toolCalls)
    {
        var signatureBuilder = new StringBuilder();
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var toolCall = toolCalls[i];
            signatureBuilder.Append(toolCall.Function.Name);
            signatureBuilder.Append('\n');
            signatureBuilder.Append(toolCall.Function.Arguments);
            signatureBuilder.Append('\u001e');
        }

        return signatureBuilder.ToString();
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
