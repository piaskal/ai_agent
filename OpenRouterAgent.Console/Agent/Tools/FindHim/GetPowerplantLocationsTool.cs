using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.FindHim;

public sealed class GetPowerplantLocationsTool : IAgentTool
{
    public const string ToolName = "get_powerplant_locations";
    private readonly string _apiKey;

    public GetPowerplantLocationsTool(IOptions<AgentToolOptions> options)
    {
        _apiKey = options.Value.ApiKey;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves a list of powerplant locations.",
            ParametersSchema: new { type = "object", properties = new { } }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();

        var url = $"https://hub.ag3nts.org/data/{_apiKey}/findhim_locations.json";
        var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ToolExecutionResult(content);
    }
}
