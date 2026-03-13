using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.Agent.Tools;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent;

public sealed class ConsoleAgent
{
    private readonly ConversationState _conversationState;
    private readonly IOpenRouterClient _openRouterClient;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<ConsoleAgent> _logger;

    public ConsoleAgent(
        ConversationState conversationState,
        IOpenRouterClient openRouterClient,
        IAgentToolRegistry toolRegistry,
        IOptions<OpenRouterOptions> options,
        ILogger<ConsoleAgent> logger)
    {
        _conversationState = conversationState;
        _openRouterClient = openRouterClient;
        _toolRegistry = toolRegistry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            _conversationState.Reset(_options.SystemPrompt);
            PrintBanner();

            while (!shutdown.IsCancellationRequested)
            {
                Console.Write("you> ");
                var input = Console.ReadLine();

                if (input is null)
                {
                    break;
                }

                var commandHandled = TryHandleCommand(input, shutdown);
                if (commandHandled)
                {
                    if (shutdown.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                _conversationState.AddUserMessage(input);

                try
                {
                    var result = await GetAssistantReplyAsync(shutdown.Token);

                    Console.WriteLine();
                    Console.WriteLine($"agent> {result.Reply}");
                    Console.WriteLine($"tokens> {result.TotalTokensConsumed}");
                    Console.WriteLine();
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "The OpenRouter request failed.");
                    Console.WriteLine();
                    Console.WriteLine($"error> {exception.Message}");
                    Console.WriteLine();
                    _conversationState.RemoveLastUserMessage();
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        }
    }

    private async Task<AssistantReplyResult> GetAssistantReplyAsync(CancellationToken cancellationToken)
    {
        const int maxToolRounds = 6;
        var tools = _toolRegistry.GetToolDefinitions();
        var totalTokensConsumed = 0;

        for (var round = 0; round < maxToolRounds; round++)
        {
            var completion = await _openRouterClient.GetCompletionAsync(
                _conversationState.Messages,
                tools,
                cancellationToken);

            totalTokensConsumed += completion.TotalTokens ?? 0;

            if (completion.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(completion.Content))
                {
                    throw new InvalidOperationException("Model response was empty.");
                }

                _conversationState.AddAssistantMessage(completion.Content);
                return new AssistantReplyResult(completion.Content, totalTokensConsumed);
            }

            _conversationState.AddAssistantToolCallMessage(completion.ToolCalls, completion.Content);

            foreach (var toolCall in completion.ToolCalls)
            {
                var toolResult = await ExecuteToolCallSafelyAsync(toolCall, cancellationToken);
                _conversationState.AddToolMessage(toolCall.Id, toolCall.Function.Name, toolResult);
            }
        }

        throw new InvalidOperationException("Exceeded the maximum tool-calling rounds.");
    }

    private sealed record AssistantReplyResult(string Reply, int TotalTokensConsumed);

    private async Task<string> ExecuteToolCallSafelyAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
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

    private bool TryHandleCommand(string input, CancellationTokenSource shutdown)
    {
        var command = input.Trim();

        if (!command.StartsWith('/'))
        {
            return false;
        }

        switch (command.ToLowerInvariant())
        {
            case "/help":
                PrintHelp();
                return true;
            case "/reset":
                _conversationState.Reset(_options.SystemPrompt);
                Console.WriteLine("conversation reset");
                return true;
            case "/exit":
            case "/quit":
                Console.WriteLine("bye");
                shutdown.Cancel();
                return true;
            default:
                if (command.StartsWith("/system ", StringComparison.OrdinalIgnoreCase))
                {
                    var newPrompt = command[8..].Trim();
                    if (string.IsNullOrWhiteSpace(newPrompt))
                    {
                        Console.WriteLine("usage: /system <prompt>");
                        return true;
                    }

                    _conversationState.Reset(newPrompt);
                    Console.WriteLine("system prompt updated and conversation reset");
                    return true;
                }

                Console.WriteLine("unknown command. use /help");
                return true;
        }
    }

    private void PrintBanner()
    {
        Console.WriteLine($"OpenRouter Agent {_options.AppName} - Model: {_options.Model}");
        Console.WriteLine("Type a prompt to chat.");
        Console.WriteLine("Commands: /help, /reset, /system <prompt>, /exit");
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("/help                Show available commands");
        Console.WriteLine("/reset               Clear the current conversation history");
        Console.WriteLine("/system <prompt>     Replace the system prompt and reset history");
        Console.WriteLine("/exit                Exit the application");
    }
}