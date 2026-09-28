using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Goingthere;

public static partial class GoingthereScanner
{
    private static readonly string FrequencyKey = Skeleton("frequency");
    private static readonly string DetectionKey = Skeleton("detectioncode");

    public static string DisarmHash(string detectionCode) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(detectionCode + "disarm"))).ToLowerInvariant();

    public static bool IsClear(string text)
    {
        var letters = LettersOnly().Replace(text.ToLowerInvariant(), string.Empty);
        var collapsed = new StringBuilder(letters.Length);
        char previous = '\0';
        foreach (var character in letters)
        {
            if (character == previous)
            {
                continue;
            }

            collapsed.Append(character);
            previous = character;
        }

        var normalized = collapsed.ToString();
        return normalized.Contains("clear", StringComparison.Ordinal)
            || normalized.Contains("allclear", StringComparison.Ordinal)
            || normalized.EndsWith("safe", StringComparison.Ordinal);
    }

    public static GoingthereThreat? Parse(string text)
    {
        if (!text.Contains('{') && !text.Contains('`') && IsClear(text))
        {
            return null;
        }

        int? frequency = null;
        string? code = null;
        foreach (Match match in Field().Matches(text))
        {
            var kind = Skeleton(match.Groups[1].Value);
            var value = ReadValue(text[(match.Index + match.Length)..]);
            if (kind == FrequencyKey && frequency is null)
            {
                var digits = DigitsOnly().Replace(value, string.Empty);
                if (digits.Length > 0 && int.TryParse(digits, out var parsed))
                {
                    frequency = parsed;
                }
            }
            else if (kind == DetectionKey && code is null && value.Length > 0)
            {
                code = value.Trim();
            }
        }

        if (frequency is not null && code is not null)
        {
            return new GoingthereThreat(frequency.Value, code);
        }

        return IsClear(text) ? null : GoingthereThreat.Unparsed(text);
    }

    public static bool RunSelfTest(TextWriter output)
    {
        var failed = false;
        if (DisarmHash("CrRTVW") != "c64c1e38cfed7bae977e8c3cf9c2f5047fca98dc")
        {
            output.WriteLine("FAIL disarm hash");
            failed = true;
        }

        if (Parse("\"It's  cleeeear!\"") is not null || Parse("\"Its     clear\"") is not null)
        {
            output.WriteLine("FAIL clear scanner text was treated as a threat");
            failed = true;
        }

        (string Body, int Frequency, string Code)[] threats =
        [
            ("""
            {
                "bAtA": {
                    "wEAp0NtyPe": 'pursuit missile"
                    'BETecTI0nc0BE": "fibaZv"
                },
                "BeinGTRACkEb": true,
                "FrEPuenCy": 608
            }
            """, 608, "fibaZv"),
            ("""
            {
                "beingTracked": true
                `frequency": 417,
                "data": {
                    "weaponType": "self-guided missile",
                    "detectionCode`: "yPmujT"
                }
            }
            """, 417, "yPmujT"),
            ("""
            {
                "bEiNgTRaCKeD": true,
                "FREQuEncy": 672,
                "DATa": {
                    "weaPonTypE`: "self-guided missile"
                    "DETectioNCoDE": 'RvMDnE"
                }
            }
            """, 672, "RvMDnE"),
            ("""
            {
                "BeingtRACkeD": true,
                "fReQuEncy": 751,
                "dATA": {
                    "wEAPoNTYPe`: "pursuit missile"
                    "dEtecTioNcOde": "gIRsP7`
                }
            }
            """, 751, "gIRsP7")
        ];

        foreach (var (body, expectedFrequency, expectedCode) in threats)
        {
            var threat = Parse(body);
            if (threat is null || !threat.IsLock || threat.Frequency != expectedFrequency || threat.DetectionCode != expectedCode)
            {
                output.WriteLine($"FAIL scanner parse expected {expectedFrequency}/{expectedCode} got {threat}");
                failed = true;
            }
        }

        output.WriteLine(failed ? "scanner self-test failed" : "scanner self-test passed");
        return !failed;
    }

    private static string ReadValue(string rest)
    {
        rest = rest.TrimStart();
        if (rest.Length == 0)
        {
            return string.Empty;
        }

        if (rest[0] is '"' or '\'' or '`')
        {
            rest = rest[1..];
        }

        var match = ValueToken().Match(rest);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private static string Skeleton(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            var mapped = character switch
            {
                'd' or 'b' or '8' => 'd',
                'o' or '0' => 'o',
                'p' or 'q' => 'p',
                'i' or 'l' or '1' or '|' => 'i',
                'e' or '3' => 'e',
                'a' or '4' or '@' => 'a',
                's' or '5' or '$' => 's',
                't' or '7' or '+' => 't',
                'g' or '6' or '9' => 'g',
                'z' or '2' => 'z',
                _ => character
            };
            if (char.IsAsciiLetterOrDigit(mapped))
            {
                builder.Append(mapped);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"[^a-z]")]
    private static partial Regex LettersOnly();

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9_]{2,})[""'`]?\s*[:=]")]
    private static partial Regex Field();

    [GeneratedRegex(@"([^,""'`\n]+)")]
    private static partial Regex ValueToken();
}

public sealed record GoingthereThreat(int Frequency, string DetectionCode, bool IsLock)
{
    public static GoingthereThreat Unparsed(string raw) => new(0, raw, false);

    public GoingthereThreat(int frequency, string detectionCode) : this(frequency, detectionCode, true)
    {
    }

    public override string ToString() => IsLock ? $"{Frequency}/{DetectionCode}" : "unparsed";
}
