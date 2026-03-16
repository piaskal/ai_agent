using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Categorize;
public interface IGetCargoDesriptions
{
    Task<string> GetContent(CancellationToken cancellationToken);
}
public sealed class GetCargoDesriptions : IAgentTool, IGetCargoDesriptions
{
    public const string ToolName = "get_cargo_descriptions";
    private readonly string _apiKey;

    public GetCargoDesriptions(IOptions<AgentToolOptions> options)
    {
        _apiKey = options.Value.ApiKey;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Fetches cargo category data from the central agent catalog.",
            ParametersSchema: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Agent tool API key is not configured.");
        }
        string content = await GetContent(cancellationToken);

        return new ToolExecutionResult(content);
    }

    public async Task<string> GetContent(CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var url = $"https://hub.ag3nts.org/data/{Uri.EscapeDataString(_apiKey)}/categorize.csv";

        var response = await httpClient.GetAsync(url, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to download categorize data: {response.StatusCode} - {content}");
        }

        return content;
    }
}
