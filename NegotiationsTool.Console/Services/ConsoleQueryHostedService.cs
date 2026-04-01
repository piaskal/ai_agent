namespace NegotiationsTool.Console.Services;

using Microsoft.Extensions.Hosting;
using NegotiationsTool.Console.Interfaces;
using NegotiationsTool.Console.Models;
using Serilog;

public sealed class ConsoleQueryHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger _logger;

    public ConsoleQueryHostedService(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime)
    {
        _scopeFactory = scopeFactory;
        _applicationLifetime = applicationLifetime;
        _logger = Log.ForContext<ConsoleQueryHostedService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        System.Console.WriteLine("[Console Query] Ready. Type keywords separated by spaces or 'help'. Type 'exit' to stop.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                System.Console.Write("query> ");
                var input = await Task.Run(System.Console.ReadLine);

                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                var command = input.Trim();
                if (command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    command.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    System.Console.WriteLine("[Console Query] Stopping service...");
                    _applicationLifetime.StopApplication();
                    return;
                }

                using var scope = _scopeFactory.CreateScope();
                var queryService = scope.ServiceProvider.GetRequiredService<IQueryService>();

                var response = await queryService.ProcessQueryAsync(new QueryRequest { Params = command });
                System.Console.WriteLine(response.Output);
                System.Console.WriteLine();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.Error(ex, "Error while processing console query");
                System.Console.WriteLine($"[Console Query] Error: {ex.Message}");
            }
        }
    }
}
