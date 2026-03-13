using OpenRouterAgent.ConsoleApp.OpenRouter;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class BuiltInAgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly IReadOnlyDictionary<string, IAgentTool> _toolsByName;

    public BuiltInAgentToolRegistry(IEnumerable<IAgentTool> tools, IOptions<AgentToolOptions> options)
    {
        var toolOptions = options.Value;
        var allTools = tools.ToArray();

        if (!toolOptions.EnableTools)
        {
            _tools = [];
            _toolsByName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
            return;
        }

        var enabledSet = toolOptions.EnabledTools
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var disabledSet = toolOptions.DisabledTools
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var filtered = allTools.Where(tool =>
        {
            var explicitlyEnabled = enabledSet.Count == 0 || enabledSet.Contains(tool.Name);
            var explicitlyDisabled = disabledSet.Contains(tool.Name);
            return explicitlyEnabled && !explicitlyDisabled;
        });

        _tools = filtered.ToArray();
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
