using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Shellaccess;

public static class ShellaccessArchive
{
    public sealed record TimeLogEntry(DateOnly Date, string Description, int LocationId, int EntryId);

    public sealed record MeetingAnswer(DateOnly Date, string City, decimal Longitude, decimal Latitude);

    public static TimeLogEntry? ParseLogLine(string line)
    {
        var payload = line.Trim().TrimEnd('\r');
        if (payload.Length == 0)
        {
            return null;
        }

        var colon = payload.IndexOf(':');
        if (colon > 0 && payload[..colon].All(char.IsDigit))
        {
            payload = payload[(colon + 1)..].Trim();
        }

        var parts = payload.Split(';');
        if (parts.Length < 4)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var locationId))
        {
            return null;
        }

        if (!int.TryParse(parts[^1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var entryId))
        {
            return null;
        }

        var description = string.Join(';', parts[1..^2]).Trim().Trim('"');
        return new TimeLogEntry(date, description, locationId, entryId);
    }

    public static TimeLogEntry SelectBodyDiscovery(IEnumerable<string> lines)
    {
        var entries = lines
            .Select(ParseLogLine)
            .OfType<TimeLogEntry>()
            .Where(entry => MentionsBody(entry.Description))
            .ToList();

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The time archive has no entry about a discovered body.");
        }

        var named = entries
            .Where(entry => entry.Description.Contains("Rafa", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pool = named.Count > 0 ? named : entries;
        var found = pool
            .Where(entry => entry.Description.Contains("znalez", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (found.Count == 1)
        {
            return found[0];
        }

        if (pool.Count == 1)
        {
            return pool[0];
        }

        throw new InvalidOperationException(
            "The time archive has more than one body-discovery entry: "
            + string.Join(" | ", pool.Select(entry => $"{entry.Date:yyyy-MM-dd} {entry.Description}")));
    }

    public static MeetingAnswer BuildAnswer(TimeLogEntry discovery, string city, string gpsTsv)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            throw new InvalidOperationException($"Location {discovery.LocationId} has no city name.");
        }

        var fields = gpsTsv.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 3)
        {
            throw new InvalidOperationException($"GPS entry {discovery.EntryId} did not return latitude, longitude, and location id.");
        }

        if (!decimal.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude))
        {
            throw new InvalidOperationException($"GPS latitude '{fields[0]}' is not a number.");
        }

        if (!decimal.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
        {
            throw new InvalidOperationException($"GPS longitude '{fields[1]}' is not a number.");
        }

        if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gpsLocationId))
        {
            throw new InvalidOperationException($"GPS location id '{fields[2]}' is not an integer.");
        }

        if (gpsLocationId != discovery.LocationId)
        {
            throw new InvalidOperationException(
                $"GPS entry {discovery.EntryId} belongs to location {gpsLocationId}, not log location {discovery.LocationId}.");
        }

        return new MeetingAnswer(discovery.Date.AddDays(-1), city.Trim(), longitude, latitude);
    }

    public static string FormatAnswer(MeetingAnswer answer)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("date", answer.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("city", answer.City);
            writer.WriteNumber("longitude", answer.Longitude);
            writer.WriteNumber("latitude", answer.Latitude);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string EchoCommand(string json) =>
        "echo '" + json.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static bool RunSelfTest(TextWriter output)
    {
        var failures = new List<string>();
        var sample = "3704:2024-11-13;W jaskini znaleziono ciało mężczyzny. Policja bada okoliczności zdarzenia;219;954634\r";
        var parsed = ParseLogLine(sample);
        if (parsed is null
            || parsed.Date != new DateOnly(2024, 11, 13)
            || parsed.LocationId != 219
            || parsed.EntryId != 954634
            || !parsed.Description.Contains("jaskini", StringComparison.Ordinal))
        {
            failures.Add("sample log line did not parse");
        }

        var selected = SelectBodyDiscovery(
        [
            "1:2019-08-10;Jeffrey Epstein znaleziony martwy w celi aresztu;64;189225",
            sample,
            "2:2010-10-15;Film The Social Network o Marku Zuckerbergu ma premierę;63;973807"
        ]);
        if (selected.EntryId != 954634)
        {
            failures.Add("body discovery selection picked the wrong log line");
        }

        var named = SelectBodyDiscovery(
        [
            sample,
            "9:2024-11-20;W lesie znaleziono ciało Rafała;10;11"
        ]);
        if (named.EntryId != 11)
        {
            failures.Add("a line that names Rafał should win over an unnamed body");
        }

        var answer = BuildAnswer(selected, "Grudziądz", "53.432303\t18.968774\t219\tjaskinia");
        if (answer.Date != new DateOnly(2024, 11, 12)
            || answer.City != "Grudziądz"
            || answer.Longitude != 18.968774m
            || answer.Latitude != 53.432303m)
        {
            failures.Add("meeting answer was not the day before the discovery in that city");
        }

        var json = FormatAnswer(answer);
        const string expected = """{"date":"2024-11-12","city":"Grudziądz","longitude":18.968774,"latitude":53.432303}""";
        if (json != expected)
        {
            failures.Add($"answer JSON was {json}");
        }

        if (EchoCommand(json) != "echo '" + expected + "'")
        {
            failures.Add("echo command was not shell-quoted");
        }

        try
        {
            BuildAnswer(selected, "Grudziądz", "1\t2\t999\tjaskinia");
            failures.Add("a GPS row from another city was accepted");
        }
        catch (InvalidOperationException)
        {
        }

        if (failures.Count == 0)
        {
            output.WriteLine("shellaccess self-test passed");
            return true;
        }

        foreach (var failure in failures)
        {
            output.WriteLine("FAIL: " + failure);
        }

        return false;
    }

    private static bool MentionsBody(string description) =>
        description.Contains("ciało", StringComparison.OrdinalIgnoreCase)
        || description.Contains("cialo", StringComparison.OrdinalIgnoreCase);
}
