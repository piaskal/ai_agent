using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Timetravel;

public static partial class TimetravelMath
{
    private static readonly string[] SubtractMarkers = ["obniz", "zmniejsz", "odjac", "odejm"];
    private static readonly string[] AddMarkers = ["podnies", "zwieksz", "podwyzsz"];

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["zero"] = 0,
        ["jeden"] = 1,
        ["jedna"] = 1,
        ["jedno"] = 1,
        ["dwa"] = 2,
        ["dwie"] = 2,
        ["trzy"] = 3,
        ["cztery"] = 4,
        ["piec"] = 5,
        ["szesc"] = 6,
        ["siedem"] = 7,
        ["osiem"] = 8,
        ["dziewiec"] = 9,
        ["dziesiec"] = 10,
        ["jedenascie"] = 11,
        ["dwanascie"] = 12,
        ["trzynascie"] = 13,
        ["czternascie"] = 14,
        ["pietnascie"] = 15,
        ["szesnascie"] = 16,
        ["siedemnascie"] = 17,
        ["osiemnascie"] = 18,
        ["dziewietnascie"] = 19,
        ["dwadziescia"] = 20,
        ["trzydziesci"] = 30,
        ["czterdziesci"] = 40,
        ["piecdziesiat"] = 50,
        ["szescdziesiat"] = 60,
        ["siedemdziesiat"] = 70,
        ["osiemdziesiat"] = 80,
        ["dziewiecdziesiat"] = 90,
        ["sto"] = 100,
        ["dwiescie"] = 200,
        ["trzysta"] = 300,
        ["czterysta"] = 400,
        ["piecset"] = 500,
        ["szescset"] = 600,
        ["siedemset"] = 700,
        ["osiemset"] = 800,
        ["dziewiecset"] = 900,
        ["tysiac"] = 1000
    };

    public static decimal SyncRatio(int year, int month, int day)
    {
        var raw = (day * 8 + month * 12 + year * 7) % 101;
        return raw / 100m;
    }

    public static int InternalMode(int year) => year switch
    {
        < 2000 => 1,
        <= 2150 => 2,
        <= 2300 => 3,
        _ => 4
    };

    public static (bool Pta, bool Ptb) Ports(DateOnly current, DateOnly target, bool tunnel)
    {
        if (target == current)
        {
            throw new InvalidOperationException($"Target date {target:yyyy-MM-dd} is the device's current date.");
        }

        if (tunnel)
        {
            return (true, true);
        }

        return target > current ? (false, true) : (true, false);
    }

    public static IReadOnlyDictionary<int, int> ParseProtectionTable(string markdown)
    {
        var table = new Dictionary<int, int>();
        foreach (Match match in ProtectionRow().Matches(markdown))
        {
            var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var protection = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            table[year] = protection;
        }

        return table;
    }

    public static int ParseStabilization(string hint)
    {
        var stripped = StripDiacritics(hint).ToLowerInvariant();
        var subtractAt = IndexOfAny(stripped, SubtractMarkers);
        var addAt = IndexOfAny(stripped, AddMarkers);
        if (subtractAt < 0 && addAt < 0)
        {
            throw new InvalidOperationException("Stabilization hint does not say whether to raise or lower the setting.");
        }

        var subtract = subtractAt >= 0 && (addAt < 0 || subtractAt < addAt);
        var markerAt = subtract ? subtractAt : addAt;
        var numbers = ReadNumbers(stripped);
        var baseline = numbers.LastOrDefault(number => number.Start < markerAt);
        var adjustment = numbers.FirstOrDefault(number => number.Start > markerAt);
        if (baseline == default || adjustment == default)
        {
            throw new InvalidOperationException($"Could not read stabilization numbers from: {hint}");
        }

        var value = subtract ? baseline.Value - adjustment.Value : baseline.Value + adjustment.Value;
        if (value is < 0 or > 1000)
        {
            throw new InvalidOperationException($"Stabilization {value} is outside 0..1000.");
        }

        return value;
    }

    public static void RunSelfTest()
    {
        var failures = new List<string>();
        Expect(SyncRatio(2238, 11, 5) == 0.82m, "2238-11-05 sync ratio", failures);
        Expect(SyncRatio(2026, 9, 28) == 0.71m, "2026-09-28 sync ratio", failures);
        Expect(SyncRatio(2024, 11, 12) == 0.54m, "2024-11-12 sync ratio", failures);
        Expect(SyncRatio(1, 1, 0) == 0.19m, "raw sync formula", failures);
        Expect(InternalMode(1999) == 1, "internalMode 1999", failures);
        Expect(InternalMode(2000) == 2, "internalMode 2000", failures);
        Expect(InternalMode(2150) == 2, "internalMode 2150", failures);
        Expect(InternalMode(2151) == 3, "internalMode 2151", failures);
        Expect(InternalMode(2300) == 3, "internalMode 2300", failures);
        Expect(InternalMode(2301) == 4, "internalMode 2301", failures);
        Expect(InternalMode(2238) == 3, "internalMode 2238", failures);
        Expect(InternalMode(2024) == 2, "internalMode 2024", failures);

        var future = Ports(new DateOnly(2026, 9, 28), new DateOnly(2238, 11, 5), tunnel: false);
        var past = Ports(new DateOnly(2238, 11, 5), new DateOnly(2026, 9, 28), tunnel: false);
        var tunnel = Ports(new DateOnly(2026, 9, 28), new DateOnly(2024, 11, 12), tunnel: true);
        Expect(future == (false, true), "future ports", failures);
        Expect(past == (true, false), "past ports", failures);
        Expect(tunnel == (true, true), "tunnel ports", failures);

        var table = ParseProtectionTable("""
            | Rok | Ochrona | Rok | Ochrona |
            | --- | --- | --- | --- |
            | 1500 | 03 | 1999 | 14 |
            | 2000 | 13 | 2024 | 19 |
            | 2025 | 18 | 2026 | 28 |
            | 2150 | 82 | 2151 | 86 |
            | 2238 | 91 | 2499 | 97 |
            """);
        Expect(table.Count == 10, "protection fixture count", failures);
        Expect(table[1500] == 3, "protection leading zero", failures);
        Expect(table[2024] == 19 && table[2026] == 28 && table[2238] == 91, "protection known years", failures);
        Expect(table[1999] == 14 && table[2000] == 13 && table[2150] == 82 && table[2151] == 86, "protection boundaries", failures);

        Expect(ParseStabilization("sugerują zwykle dziewięćset jednostek. zalecane jest obniżenie poziomu o siedemset jedenaście.") == 189, "stab 189", failures);
        Expect(ParseStabilization("nastawa rzędu pięćset jednostek. należy zwiększyć tę nastawę o trzysta sześćdziesiąt cztery") == 864, "stab 864", failures);
        Expect(ParseStabilization("pojawia się poziom sześćset. zwiększyć tę nastawę o 395 punktów") == 995, "stab 995", failures);
        Expect(ParseStabilization("sugerują zwykle pięćset dwadzieścia pięć jednostek. podniesienie poziomu o 3") == 528, "stab 528", failures);
        Expect(ParseStabilization("nastawa rzędu pięćdziesiąt jednostek. podniesienie poziomu o około 37") == 87, "stab 87", failures);
        Expect(ParseStabilization("trzysta siedemdziesiąt punktów. zmniejszyć go o dziewięć") == 361, "stab 361", failures);
        Expect(ParseStabilization("nastawa rzędu tysiąc jednostek. odjąć dwieście dziewięćdziesiąt osiem") == 702, "stab 702", failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException("timetravel self-test failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
    }

    private static List<NumberHit> ReadNumbers(string text)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            tokens.Add(new Token(text[start..i], start));
        }

        var numbers = new List<NumberHit>();
        for (var index = 0; index < tokens.Count;)
        {
            if (!TryConsume(tokens, index, out var value, out var consumed))
            {
                index++;
                continue;
            }

            numbers.Add(new NumberHit(value, tokens[index].Start));
            index += consumed;
        }

        return numbers;
    }

    private static bool TryConsume(IReadOnlyList<Token> tokens, int index, out int value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (index >= tokens.Count)
        {
            return false;
        }

        var text = tokens[index].Text;
        if (text.All(char.IsDigit))
        {
            value = int.Parse(text, CultureInfo.InvariantCulture);
            consumed = 1;
            return true;
        }

        var cursor = index;
        var sum = 0;
        var any = false;
        if (Is(tokens, cursor, 1000))
        {
            sum += 1000;
            cursor++;
            any = true;
        }

        if (TryLex(tokens, cursor, out var hundreds) && hundreds is >= 100 and <= 900 && hundreds % 100 == 0)
        {
            sum += hundreds;
            cursor++;
            any = true;
        }

        if (TryLex(tokens, cursor, out var tail))
        {
            if (tail is >= 20 and <= 90 && tail % 10 == 0)
            {
                sum += tail;
                cursor++;
                any = true;
                if (TryLex(tokens, cursor, out var unit) && unit is >= 1 and <= 9)
                {
                    sum += unit;
                    cursor++;
                }
            }
            else if (tail is >= 10 and <= 19 || (tail == 0 && !any) || tail is >= 1 and <= 9)
            {
                sum += tail;
                cursor++;
                any = true;
            }
        }

        if (!any || cursor == index)
        {
            return false;
        }

        value = sum;
        consumed = cursor - index;
        return true;
    }

    private static bool Is(IReadOnlyList<Token> tokens, int index, int expected) =>
        TryLex(tokens, index, out var value) && value == expected;

    private static bool TryLex(IReadOnlyList<Token> tokens, int index, out int value)
    {
        value = 0;
        return index < tokens.Count && NumberWords.TryGetValue(tokens[index].Text, out value);
    }

    private static int IndexOfAny(string text, IEnumerable<string> markers)
    {
        var found = -1;
        foreach (var marker in markers)
        {
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0 && (found < 0 || at < found))
            {
                found = at;
            }
        }

        return found;
    }

    private static string StripDiacritics(string text)
    {
        var decomposed = text.Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static void Expect(bool condition, string name, List<string> failures)
    {
        if (!condition)
        {
            failures.Add(name);
        }
    }

    [GeneratedRegex(@"\|\s*(\d{4})\s*\|\s*(\d+)\s*")]
    private static partial Regex ProtectionRow();

    private readonly record struct Token(string Text, int Start);

    private readonly record struct NumberHit(int Value, int Start);
}
