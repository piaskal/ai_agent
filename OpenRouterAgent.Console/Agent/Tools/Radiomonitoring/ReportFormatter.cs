using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public static class ReportFormatter
{
    public static string FormatArea(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    public static bool TryParseArea(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = raw.Trim();
        s = Regex.Replace(s, @"\s*(km²|km2|m²|m2|ha)\s*", string.Empty, RegexOptions.IgnoreCase);
        s = s.Replace(',', '.');
        s = Regex.Replace(s, @"[^\d.\-]", string.Empty);
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    public static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        var plus = trimmed.StartsWith('+') ? "+" : string.Empty;
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        return plus + digits;
    }

    public static RadioReport FromDraft(RadioReportDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        decimal? area = null;
        if (draft.AreaLength is { } length && draft.AreaWidth is { } width)
        {
            area = length * width;
        }
        else if (TryParseArea(draft.CityAreaRaw, out var parsed))
        {
            area = parsed;
        }

        var warehouses = draft.WarehousesCount;
        if (draft.WarehouseNames is { Length: > 0 })
        {
            warehouses = draft.WarehouseNames.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        return new RadioReport
        {
            CityName = draft.CityName?.Trim() ?? string.Empty,
            CityArea = area is { } value ? FormatArea(value) : string.Empty,
            WarehousesCount = warehouses ?? 0,
            PhoneNumber = NormalizePhone(draft.PhoneNumber)
        };
    }

    public static bool IsComplete(RadioReport report) =>
        !string.IsNullOrWhiteSpace(report.CityName) &&
        !string.IsNullOrWhiteSpace(report.CityArea) &&
        report.WarehousesCount > 0 &&
        !string.IsNullOrWhiteSpace(report.PhoneNumber);
}
