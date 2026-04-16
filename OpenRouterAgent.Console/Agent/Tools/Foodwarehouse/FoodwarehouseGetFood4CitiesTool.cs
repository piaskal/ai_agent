using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

public sealed class FoodwarehouseGetFood4CitiesTool : IAgentTool
{
    public const string ToolName = "foodwarehouse_get_food4cities";
    private const string DataUrl = "https://hub.ag3nts.org/dane/food4cities.json";

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Downloads and returns raw JSON content from https://hub.ag3nts.org/dane/food4cities.json.",
            ParametersSchema: new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(DataUrl, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to download food4cities data from '{DataUrl}'. Status: {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
        }

        return new ToolExecutionResult(responseBody);
    }
}
