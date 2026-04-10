using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Filesystem;

public sealed class GetNatanNotesTool : IAgentTool
{
    public const string ToolName = "get_natan_notes";
    private const string NotesZipUrl = "https://hub.ag3nts.org/dane/natan_notes.zip";
    private const int DefaultMaxTotalChars = 80_000;
    private const int MinMaxTotalChars = 2_000;
    private const int MaxMaxTotalChars = 300_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".markdown",
        ".json",
        ".csv",
        ".log",
        ".xml",
        ".yaml",
        ".yml"
    };

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Downloads and extracts natan notes from https://hub.ag3nts.org/dane/natan_notes.zip and returns textual contents for LLM analysis.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    max_total_chars = new
                    {
                        type = "integer",
                        description = "Optional maximum number of characters returned across all extracted files.",
                        minimum = MinMaxTotalChars,
                        maximum = MaxMaxTotalChars
                    }
                },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var maxTotalChars = ParseMaxTotalChars(toolCall.Function.Arguments);

        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(NotesZipUrl, cancellationToken);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = Encoding.UTF8.GetString(responseBody);
            throw new InvalidOperationException(
                $"Failed to download natan notes from '{NotesZipUrl}'. Status: {(int)response.StatusCode} ({response.StatusCode}). Response: {errorText}");
        }

        using var zipStream = new MemoryStream(responseBody);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        return new ToolExecutionResult(BuildResultContent(archive, maxTotalChars));
    }

    private static int ParseMaxTotalChars(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return DefaultMaxTotalChars;

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("max_total_chars", out var valueElement) || valueElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return DefaultMaxTotalChars;

        if (valueElement.ValueKind != JsonValueKind.Number || !valueElement.TryGetInt32(out var maxTotalChars))
            throw new InvalidOperationException("Argument 'max_total_chars' must be an integer.");

        if (maxTotalChars < MinMaxTotalChars || maxTotalChars > MaxMaxTotalChars)
            throw new InvalidOperationException($"Argument 'max_total_chars' must be between {MinMaxTotalChars} and {MaxMaxTotalChars}.");

        return maxTotalChars;
    }

    private static string BuildResultContent(ZipArchive archive, int maxTotalChars)
    {
        var files = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
            return "Downloaded natan notes archive, but no files were found inside the ZIP.";

        var sb = new StringBuilder();
        sb.AppendLine("Natan notes were downloaded and extracted from https://hub.ag3nts.org/dane/natan_notes.zip.");
        sb.AppendLine("Included files:");

        foreach (var file in files)
            sb.AppendLine($"- {file.FullName} ({file.Length} bytes)");

        sb.AppendLine();
        sb.AppendLine("Extracted textual content:");

        var remainingChars = Math.Max(0, maxTotalChars - sb.Length);
        var truncated = false;

        foreach (var entry in files)
        {
            if (remainingChars <= 0)
            {
                truncated = true;
                break;
            }

            if (!IsTextEntry(entry))
                continue;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();

            var header = $"\n=== FILE: {entry.FullName} ===\n";
            if (header.Length >= remainingChars)
            {
                truncated = true;
                break;
            }

            sb.Append(header);
            remainingChars -= header.Length;

            if (content.Length > remainingChars)
            {
                sb.Append(content.AsSpan(0, remainingChars));
                truncated = true;
                remainingChars = 0;
                break;
            }

            sb.Append(content);
            remainingChars -= content.Length;
        }

        if (truncated)
            sb.AppendLine("\n\n[Output truncated due to max_total_chars limit.]\n");

        return sb.ToString();
    }

    private static bool IsTextEntry(ZipArchiveEntry entry)
    {
        var extension = Path.GetExtension(entry.Name);
        return TextExtensions.Contains(extension);
    }
}