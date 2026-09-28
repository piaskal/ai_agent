using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Timetravel;

public sealed record DeviceConfig(
    DateOnly CurrentDate,
    int? Day,
    int? Month,
    int? Year,
    decimal SyncRatio,
    int Stabilization,
    string Condition,
    int FluxDensity,
    string BatteryStatus,
    bool Pta,
    bool Ptb,
    int Pwr,
    string Mode,
    int InternalMode)
{
    public static DeviceConfig Parse(JsonObject root)
    {
        if (root["config"] is not JsonObject config)
        {
            throw new InvalidOperationException("Device response did not include config.");
        }

        return new DeviceConfig(
            DateOnly.Parse(RequiredString(config, "currentDate"), CultureInfo.InvariantCulture),
            ReadInt(config["day"]),
            ReadInt(config["month"]),
            ReadInt(config["year"]),
            ReadDecimal(config["syncRatio"]) ?? 0m,
            ReadInt(config["stabilization"]) ?? 0,
            RequiredString(config, "condition"),
            ReadInt(config["fluxDensity"]) ?? 0,
            RequiredString(config, "batteryStatus"),
            ReadBool(config["PTA"]),
            ReadBool(config["PTB"]),
            ReadInt(config["PWR"]) ?? 0,
            RequiredString(config, "mode"),
            ReadInt(config["internalMode"]) ?? 0);
    }

    public override string ToString() =>
        $"{CurrentDate:yyyy-MM-dd} flux={FluxDensity} condition={Condition} battery={BatteryStatus} mode={Mode} internalMode={InternalMode} PTA={Pta} PTB={Ptb} PWR={Pwr} sync={SyncRatio} stab={Stabilization}";

    private static string RequiredString(JsonObject config, string name)
    {
        if (config[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new InvalidOperationException($"Device config is missing {name}.");
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer;
        }

        if (value.TryGetValue<decimal>(out var number))
        {
            return (int)number;
        }

        return null;
    }

    private static decimal? ReadDecimal(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() == System.Text.Json.JsonValueKind.Null)
        {
            return null;
        }

        if (value.TryGetValue<decimal>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            return integer;
        }

        return null;
    }

    private static bool ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
