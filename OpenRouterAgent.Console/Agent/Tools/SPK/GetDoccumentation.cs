using System.Net.Http;
using System.Text.Json;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Threading.Tasks;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.SPK;

public sealed class GetDocumentation : IAgentTool
{
    public const string ToolName = "GetDocumentation";

    private static readonly string BaseUrl = "https://hub.ag3nts.org/dane/doc/";
    private static readonly string DefaultFile = "index.md";

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "pobiera dokumentację systemu SPK z pliku https://hub.ag3nts.org/dane/doc/index.md lub wybranego pliku załącznika https://hub.ag3nts.org/dane/doc/<file>",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    file = new { type = "string", description = "Optional file to fetch documentation from." }
                },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        string url = $"{BaseUrl}{DefaultFile}";

        if (!string.IsNullOrWhiteSpace(toolCall.Function.Arguments))
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, string>>(toolCall.Function.Arguments);

            if (args is not null && args.TryGetValue("file", out var candidateFile) && !string.IsNullOrWhiteSpace(candidateFile))
            {
                url = $"{BaseUrl}{candidateFile}";
                if (!(
                    candidateFile.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                    candidateFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ))
                
                {
                    throw new InvalidOperationException($"Invalid file specified. Only text files are allowed. Cannot read {url} directly ");
                }   
            }
        }

        return new ToolExecutionResult(await FetchDocumentationAsync(url, cancellationToken));
    }

    private static async Task<string> FetchDocumentationAsync(string url, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();

        if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            throw new InvalidOperationException($"Invalid URL '{url}' provided for GetDocumentation.");

        using var response = await httpClient.GetAsync(url, cancellationToken);
        if(!response.IsSuccessStatusCode) {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Failed to fetch documentation from '{url}'. Status code: {response.StatusCode}. Response: {errorContent}");
        }

        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("text") == true)        {
           return await response.Content.ReadAsStringAsync(cancellationToken);
        } else
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return Convert.ToBase64String(bytes);
        }

        
    }
}

