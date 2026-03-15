using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Globalization;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class GetSuspectLocationsTool : IAgentTool
{
    public const string ToolName = "get_suspect_locations";
    public readonly string ApiKey;

    public GetSuspectLocationsTool(IOptions<AgentToolOptions> options)
    {
        var toolOptions = options.Value;
        ApiKey = toolOptions.ApiKey;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves a list of suspects based on the given criteria.",
            ParametersSchema: new { type = "object", properties = new
            {
                name = new { type = "string", description = "Name of the suspect." },
                surname = new { type = "string", description = "Surname of the suspect." }
            }, required = new[] { "name", "surname" }})
            );

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();

        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(toolCall.Function.Arguments);

        var requestBody = new
        {
            name = parameters?["name"],
            surname = parameters?["surname"],
            apikey = ApiKey
        };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://hub.ag3nts.org/api/location")
        {
            Content = content
        };

        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        return new ToolExecutionResult(responseContent);
        
    }
}
