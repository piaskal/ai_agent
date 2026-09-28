using System.Text.Json;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public sealed class PhonecallReply
{
    public int? Code { get; init; }
    public string? Message { get; init; }
    public string? Transcription { get; init; }
    public string? Hint { get; init; }
    public string? AudioBase64 { get; init; }

    public bool HasFlag =>
        (Message ?? string.Empty).Contains("FLG:", StringComparison.OrdinalIgnoreCase);

    public static PhonecallReply Parse(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;
            return new PhonecallReply
            {
                Code = ReadInt(root, "code"),
                Message = ReadString(root, "message"),
                Transcription = ReadString(root, "transcription"),
                Hint = ReadString(root, "hint"),
                AudioBase64 = ReadString(root, "audio")
            };
        }
        catch (JsonException)
        {
            return new PhonecallReply { Message = responseBody };
        }
    }

    public string Describe()
    {
        var parts = new List<string> { $"code={Code}" };
        if (!string.IsNullOrWhiteSpace(Message))
        {
            parts.Add($"message={Truncate(Message)}");
        }

        if (!string.IsNullOrWhiteSpace(Transcription))
        {
            parts.Add($"transcription={Truncate(Transcription)}");
        }

        if (!string.IsNullOrWhiteSpace(Hint))
        {
            parts.Add($"hint={Truncate(Hint)}");
        }

        if (!string.IsNullOrWhiteSpace(AudioBase64))
        {
            parts.Add($"audioChars={AudioBase64.Length}");
        }

        return string.Join(" ", parts);
    }

    public object ToPublicObject(string? operatorTranscript) => new
    {
        code = Code,
        message = Message,
        transcription = Transcription,
        hint = Hint,
        operatorTranscript
    };

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string Truncate(string value)
    {
        const int max = 240;
        return value.Length <= max ? value : value[..max] + "...";
    }
}
