using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.Agent;
using OpenRouterAgent.ConsoleApp.Agent.Tools;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using Serilog;

var isServeMode = args.Contains("--serve", StringComparer.OrdinalIgnoreCase);
var configArgs = args.Where(a => !a.Equals("--serve", StringComparison.OrdinalIgnoreCase)).ToArray();

try
{
	var builder = WebApplication.CreateBuilder(configArgs);
	builder.Configuration.AddUserSecrets<Program>(optional: true);
	builder.Services.AddSerilog((services, loggerConfig) =>
		loggerConfig.ReadFrom.Configuration(builder.Configuration));
	builder.Configuration.AddCommandLine(
		configArgs,
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["--model"] = "OpenRouter:Model",
			["-m"] = "OpenRouter:Model"
		});

	builder.Services
		.AddOptions<OpenRouterOptions>()
		.Bind(builder.Configuration.GetSection(OpenRouterOptions.SectionName))
		.Validate(
			options => !string.IsNullOrWhiteSpace(options.ApiKey),
			$"Configuration value '{OpenRouterOptions.SectionName}:ApiKey' is required. Use appsettings, user secrets, or OPENROUTER__APIKEY.")
		.Validate(
			options => !string.IsNullOrWhiteSpace(options.Model),
			$"Configuration value '{OpenRouterOptions.SectionName}:Model' is required.")
		.ValidateOnStart();

	builder.Services.AddHttpClient<IOpenRouterClient, OpenRouterClient>();
	builder.Services.AddSingleton<IAgentToolRegistry, GetCurrentTimeToolRegistry>();
	builder.Services.AddSingleton<AgentService>();
	builder.Services.AddSingleton<ConsoleAgent>();

	var app = builder.Build();

	if (isServeMode)
	{
		app.MapPost("/chat", async (ChatRequest req, AgentService agentService, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(req.Message))
				return Results.BadRequest("Message is required.");

			var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
				? agentService.CreateSession()
				: req.SessionId;

			try
			{
				var reply = await agentService.ChatAsync(sessionId, req.Message, ct);
				return Results.Ok(new ChatResponse(sessionId, reply.Reply, reply.TotalTokensConsumed));
			}
			catch (OperationCanceledException)
			{
				return Results.StatusCode(499);
			}
			catch (Exception ex)
			{
				return Results.Problem(ex.Message, statusCode: 500);
			}
		});

		app.MapDelete("/chat/{sessionId}", (string sessionId, AgentService agentService) =>
		{
			agentService.ResetSession(sessionId);
			return Results.NoContent();
		});

		app.Lifetime.ApplicationStarted.Register(() =>
		{
			var urls = app.Urls.DefaultIfEmpty("http://localhost:5000");
			Console.WriteLine("Agent HTTP service started.");
			Console.WriteLine($"Model: {app.Services.GetRequiredService<IOptions<OpenRouterOptions>>().Value.Model}");
			Console.WriteLine();
			foreach (var url in urls)
			{
				Console.WriteLine($"  POST   {url}/chat");
				Console.WriteLine($"  DELETE {url}/chat/{{sessionId}}");
			}
			Console.WriteLine();
			Console.WriteLine("Press Ctrl+C to stop.");
		});

		await app.RunAsync();
	}
	else
	{
		var agent = app.Services.GetRequiredService<ConsoleAgent>();
		await agent.RunAsync();
	}
	return;
}
catch (OptionsValidationException exception)
{
	Console.Error.WriteLine("Startup failed: OpenRouter configuration is invalid.");

	foreach (var failure in exception.Failures.Distinct())
	{
		Console.Error.WriteLine($"- {failure}");
	}

	Console.Error.WriteLine("Set the value in user secrets or with the OPENROUTER__APIKEY environment variable, then run the app again.");
	Environment.ExitCode = 1;
}

internal sealed record ChatRequest(string Message, string? SessionId = null);
internal sealed record ChatResponse(string SessionId, string Reply, int TokensConsumed);
