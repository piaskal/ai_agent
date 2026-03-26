using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Drone;

public sealed class GetDroneDocumentationTool : IAgentTool
{
    public const string ToolName = "get_drone_documentation";

    private readonly ILogger<GetDroneDocumentationTool> _logger;

    public GetDroneDocumentationTool(ILogger<GetDroneDocumentationTool> logger)
    {
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves documentation for DRN-BMB7 drone control commands. Provide optional 'category': location, engine, flight, diagnostic, configuration, information, calibration, service, mission, or all.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    category = new
                    {
                        type = "string",
                        description = "Optional category: location, engine, flight, diagnostic, configuration, information, calibration, service, mission, or all (default)"
                    }
                },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var category = ParseArguments(toolCall.Function.Arguments);
        _logger.LogInformation("Fetching drone documentation for category: {Category}", category ?? "all");

        var documentation = BuildDocumentation(category);
        return new ToolExecutionResult(JsonSerializer.Serialize(documentation, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (root.TryGetProperty("category", out var categoryElement) && categoryElement.ValueKind == JsonValueKind.String)
        {
            return categoryElement.GetString()?.Trim().ToLowerInvariant();
        }

        return null;
    }

    private static object BuildDocumentation(string? category)
    {
        category ??= "all";
        var docs = new Dictionary<string, object>();

        if (category == "location" || category == "all")
        {
            docs["location"] = new
            {
                description = "Location control commands",
                commands = new object[]
                {
                    new { method = "setDestinationObject(ID)", parameters = "ID format: [A-Z]{3}[0-9]+[A-Z]{2}", description = "Sets target object", example = "setDestinationObject(BLD1234PL)" },
                    new { method = "set(x,y)", parameters = "x: column, y: row (1-based)", description = "Sets landing sector", example = "set(3,4)" }
                }
            };
        }

        if (category == "engine" || category == "all")
        {
            docs["engine"] = new
            {
                description = "Engine control",
                commands = new object[]
                {
                    new { method = "set(mode)", parameters = "engineON or engineOFF", description = "Enable/disable engines", example = "set(engineON)" },
                    new { method = "set(power)", parameters = "0% to 100%", description = "Set engine power", example = "set(50%)" }
                }
            };
        }

        if (category == "flight" || category == "all")
        {
            docs["flight"] = new
            {
                description = "Flight control",
                commands = new object[]
                {
                    new { method = "set(xm)", parameters = "1m to 100m", description = "Set altitude", example = "set(25m)" },
                    new { method = "flyToLocation", parameters = "none", description = "Start flight to destination", example = "flyToLocation" }
                }
            };
        }

        if (category == "diagnostic" || category == "all")
        {
            docs["diagnostic"] = new
            {
                description = "Diagnostic commands",
                commands = new object[]
                {
                    new { method = "selfCheck", parameters = "none", description = "System health check", example = "selfCheck" }
                }
            };
        }

        if (category == "configuration" || category == "all")
        {
            docs["configuration"] = new
            {
                description = "Configuration",
                commands = new object[]
                {
                    new { method = "setName(x)", parameters = "alphanumeric with spaces", description = "Set drone name", example = "setName(Fox 21)" },
                    new { method = "setOwner(FirstName LastName)", parameters = "two words", description = "Set owner", example = "setOwner(Adam Kowalski)" },
                    new { method = "setLed(color)", parameters = "HEX #RRGGBB", description = "Set LED color", example = "setLed(#FF8800)" }
                }
            };
        }

        if (category == "information" || category == "all")
        {
            docs["information"] = new
            {
                description = "Information queries",
                commands = new object[]
                {
                    new { method = "getFirmwareVersion", parameters = "none", description = "Get firmware version", example = "getFirmwareVersion" },
                    new { method = "getConfig", parameters = "none", description = "Get current config", example = "getConfig" }
                }
            };
        }

        if (category == "calibration" || category == "all")
        {
            docs["calibration"] = new
            {
                description = "Calibration",
                commands = new object[]
                {
                    new { method = "calibrateCompass", parameters = "none", description = "Calibrate compass", example = "calibrateCompass" },
                    new { method = "calibrateGPS", parameters = "none", description = "Calibrate GPS", example = "calibrateGPS" }
                }
            };
        }

        if (category == "service" || category == "all")
        {
            docs["service"] = new
            {
                description = "Service",
                commands = new object[]
                {
                    new { method = "hardReset", parameters = "none", description = "Factory reset", example = "hardReset" }
                }
            };
        }

        if (category == "mission" || category == "all")
        {
            docs["mission"] = new
            {
                description = "Mission objectives",
                note = "Multiple objectives can be set in any order",
                commands = new object[]
                {
                    new { method = "set(video)", description = "Record video" },
                    new { method = "set(image)", description = "Capture image" },
                    new { method = "set(destroy)", description = "Destroy target" },
                    new { method = "set(return)", description = "Return to base" }
                }
            };
        }

        if (category == "all")
        {
            docs["apiEndpoint"] = "POST https://hub.ag3nts.org/verify";
            docs["model"] = "DRN-BMB7 combat drone (SoftoInc, 2026)";
            docs["capabilities"] = new
            {
                payload = "Small-range explosive",
                maxAltitude = "100m",
                minAltitude = "1m"
            };
        }

        return docs;
    }
}
