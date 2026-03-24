using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.FailureLogs;

public sealed class GetFailureLogsTool : IAgentTool
{
    public const string ToolName = "get_failure_logs";


    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;

    private static readonly Regex LogLineRegex = new(
        @"^\[(?<timestamp>\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2})\]\s+\[(?<level>[A-Z]+)\]\s+(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] SupportedLevels = ["INFO", "WARN", "ERRO", "CRIT"];

    public string Name => ToolName;

    private readonly string _apiKey;
    private readonly string LogUrl;

    public GetFailureLogsTool(IOptions<AgentToolOptions> options)
    {
        _apiKey = options.Value.ApiKey;
        LogUrl = $"https://hub.ag3nts.org/data/{_apiKey}/failure.log";
    }

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves and filters failure logs by level, date/time ranges, keyword, and supports limit/offset pagination.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    level = new { type = "string", description = "Optional log level filter. One of INFO, WARN, ERRO, CRIT." },
                    keyword = new { type = "string", description = "Optional case-insensitive keyword search over log message." },
                    from = new { type = "string", description = "Optional timestamp lower bound (inclusive). Format: yyyy-MM-dd HH:mm:ss or yyyy-MM-ddTHH:mm:ss." },
                    to = new { type = "string", description = "Optional timestamp upper bound (inclusive). Format: yyyy-MM-dd HH:mm:ss or yyyy-MM-ddTHH:mm:ss." },
                    dateFrom = new { type = "string", description = "Optional date lower bound (inclusive). Format: yyyy-MM-dd." },
                    dateTo = new { type = "string", description = "Optional date upper bound (inclusive). Format: yyyy-MM-dd." },
                    timeFrom = new { type = "string", description = "Optional time lower bound (inclusive). Format: HH:mm:ss." },
                    timeTo = new { type = "string", description = "Optional time upper bound (inclusive). Format: HH:mm:ss." },
                    limit = new { type = "integer", description = "Maximum number of log entries to return. Default: 100. Max: 1000.", minimum = 1 },
                    offset = new { type = "integer", description = "Number of matching log entries to skip. Default: 0.", minimum = 0 }
                },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var filter = ParseArguments(toolCall.Function.Arguments);

        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(LogUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        var filtered = new List<LogEntry>(capacity: lines.Length);

        foreach (var line in lines)
        {
            if (!TryParseLine(line, out var entry))
                continue;

            if (!MatchesFilters(entry, filter))
                continue;

            filtered.Add(entry);
        }

        var totalMatched = filtered.Count;
        var paged = filtered
            .Skip(filter.Offset)
            .Take(filter.Limit)
            .ToArray();

        var result = new
        {
            source = LogUrl,
            filters = new
            {
                filter.Level,
                filter.Keyword,
                filter.From,
                filter.To,
                filter.DateFrom,
                filter.DateTo,
                filter.TimeFrom,
                filter.TimeTo,
                filter.Limit,
                filter.Offset
            },
            totalMatched,
            returned = paged.Length,
            hasMore = filter.Offset + paged.Length < totalMatched,
            entries = paged.Select(e => new
            {
                timestamp = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                level = e.Level,
                message = e.Message
            })
        };

        return new ToolExecutionResult(JsonSerializer.Serialize(result));
    }

