using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.Agent;
using OpenRouterAgent.ConsoleApp.Agent.Tools;
using OpenRouterAgent.ConsoleApp.Agent.Tools.Categorize;
using OpenRouterAgent.ConsoleApp.Agent.Tools.Drone;
using OpenRouterAgent.ConsoleApp.Agent.Tools.Electricity;
using OpenRouterAgent.ConsoleApp.Agent.Tools.FailureLogs;
using OpenRouterAgent.ConsoleApp.Agent.Tools.FindHim;
using OpenRouterAgent.ConsoleApp.Agent.Tools.Firmware;
using OpenRouterAgent.ConsoleApp.Agent.Tools.Mailbox;
using OpenRouterAgent.ConsoleApp.Agent.Tools.Reactor;
using OpenRouterAgent.ConsoleApp.Agent.Tools.RedirectPackage;
using OpenRouterAgent.ConsoleApp.Agent.Tools.SaveThem;
using OpenRouterAgent.ConsoleApp.Agent.Tools.SPK;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using Serilog;
using Serilog.Filters;

var isServeMode = args.Contains("--serve", StringComparer.OrdinalIgnoreCase);
var configArgs = args.Where(a => !a.Equals("--serve", StringComparison.OrdinalIgnoreCase)).ToArray();

try
{
	var builder = WebApplication.CreateBuilder(configArgs);
	builder.Configuration.AddUserSecrets<Program>(optional: true);
	var runLogPath = Path.Combine("logs", $"trace-{DateTime.Now:yyyyMMdd-HHmmss}.log");
	var describeConnectionMapVerboseLogPath = Path.Combine("logs", "describe-connection-map-verbose.log");
	builder.Services.AddSerilog((services, loggerConfig) =>
		loggerConfig
			.ReadFrom.Configuration(builder.Configuration)
			.WriteTo.File(runLogPath)
			.WriteTo.Logger(lc => lc
				.Filter.ByIncludingOnly(Matching.FromSource<DescribeConnectionMapTool>())
				.WriteTo.File(
					describeConnectionMapVerboseLogPath,
					restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Verbose,
					shared: true)));
	builder.Configuration.AddCommandLine(
		configArgs,
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["--model"] = "OpenRouter:Model",
			["-m"] = "OpenRouter:Model",
			["--provider"] = "OpenRouter:Provider",
			["-p"] = "OpenRouter:Provider"
		});

	builder.Services
		.AddOptions<OpenRouterOptions>()
		.Bind(builder.Configuration.GetSection(OpenRouterOptions.SectionName))
		.PostConfigure(options =>
		{
			if (string.IsNullOrWhiteSpace(options.LlmRouterApiKey))
			{
				options.LlmRouterApiKey = builder.Configuration["LlmRouter:ApiKey"] ?? string.Empty;
			}

			if (string.IsNullOrWhiteSpace(options.BaseUrl))
			{
				options.BaseUrl = builder.Configuration["OpenRouter:BaseUrl"] ?? string.Empty;
			}
		})
		.Validate(
			options => options.IsProviderValid(),
			"Configuration value 'OpenRouter:Provider' must be either 'openrouter' or 'llmrouter'.")
		.Validate(
			options =>
			{
				var key = options.GetEffectiveApiKey();
				return !string.IsNullOrWhiteSpace(key);
			},
			"Selected provider API key is missing. For llmrouter use 'LlmRouter:ApiKey' (or LLMROUTER__APIKEY). For openrouter use 'OpenRouter:ApiKey' (or OPENROUTER__APIKEY).")
		.Validate(
			options => !string.IsNullOrWhiteSpace(options.GetEffectiveBaseUrl()),
			"Selected provider base URL is missing. Set 'OpenRouter:BaseUrl' or provider-specific base URLs.")
		.Validate(
			options => !string.IsNullOrWhiteSpace(options.Model),
			$"Configuration value '{OpenRouterOptions.SectionName}:Model' is required.")
		.ValidateOnStart();

	builder.Services
		.AddOptions<AgentToolOptions>()
		.Bind(builder.Configuration.GetSection(AgentToolOptions.SectionName));

	builder.Services.AddHttpClient<IOpenRouterClient, OpenRouterClient>();
	builder.Services.AddSingleton<IAgentTool, GetCurrentTimeTool>();
	builder.Services.AddSingleton<IAgentTool, SumTwoNumbersTool>();
	builder.Services.AddSingleton<IAgentTool, DistanceBetweenLocationsTool>();
	builder.Services.AddSingleton<IAgentTool, GetSuspectsTool>();
	builder.Services.AddSingleton<IAgentTool, GetSuspectLocationsTool>();
	builder.Services.AddSingleton<IAgentTool, GetSuspectAccessLevelTool>();
	builder.Services.AddSingleton<IAgentTool, GetPowerplantLocationsTool>();
	builder.Services.AddSingleton<IAgentTool, CheckPackageStatusTool>();
	builder.Services.AddSingleton<IAgentTool, RedirectPackageTool>();
	builder.Services.AddSingleton<IAgentTool, GetDocumentation>();
	builder.Services.AddSingleton<IAgentTool, OcrImageDocumentTool>();
	builder.Services.AddSingleton<IAgentTool, DescribeConnectionMapTool>();
	builder.Services.AddSingleton<IAgentTool, RotateTileTool>();
	builder.Services.AddSingleton<IAgentTool, VerifyDeclarationTool>();
	builder.Services.AddSingleton<IAgentTool, GetCargoDesriptions>();
	builder.Services.AddSingleton<IGetCargoDesriptions, GetCargoDesriptions>();
	builder.Services.AddSingleton<IAgentTool, CategorizeCargo>();
	builder.Services.AddSingleton<IAgentTool, RailwayApiTool>();
	builder.Services.AddSingleton<IAgentTool, ReactorApiTool>();
	builder.Services.AddSingleton<IAgentTool, GetFailureLogsTool>();
	builder.Services.AddSingleton<IAgentTool, SubmitFailureLogsForAnalysisTool>();
	builder.Services.AddSingleton<IAgentTool, GetMailboxTool>();
	builder.Services.AddSingleton<IAgentTool, DescribeImageTool>();
	builder.Services.AddSingleton<IAgentTool, DroneControlTool>();
	builder.Services.AddSingleton<IAgentTool, GetDroneDocumentationTool>();
	builder.Services.AddSingleton<IAgentTool, ShellCommandTool>();
	builder.Services.AddSingleton<IAgentTool, SaveThemApiTool>();
	builder.Services.AddSingleton<IAgentTool, VerifyPathTool>();
	builder.Services.AddSingleton<IAgentToolRegistry, BuiltInAgentToolRegistry>();
	builder.Services.AddSingleton<AgentService>();
	builder.Services.AddSingleton<ConsoleAgent>();

	var app = builder.Build();

	if (isServeMode)
	{
		app.MapGet("/", () => Results.Ok("OpenRouterAgent is running. Use POST /chat to interact with the agent."));
		app.MapPost("/chat", async (ChatRequest req, AgentService agentService, CancellationToken ct) =>
		{
			if (string.IsNullOrWhiteSpace(req.Message))
				return Results.BadRequest("Message is required.");

			var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
				? agentService.CreateSession()
				: req.SessionId;

			app.Logger.LogInformation(
				"Received /chat request for session '{SessionId}' with message {Message}.",
				sessionId,
				req.Message);

			try
			{
				var reply = await agentService.ChatAsync(sessionId, req.Message, ct);

				app.Logger.LogInformation(
				"Sending /chat response for session '{SessionId}': {reply.Reply}.",
				sessionId,
				reply.Reply);

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

	Console.Error.WriteLine("Set the selected provider API key in user secrets or environment variables and run again.");
	Environment.ExitCode = 1;
}

internal sealed class ChatRequest{
	[JsonPropertyName("msg")]
	[JsonRequired]
	public required string Message { get; set; }
	[JsonPropertyName("sessionID")]
	public string? SessionId { get; set; } = null;
}
internal sealed class ChatResponse{
	[JsonIgnore]
	public string SessionId { get; set; }
	[JsonPropertyName("msg")]
	public string Reply { get; set; }
	[JsonIgnore]
	public int TokensConsumed { get; set; }

	public ChatResponse(string sessionId, string reply, int tokensConsumed)
	{
		SessionId = sessionId;
		Reply = reply;
		TokensConsumed = tokensConsumed;
	}
}
