using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.Agent;
using OpenRouterAgent.ConsoleApp.Agent.Tools;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using Serilog;

try
{
	var builder = Host.CreateApplicationBuilder(args);
	builder.Configuration.AddUserSecrets<Program>(optional: true);
	builder.Services.AddSerilog((services, loggerConfig) =>
		loggerConfig.ReadFrom.Configuration(builder.Configuration));
	builder.Configuration.AddCommandLine(
		args,
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
	builder.Services.AddSingleton<ConversationState>();
	builder.Services.AddSingleton<ConsoleAgent>();

	using var host = builder.Build();

	var agent = host.Services.GetRequiredService<ConsoleAgent>();
	await agent.RunAsync();
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
