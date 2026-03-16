using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.Agent.Tools;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent;

public sealed class ConsoleAgent
{
    private readonly AgentService _agentService;
    private readonly IAgentToolRegistry _toolRegistry;
    private readonly OpenRouterOptions _options;
    private readonly ILogger<ConsoleAgent> _logger;

    private const string ConsoleSessionId = "console";

    public ConsoleAgent(
        AgentService agentService,
        IAgentToolRegistry toolRegistry,
        IOptions<OpenRouterOptions> options,
        ILogger<ConsoleAgent> logger)
    {
        _agentService = agentService;
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
            case "/tools":
                PrintTools();
                return true;
            case "/exit":
            case "/quit":
                Console.WriteLine("bye");
                shutdown.Cancel();
                return true;
            default:
                if (command.StartsWith("/tool ", StringComparison.OrdinalIgnoreCase))
                {
                    _ = ExecuteToolDebugAsync(command[6..], shutdown.Token);
                    return true;
                }

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
        Console.WriteLine("Commands: /help, /reset, /tools, /tool <name> [json], /system <prompt>, /exit");
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("/help                     Show available commands");
        Console.WriteLine("/reset                    Clear the current conversation history");
        Console.WriteLine("/tools                    List all registered tools");
        Console.WriteLine("/tool <name> [json]       Execute a tool directly with optional JSON arguments");
        Console.WriteLine("/system <prompt>          Replace the system prompt and reset history");
        Console.WriteLine("/exit                     Exit the application");
    }

    private void PrintTools()
    {
        var definitions = _toolRegistry.GetToolDefinitions();
        if (definitions.Count == 0)
        {
            Console.WriteLine("No tools are currently registered.");
            return;
        }

        Console.WriteLine($"Registered tools ({definitions.Count}):");
        foreach (var def in definitions)
        {
            Console.WriteLine($"  {def.Function.Name,-30} {def.Function.Description}");
        }
    }

    private async Task ExecuteToolDebugAsync(string args, CancellationToken cancellationToken)
    {
        var spaceIndex = args.IndexOf(' ');
        string toolName;
        string jsonArguments;

        if (spaceIndex < 0)
        {
            toolName = args.Trim();
            jsonArguments = "{}";
        }
        else
        {
            toolName = args[..spaceIndex].Trim();
            jsonArguments = args[spaceIndex..].Trim();
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            Console.WriteLine("usage: /tool <name> [json_args]");
            return;
        }

        var toolCall = new ChatToolCall(
            Id: $"debug-{Guid.NewGuid():N}",
            Type: "function",
            Function: new ChatToolCallFunction(toolName, jsonArguments));

        _logger.LogInformation("Invoking '{ToolName}' with: {Arguments}", toolName, jsonArguments);

        try
        {
            var result = await _toolRegistry.ExecuteAsync(toolCall, cancellationToken);
            _logger.LogInformation("Result: {Result}", result.Content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool '{ToolName}' failed.", toolName);
        }

        Console.WriteLine();
    }
}