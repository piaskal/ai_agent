using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class CheckPackageStatusTool : IAgentTool
{
    public const string ToolName = "check_package_status";
    private readonly string _apiKey;

    public CheckPackageStatusTool(IOptions<AgentToolOptions> options)
    {
        _apiKey = options.Value.ApiKey;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Checks the status of a package by its ID.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    packageId = new { type = "string", description = "The package ID to check (e.g. PKG12345678)." }
                },
                required = new[] { "packageId" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();

        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(toolCall.Function.Arguments);
        var packageId = parameters?["packageId"] ?? throw new InvalidOperationException("Missing required parameter 'packageId'.");

        var requestBody = new
        {
            apikey = _apiKey,
            action = "check",
            packageid = packageId
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await httpClient.PostAsync("https://hub.ag3nts.org/api/packages", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ToolExecutionResult(responseContent);
    }
}
