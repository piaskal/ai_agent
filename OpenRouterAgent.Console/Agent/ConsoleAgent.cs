using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent;

public sealed class ConsoleAgent
{
    private readonly AgentService _agentService;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<ConsoleAgent> _logger;

    private const string ConsoleSessionId = "console";

    public ConsoleAgent(
        AgentService agentService,
        IOptions<OpenRouterOptions> options,
        ILogger<ConsoleAgent> logger)
    {
        _agentService = agentService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            _agentService.ResetSession(ConsoleSessionId);
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

                try
                {
                    var reply = await _agentService.ChatAsync(ConsoleSessionId, input, shutdown.Token);

                    Console.WriteLine();
                    Console.WriteLine($"agent> {reply.Reply}");
                    Console.WriteLine($"tokens> {reply.TotalTokensConsumed}");
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
                _agentService.ResetSession(ConsoleSessionId);
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

                    _agentService.ResetSession(ConsoleSessionId, newPrompt);
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