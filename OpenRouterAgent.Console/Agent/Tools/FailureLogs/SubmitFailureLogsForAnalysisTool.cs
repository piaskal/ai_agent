using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.FailureLogs;

public sealed class SubmitFailureLogsForAnalysisTool : IAgentTool
{
    public const string ToolName = "submit_failure_logs_for_analysis";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "failure";

    private readonly string _apiKey;

    public SubmitFailureLogsForAnalysisTool(IOptions<AgentToolOptions> options)
    {
        _apiKey = options.Value.ApiKey;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Submits failure logs to hub.ag3nts.org for analysis using task 'failure'.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    logs = new { type = "string", description = "Required log lines to analyze. Multiple lines are allowed." }
                },
                required = new[] { "logs" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var logs = ParseArguments(toolCall.Function.Arguments);

        var payload = new
        {
            apikey = _apiKey,
            task = TaskName,
            answer = new
            {
                logs
            }
        };

        var requestContent = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var httpClient = new HttpClient();
        using var response = await httpClient.PostAsync(VerifyUrl, requestContent, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Failure log submission failed with status {response.StatusCode}. Response: {responseBody}");

        return new ToolExecutionResult(responseBody);
    }

    private static string ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            throw new InvalidOperationException("Tool 'submit_failure_logs_for_analysis' requires argument 'logs'.");

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("logs", out var logsElement) || logsElement.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Tool 'submit_failure_logs_for_analysis' requires string argument 'logs'.");

        var logs = logsElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(logs))
            throw new InvalidOperationException("Argument 'logs' cannot be empty.");

        return logs;
    }
}
