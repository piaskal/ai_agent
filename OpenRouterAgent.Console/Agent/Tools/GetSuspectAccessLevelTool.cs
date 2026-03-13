using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class GetSuspectAccessLevelTool : IAgentTool
{
    public const string ToolName = "get_suspect_access_level";
    public readonly string ApiKey;

    public GetSuspectAccessLevelTool(IOptions<AgentToolOptions> options)
    {
        var toolOptions = options.Value;
        ApiKey = toolOptions.ApiKey;
       
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves the access level of a suspect based on the given criteria.",
            ParametersSchema: new { type = "object", properties = new
            {
                name = new { type = "string", description = "Name of the suspect." },
                surname = new { type = "string", description = "Surname of the suspect." },
                birthYear = new { type = "number", description = "Birth year of the suspect." }
            }, required = new[] { "name", "surname", "birthYear" }})
            );

    private struct SuspectAccessLevelRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("surname")]   
        public string Surname { get; set; }
        [JsonPropertyName("birthYear")]
        public int? BirthYear { get; set; }
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();

        var parameters = JsonSerializer.Deserialize<SuspectAccessLevelRequest>(toolCall.Function.Arguments);

        var requestBody = new{
            name = parameters.Name,
            surname = parameters.Surname,
            birthYear = parameters.BirthYear,
            apikey = ApiKey
        };

        var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "https://hub.ag3nts.org/api/accesslevel")
        {
            Content = content
        };

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"API request failed with status code {response.StatusCode}: {errorContent}");          
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        return new ToolExecutionResult(responseContent);
        
    }
}
