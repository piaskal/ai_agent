using OpenRouterAgent.ConsoleApp.OpenRouter;
using System.Globalization;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.FindHim;

public sealed class DistanceBetweenLocationsTool : IAgentTool
{
    public const string ToolName = "distance_between_locations";

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Calculates distance in kilometers between two locations using latitude and longitude.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    latitude1 = new { type = "number", description = "Latitude of the first location (-90 to 90)." },
                    longitude1 = new { type = "number", description = "Longitude of the first location (-180 to 180)." },
                    latitude2 = new { type = "number", description = "Latitude of the second location (-90 to 90)." },
                    longitude2 = new { type = "number", description = "Longitude of the second location (-180 to 180)." }
                },
                required = new[] { "latitude1", "longitude1", "latitude2", "longitude2" }
            }));

    public Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var (latitude1, longitude1, latitude2, longitude2) = ParseArguments(toolCall.Function.Arguments);
        var distanceKm = CalculateDistanceKm(latitude1, longitude1, latitude2, longitude2);
        return Task.FromResult(new ToolExecutionResult(distanceKm.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    private static (double Latitude1, double Longitude1, double Latitude2, double Longitude2) ParseArguments(string argumentsJson)
    {
        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        var latitude1 = ReadNumber(root, "latitude1");
        var longitude1 = ReadNumber(root, "longitude1");
        var latitude2 = ReadNumber(root, "latitude2");
        var longitude2 = ReadNumber(root, "longitude2");

        ValidateCoordinateRange(latitude1, -90, 90, "latitude1");
        ValidateCoordinateRange(latitude2, -90, 90, "latitude2");
        ValidateCoordinateRange(longitude1, -180, 180, "longitude1");
        ValidateCoordinateRange(longitude2, -180, 180, "longitude2");

        return (latitude1, longitude1, latitude2, longitude2);
    }

    private static double ReadNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"Tool '{ToolName}' requires numeric argument '{propertyName}'.");

        return element.GetDouble();
    }

    private static void ValidateCoordinateRange(double value, double min, double max, string name)
    {
        if (value < min || value > max)
            throw new InvalidOperationException($"Argument '{name}' must be between {min} and {max}.");
    }

    private static double CalculateDistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusKm = 6371.0;

        var lat1Rad = DegreesToRadians(latitude1);
        var lon1Rad = DegreesToRadians(longitude1);
        var lat2Rad = DegreesToRadians(latitude2);
        var lon2Rad = DegreesToRadians(longitude2);

        var deltaLat = lat2Rad - lat1Rad;
        var deltaLon = lon2Rad - lon1Rad;

        var a = Math.Pow(Math.Sin(deltaLat / 2), 2) +
                Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * Math.Pow(Math.Sin(deltaLon / 2), 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
