using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class ReportExtractor
{
    private const int MaxEvidenceChars = 80_000;

    private static readonly JsonSerializerOptions DraftJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IOpenRouterClient _openRouterClient;
    private readonly ILogger<ReportExtractor> _logger;

    public ReportExtractor(IOpenRouterClient openRouterClient, ILogger<ReportExtractor> logger)
    {
        _openRouterClient = openRouterClient;
        _logger = logger;
    }

    public async Task<RadioReport> ExtractAsync(
        IReadOnlyList<EvidenceItem> evidence,
        CancellationToken cancellationToken = default)
    {
        if (evidence.Count == 0)
        {
            throw new InvalidOperationException("No usable radiomonitoring evidence was collected.");
        }

        var bundle = BuildEvidenceBundle(evidence);
        _logger.LogInformation(
            "Extracting radiomonitoring report from {Count} evidence items ({Chars} chars) via OpenRouter.",
            evidence.Count,
            bundle.Length);

        var messages = new[]
        {
            ChatMessage.System(
                "You extract facts about the city known as Syjon from intercepted radio material. " +
                "Syjon is a codename; cityName must be the real city name. " +
                "Return JSON only, no markdown. " +
                "Use null when a field is unknown. Do not invent values. " +
                "cityAreaRaw should be the numeric area if stated; otherwise leave it null and fill areaLength and areaWidth when dimensions are given. " +
                "If warehouses are listed, put every name in warehouseNames and still set warehousesCount. " +
                "Copy phoneNumber from the source without adding extra digits."),
            ChatMessage.User(
                "Intercepted evidence:\n\n" + bundle + "\n\n" +
                "Return JSON with this shape:\n" +
                """{"cityName":"","cityAreaRaw":"","areaLength":null,"areaWidth":null,"warehousesCount":null,"warehouseNames":[],"phoneNumber":"","notes":""}""")
        };

        var completion = await _openRouterClient.GetCompletionAsync(messages, [], cancellationToken);
        if (string.IsNullOrWhiteSpace(completion.Content))
        {
            throw new InvalidOperationException("OpenRouter returned an empty radiomonitoring extraction.");
        }

        _logger.LogInformation("OpenRouter extraction raw response: {Response}", completion.Content);
        var draft = ParseDraft(completion.Content);
        var report = ReportFormatter.FromDraft(draft);
        if (!ReportFormatter.IsComplete(report))
        {
            throw new InvalidOperationException(
                "Extraction is incomplete: " +
                $"cityName='{report.CityName}', cityArea='{report.CityArea}', warehousesCount={report.WarehousesCount}, phoneNumber='{report.PhoneNumber}'. " +
                $"notes={draft.Notes}");
        }

        return report;
    }

    private static string BuildEvidenceBundle(IReadOnlyList<EvidenceItem> evidence)
    {
        var builder = new StringBuilder();
        foreach (var item in evidence)
        {
            builder.AppendLine($"--- capture {item.Sequence:D4} ({item.Kind}, {item.Source}) ---");
            builder.AppendLine(item.Content);
            builder.AppendLine();
            if (builder.Length >= MaxEvidenceChars)
            {
                builder.AppendLine("--- truncated remaining evidence ---");
                break;
            }
        }

        return builder.ToString();
    }

    internal static RadioReportDraft ParseDraft(string content)
    {
        var json = UnwrapJson(content);
        var draft = JsonSerializer.Deserialize<RadioReportDraft>(json, DraftJsonOptions);
        if (draft is null)
        {
            throw new InvalidOperationException("Could not deserialize extraction JSON.");
        }

        if (draft.AreaLength is null && TryGetNumber(json, "areaLength", out var length))
        {
            draft.AreaLength = length;
        }

        if (draft.AreaWidth is null && TryGetNumber(json, "areaWidth", out var width))
        {
            draft.AreaWidth = width;
        }

        return draft;
    }

    private static string UnwrapJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = trimmed.IndexOf('\n');
            if (firstBreak >= 0)
            {
                trimmed = trimmed[(firstBreak + 1)..];
            }

            var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                trimmed = trimmed[..fence];
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Extraction response did not contain JSON.");
        }

        return trimmed[start..(end + 1)];
    }

    private static bool TryGetNumber(string json, string property, out decimal value)
    {
        value = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(property, out var element))
            {
                return false;
            }

            return element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetDecimal(out value),
                JsonValueKind.String => decimal.TryParse(
                    element.GetString(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out value),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
