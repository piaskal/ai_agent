using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class BuiltInAgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly IReadOnlyDictionary<string, IAgentTool> _toolsByName;

    public BuiltInAgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.ToArray();
        _toolsByName = _tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<ChatToolDefinition> GetToolDefinitions() => _tools.Select(t => t.Definition).ToArray();

    public Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        if (_toolsByName.TryGetValue(toolCall.Function.Name, out var tool))
            return tool.ExecuteAsync(toolCall, cancellationToken);

        throw new InvalidOperationException($"Unknown tool '{toolCall.Function.Name}'.");
    }
}