    private static bool TryParseLine(string line, out LogEntry entry)
    {
        var match = LogLineRegex.Match(line);
        if (!match.Success)
        {
            entry = null!;
            return false;
        }

        var timestampText = match.Groups["timestamp"].Value;
        if (!DateTime.TryParseExact(
                timestampText,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            entry = null!;
            return false;
        }

        entry = new LogEntry(
            Timestamp: timestamp,
            Level: match.Groups["level"].Value,
            Message: match.Groups["message"].Value);

        return true;
    }

    private static bool MatchesFilters(LogEntry entry, LogFilter filter)
    {
        if (filter.Level is not null && !entry.Level.Equals(filter.Level, StringComparison.OrdinalIgnoreCase))
            return false;

        if (filter.From.HasValue && entry.Timestamp < filter.From.Value)
            return false;

        if (filter.To.HasValue && entry.Timestamp > filter.To.Value)
            return false;

        if (filter.DateFrom.HasValue && DateOnly.FromDateTime(entry.Timestamp) < filter.DateFrom.Value)
            return false;

        if (filter.DateTo.HasValue && DateOnly.FromDateTime(entry.Timestamp) > filter.DateTo.Value)
            return false;

        if (filter.TimeFrom.HasValue && TimeOnly.FromDateTime(entry.Timestamp) < filter.TimeFrom.Value)
            return false;

        if (filter.TimeTo.HasValue && TimeOnly.FromDateTime(entry.Timestamp) > filter.TimeTo.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(filter.Keyword) &&
            entry.Message.IndexOf(filter.Keyword, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return true;
    }

    private static LogFilter ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new LogFilter();

        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;

        var level = ReadOptionalString(root, "level")?.Trim().ToUpperInvariant();
        if (level is not null && !SupportedLevels.Contains(level, StringComparer.Ordinal))
            throw new InvalidOperationException($"Invalid 'level' value '{level}'. Supported values: {string.Join(", ", SupportedLevels)}.");

        var from = ReadOptionalString(root, "from");
        var to = ReadOptionalString(root, "to");
        var dateFrom = ReadOptionalString(root, "dateFrom");
        var dateTo = ReadOptionalString(root, "dateTo");
        var timeFrom = ReadOptionalString(root, "timeFrom");
        var timeTo = ReadOptionalString(root, "timeTo");

        var parsedFrom = ParseOptionalDateTime(from, "from");
        var parsedTo = ParseOptionalDateTime(to, "to");
        var parsedDateFrom = ParseOptionalDate(dateFrom, "dateFrom");
        var parsedDateTo = ParseOptionalDate(dateTo, "dateTo");
        var parsedTimeFrom = ParseOptionalTime(timeFrom, "timeFrom");
        var parsedTimeTo = ParseOptionalTime(timeTo, "timeTo");

        if (parsedFrom.HasValue && parsedTo.HasValue && parsedFrom > parsedTo)
            throw new InvalidOperationException("'from' must be less than or equal to 'to'.");

        if (parsedDateFrom.HasValue && parsedDateTo.HasValue && parsedDateFrom > parsedDateTo)
            throw new InvalidOperationException("'dateFrom' must be less than or equal to 'dateTo'.");

        if (parsedTimeFrom.HasValue && parsedTimeTo.HasValue && parsedTimeFrom > parsedTimeTo)
            throw new InvalidOperationException("'timeFrom' must be less than or equal to 'timeTo'.");

        var limit = ReadOptionalInt(root, "limit") ?? DefaultLimit;
        if (limit <= 0)
            throw new InvalidOperationException("'limit' must be a positive integer.");
        if (limit > MaxLimit)
            throw new InvalidOperationException($"'limit' cannot exceed {MaxLimit}.");

        var offset = ReadOptionalInt(root, "offset") ?? 0;
        if (offset < 0)
            throw new InvalidOperationException("'offset' must be greater than or equal to 0.");

        return new LogFilter
        {
            Level = level,
            Keyword = ReadOptionalString(root, "keyword")?.Trim(),
            From = parsedFrom,
            To = parsedTo,
            DateFrom = parsedDateFrom,
            DateTo = parsedDateTo,
            TimeFrom = parsedTimeFrom,
            TimeTo = parsedTimeTo,
            Limit = limit,
            Offset = offset
        };
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"'{propertyName}' must be a string.");

        return element.GetString();
    }

    private static int? ReadOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw new InvalidOperationException($"'{propertyName}' must be an integer.");

        return value;
    }

    private static DateTime? ParseOptionalDateTime(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss"
        };

        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        throw new InvalidOperationException($"'{propertyName}' has invalid format. Expected yyyy-MM-dd HH:mm:ss or yyyy-MM-ddTHH:mm:ss.");
    }

    private static DateOnly? ParseOptionalDate(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        throw new InvalidOperationException($"'{propertyName}' has invalid format. Expected yyyy-MM-dd.");
    }

    private static TimeOnly? ParseOptionalTime(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (TimeOnly.TryParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        throw new InvalidOperationException($"'{propertyName}' has invalid format. Expected HH:mm:ss.");
    }

    private sealed class LogFilter
    {
        public string? Level { get; init; }
        public string? Keyword { get; init; }
        public DateTime? From { get; init; }
        public DateTime? To { get; init; }
        public DateOnly? DateFrom { get; init; }
        public DateOnly? DateTo { get; init; }
        public TimeOnly? TimeFrom { get; init; }
        public TimeOnly? TimeTo { get; init; }
        public int Limit { get; init; } = DefaultLimit;
        public int Offset { get; init; }
    }

    private sealed record LogEntry(DateTime Timestamp, string Level, string Message);
}
