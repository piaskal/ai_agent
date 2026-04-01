using Serilog;
using NegotiationsTool.Console.Interfaces;
using NegotiationsTool.Console.Models;
using NegotiationsTool.Console.Services;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(config)
    .CreateLogger();

try
{
    Log.Information("NegotiationsTool.Console application started");

    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders().AddSerilog();

    // Register services
    builder.Services.AddHttpClient<ICsvDownloadService, CsvDownloadService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<IDatabaseService, DatabaseService>();
    builder.Services.AddScoped<IQueryService, QueryService>();
    builder.Services.AddSingleton<ApplicationInitializationState>();
    builder.Services.AddHostedService<DatabaseInitializationHostedService>();
    builder.Services.AddHostedService<ConsoleQueryHostedService>();

    var app = builder.Build();

    // POST endpoint to query data
    app.MapPost("/query", async (QueryRequest request, IQueryService queryService) =>
    {
        var response = await queryService.ProcessQueryAsync(request);
        return Results.Ok(response);
    });

    // GET endpoint for health check
    app.MapGet("/health", (ApplicationInitializationState initializationState) =>
        Results.Ok(new
        {
            status = initializationState.IsReady ? "ok" : initializationState.IsInitializing ? "initializing" : "error",
            isReady = initializationState.IsReady,
            error = initializationState.ErrorMessage
        }));

    // GET endpoint for health check
    app.MapGet("/", (ApplicationInitializationState initializationState) =>
        Results.Ok(new
        {
            status = initializationState.IsReady ? "ok" : initializationState.IsInitializing ? "initializing" : "error",
            isReady = initializationState.IsReady,
            error = initializationState.ErrorMessage
        }));        

    var listeningUrl = "http://localhost:5000";
    Log.Information("Starting HTTP server on {Url}", listeningUrl);
    Console.WriteLine("\n");
    Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  NegotiationsTool Service Started                          ║");
    Console.WriteLine($"║  Listening at: {listeningUrl,-41} ║");
    Console.WriteLine("║                                                            ║");
    Console.WriteLine("║  Startup mode: background data initialization              ║");
    Console.WriteLine("║  Query endpoint returns a status message until ready       ║");
    Console.WriteLine("║  Console mode: type keywords or 'help' in terminal         ║");
    Console.WriteLine("║  Type 'exit' to stop the service                            ║");
    Console.WriteLine("║                                                            ║");
    Console.WriteLine("║  Available Endpoints:                                      ║");
    Console.WriteLine("║    POST   /query   - Query CSV data                        ║");
    Console.WriteLine("║    GET    /health  - Health check                          ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
    Console.WriteLine("\n");
    
    await app.RunAsync(listeningUrl);

    Log.Information("NegotiationsTool.Console application finished");
}
catch (Exception ex)
{
    Log.Fatal(ex, "An unhandled exception occurred");
}
finally
{
    Log.CloseAndFlush();
}

