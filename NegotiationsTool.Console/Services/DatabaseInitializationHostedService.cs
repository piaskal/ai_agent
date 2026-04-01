namespace NegotiationsTool.Console.Services;

using NegotiationsTool.Console.Interfaces;
using Serilog;

public sealed class DatabaseInitializationHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ApplicationInitializationState _initializationState;
    private readonly ILogger _logger;

    public DatabaseInitializationHostedService(
        IServiceProvider serviceProvider,
        ApplicationInitializationState initializationState)
    {
        _serviceProvider = serviceProvider;
        _initializationState = initializationState;
        _logger = Log.ForContext<DatabaseInitializationHostedService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const string baseUrl = "https://hub.ag3nts.org/dane/s03e04_csv/";
        const string dbPath = "negotiations.db";
        var csvFiles = new[] { "cities.csv", "connections.csv", "items.csv" };

        _initializationState.MarkInitializing();
        _logger.Information("Background database initialization started");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var databaseService = scope.ServiceProvider.GetRequiredService<IDatabaseService>();
            await databaseService.InitializeAsync(dbPath, baseUrl, csvFiles);

            _initializationState.MarkReady();
            _logger.Information("Background database initialization finished successfully");
        }
        catch (Exception ex)
        {
            _initializationState.MarkFailed(ex.Message);
            _logger.Error(ex, "Background database initialization failed");
        }
    }
}