using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class RedirectPackageTool : IAgentTool
{
    public const string ToolName = "redirect_package";
    private readonly string _apiKey;

    public RedirectPackageTool(IOptions<AgentToolOptions> options)
    {
        _apiKey = options.Value.ApiKey;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Redirects a package to a new destination using a security code.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    packageId = new { type = "string", description = "The package ID to redirect (e.g. PKG12345678)." },
                    destination = new { type = "string", description = "The destination code to redirect the package to (e.g. PWR3847PL)." },
                    code = new { type = "string", description = "The security code authorizing the redirect." }
                },
                required = new[] { "packageId", "destination", "code" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();

        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(toolCall.Function.Arguments);
        var packageId = parameters?["packageId"] ?? throw new InvalidOperationException("Missing required parameter 'packageId'.");
        var destination = parameters?["destination"] ?? throw new InvalidOperationException("Missing required parameter 'destination'.");
        var code = parameters?["code"] ?? throw new InvalidOperationException("Missing required parameter 'code'.");

        var requestBody = new
        {
            apikey = _apiKey,
            action = "redirect",
            packageid = packageId,
            destination,
            code
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
