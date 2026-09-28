using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public static class SignalRouter
{
    private static readonly Regex RepeatedChar = new(@"(.)\1{8,}", RegexOptions.Compiled);
    private static readonly string[] SessionCompleteMarkers =
    [
        "wystarczająco",
        "wystarczajaco",
        "enough data",
        "enough material",
        "no more",
        "end of intercept",
        "end of transmission",
        "transmission complete",
        "listening complete",
        "session complete",
        "koniec nasłuchu",
        "koniec nasluchu",
        "brak kolejnych",
        "no further",
        "pool is empty"
    ];

    public static bool IsSessionComplete(RadioCapture capture)
    {
        var message = capture.Message ?? string.Empty;
        var haystack = message.ToLowerInvariant();
        if (SessionCompleteMarkers.Any(marker => haystack.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        if (capture.HasTranscription || capture.HasAttachment)
        {
            return false;
        }

        return capture.Code is not null and not 100;
    }

    public static RoutedCapture Route(int sequence, RadioCapture capture)
    {
        if (IsSessionComplete(capture))
        {
            return new RoutedCapture(
                sequence,
                RouteKind.SessionComplete,
                capture,
                capture.Message,
                null,
                null,
                "Hub indicated the listening session is complete.");
        }

        if (capture.HasTranscription)
        {
            var transcription = capture.Transcription!.Trim();
            if (IsNoise(transcription))
            {
                return new RoutedCapture(
                    sequence,
                    RouteKind.Noise,
                    capture,
                    transcription,
                    null,
                    "text/plain",
                    "Transcription looks like radio static.");
            }

            return new RoutedCapture(
                sequence,
                RouteKind.Transcript,
                capture,
                transcription,
                null,
                "text/plain",
                "Kept spoken transcript.");
        }

        if (!capture.HasAttachment)
        {
            var message = capture.Message?.Trim();
            if (string.IsNullOrWhiteSpace(message) || IsNoise(message))
            {
                return new RoutedCapture(
                    sequence,
                    RouteKind.Noise,
                    capture,
                    message,
                    null,
                    null,
                    "No usable payload.");
            }

            return new RoutedCapture(
                sequence,
                RouteKind.Transcript,
                capture,
                message,
                null,
                "text/plain",
                "Kept hub message text.");
        }

        if (!TryDecodeBase64(capture.Attachment!, out var bytes))
        {
            return new RoutedCapture(
                sequence,
                RouteKind.UnknownBinary,
                capture,
                null,
                null,
                capture.Meta,
                "Attachment was not valid Base64.");
        }

        var mime = BinarySniffer.Detect(bytes, capture.Meta);
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new RoutedCapture(
                sequence,
                RouteKind.Image,
                capture,
                null,
                bytes,
                mime,
                "Binary sniffed as an image.");
        }

        if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pdfText = ExtractPdfStrings(bytes);
            return new RoutedCapture(
                sequence,
                RouteKind.Pdf,
                capture,
                string.IsNullOrWhiteSpace(pdfText) ? null : pdfText,
                bytes,
                mime,
                "Binary sniffed as PDF; extracted printable strings locally.");
        }

        if (mime.Equals("application/zip", StringComparison.OrdinalIgnoreCase))
        {
            var archiveText = ExtractArchiveText(bytes);
            return new RoutedCapture(
                sequence,
                RouteKind.Archive,
                capture,
                archiveText,
                bytes,
                mime,
                "Binary sniffed as a zip/office archive; extracted text locally.");
        }

        if (mime.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("application/csv", StringComparison.OrdinalIgnoreCase))
        {
            var text = DecodeUtf8(bytes);
            var normalized = TryPrettyJson(text) ?? text;
            return new RoutedCapture(
                sequence,
                RouteKind.StructuredText,
                capture,
                normalized,
                bytes,
                mime,
                "Decoded locally as structured or plain text.");
        }

        if (LooksLikeText(bytes))
        {
            return new RoutedCapture(
                sequence,
                RouteKind.StructuredText,
                capture,
                DecodeUtf8(bytes),
                bytes,
                mime,
                "Decoded locally as UTF-8 text.");
        }

        return new RoutedCapture(
            sequence,
            RouteKind.UnknownBinary,
            capture,
            null,
            bytes,
            mime,
            "Unknown binary; not sent to a model.");
    }

    public static bool IsNoise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.Trim();
        if (trimmed.Length < 12)
        {
            return true;
        }

        var letterCount = trimmed.Count(char.IsLetter);
        if (letterCount < 8)
        {
            return true;
        }

        if ((double)letterCount / trimmed.Length < 0.35)
        {
            return true;
        }

        var distinctLetters = trimmed.Where(char.IsLetter).Select(char.ToLowerInvariant).Distinct().Count();
        if (distinctLetters < 4)
        {
            return true;
        }

        if (RepeatedChar.IsMatch(trimmed))
        {
            return true;
        }

        var lower = trimmed.ToLowerInvariant();
        return lower is "szum" or "static" or "noise" or "krz" or "zzz";
    }

    public static string ExtensionForMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "application/pdf" => "pdf",
        "application/zip" => "zip",
        "application/json" => "json",
        "text/csv" => "csv",
        "text/html" => "html",
        "text/xml" or "application/xml" => "xml",
        "text/plain" => "txt",
        _ => "bin"
    };

    internal static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        var compact = value.Trim().Replace("\r", string.Empty).Replace("\n", string.Empty).Replace(" ", string.Empty);
        try
        {
            bytes = Convert.FromBase64String(compact);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes).Trim('\uFEFF');
    }

    private static string? TryPrettyJson(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var length = Math.Min(bytes.Length, 2048);
        var control = 0;
        for (var i = 0; i < length; i++)
        {
            var b = bytes[i];
            if (b < 9 || (b > 13 && b < 32))
            {
                control++;
            }
        }

        return control / (double)length < 0.02;
    }

    private static string ExtractPdfStrings(byte[] bytes)
    {
        var builder = new StringBuilder();
        var current = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b is >= 32 and <= 126 or >= 192)
            {
                current.Append((char)b);
                continue;
            }

            if (current.Length >= 6)
            {
                builder.AppendLine(current.ToString());
            }

            current.Clear();
        }

        if (current.Length >= 6)
        {
            builder.AppendLine(current.ToString());
        }

        return builder.ToString().Trim();
    }

    private static string ExtractArchiveText(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var builder = new StringBuilder();
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.Length <= 0)
                {
                    continue;
                }

                var name = entry.FullName.ToLowerInvariant();
                if (name.Contains("..") || entry.Length > 2_000_000)
                {
                    continue;
                }

                var extractable = name.EndsWith(".txt") || name.EndsWith(".json") || name.EndsWith(".xml") ||
                                  name.EndsWith(".csv") || name.EndsWith(".md") || name.EndsWith(".html") ||
                                  name.Contains("sharedstring") || name.Contains("document.xml") ||
                                  name.Contains("sheet");
                if (!extractable)
                {
                    continue;
                }

                using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                builder.AppendLine($"# {entry.FullName}");
                builder.AppendLine(content.Length > 50_000 ? content[..50_000] : content);
                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
    }
}

internal static class BinarySniffer
{
    public static string Detect(ReadOnlySpan<byte> bytes, string? declaredMeta)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        if (bytes.Length >= 5 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F')
        {
            return "application/pdf";
        }

        if (bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            return "application/zip";
        }

        var text = Encoding.UTF8.GetString(bytes.Length > 256 ? bytes[..256] : bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            return "application/json";
        }

        if (!string.IsNullOrWhiteSpace(declaredMeta) &&
            (declaredMeta.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
             declaredMeta.Contains("json", StringComparison.OrdinalIgnoreCase) ||
             declaredMeta.Contains("xml", StringComparison.OrdinalIgnoreCase)))
        {
            return declaredMeta;
        }

        return string.IsNullOrWhiteSpace(declaredMeta) ? "application/octet-stream" : declaredMeta;
    }
}
