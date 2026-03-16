using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Categorize;

public sealed class CategorizeCargo : IAgentTool
{
    public const string ToolName = "categorize_cargo";
    private readonly string _apiKey;
    private readonly ILogger<CategorizeCargo> _logger;
    private readonly IGetCargoDesriptions _tool;
    public CategorizeCargo(IOptions<AgentToolOptions> options, IGetCargoDesriptions tool, ILogger<CategorizeCargo> logger)
    {
        _apiKey = options.Value.ApiKey;
        _tool = tool;
        _logger = logger;
    }    

    public string Name => ToolName;


    public async Task<string> getDescriptions()
    {
        return await _tool.GetContent(CancellationToken.None);
    }
    public ChatToolDefinition Definition
    {
        get
        {
            return new ChatToolDefinition(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Categorizes a cargo items based on provided prompt",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    prompt = new
                    {
                        type = "string",
                        description = "Model prompt to categorize cargo description. The prompt should contain instructions on how to categorize cargo. CAn not exceed 100 tokens"
                    },
                },
                required = new[] { "prompt" }
            }));
        }
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        await ResetEndpoint(cancellationToken);
         var lines =(await getDescriptions()).Split(Environment.NewLine).Skip(1);

        List<(string id, string description)> cargoItems = lines
            .Where(line => !string.IsNullOrWhiteSpace(line) )
            .Select(line =>
        {
            var parts = line.Split(',',2);
            return (id: parts[0].Trim(), description: parts[1].Trim());
        }).ToList();   

        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(toolCall.Function.Arguments ?? "{}");
        string basePrompt = args?.GetValueOrDefault("prompt") ?? throw new InvalidOperationException("Missing required parameter 'prompt'.");

        StringBuilder responses = new StringBuilder();

        foreach (var item in cargoItems)
        {
        string prompt = basePrompt +$" {item.id}, {item.description}"; 

        dynamic body = new 
        {
            apikey= _apiKey,
            task = "categorize",
            answer = new {
                prompt = prompt
            }
        };

        _logger.LogInformation("Sending prompt for cargo item {CargoId}: {Prompt}", item.id, prompt);

         using var httpClient = new System.Net.Http.HttpClient();
        var verifyBody = new System.Net.Http.StringContent(
            JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8,
            "application/json"
        );

        

        var verifyResponse = await httpClient.PostAsync("https://hub.ag3nts.org/verify", verifyBody, cancellationToken);
        if (!verifyResponse.IsSuccessStatusCode)
        {
            var errorContent = await verifyResponse.Content.ReadAsStringAsync(cancellationToken);

            await ResetEndpoint( cancellationToken);

            throw new InvalidOperationException($"Failed to verify prompt: {prompt} Response:{errorContent} (Status code: {verifyResponse.StatusCode})");
        } else {
            var resultContent = await verifyResponse.Content.ReadAsStringAsync(cancellationToken);
            responses.AppendLine($"{item.id}, {item.description}")
                     .AppendLine($"Response: {resultContent}");
                _logger.LogInformation("Received response for cargo item {CargoId}: {Response}", item.id, resultContent);
        }

    }
        return new ToolExecutionResult(responses.ToString());
    }

    private async Task ResetEndpoint(CancellationToken cancellationToken)
    {
        dynamic resetBody = new
        {
            apikey = _apiKey,
            task = "categorize",
            answer = new
            {
                prompt = "reset"
            }
        };
using var httpClient = new System.Net.Http.HttpClient();
        var resetHttpBody = new System.Net.Http.StringContent(
    JsonSerializer.Serialize(resetBody),
    System.Text.Encoding.UTF8,
    "application/json"
);
        await httpClient.PostAsync("https://hub.ag3nts.org/verify", resetHttpBody, cancellationToken);
    }
}