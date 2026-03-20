namespace OpenRouterAgent.ConsoleApp.Agent.Tools;

public sealed class AgentToolOptions
{
    public const string SectionName = "AgentTools";

    public bool EnableTools { get; set; } = true;

    public string[] EnabledTools { get; set; } = [];
    public string[] DisabledTools { get; set; } = [];
    public string ApiKey { get; set; } = string.Empty;

}
