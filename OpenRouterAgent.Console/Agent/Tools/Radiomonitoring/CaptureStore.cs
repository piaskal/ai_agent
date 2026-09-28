using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class CaptureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<CaptureStore> _logger;
    private readonly Lazy<string> _sessionDirectory;

    public CaptureStore(ILogger<CaptureStore> logger)
    {
        _logger = logger;
        _sessionDirectory = new Lazy<string>(() =>
        {
            var sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var directory = Path.Combine("logs", "radiomonitoring", sessionId);
            Directory.CreateDirectory(directory);
            _logger.LogInformation("Radiomonitoring captures will be stored in {Directory}.", Path.GetFullPath(directory));
            return directory;
        });
    }

    public string SessionDirectory => _sessionDirectory.Value;

    public void SaveRaw(int sequence, RadioCapture capture)
    {
        var path = Path.Combine(SessionDirectory, $"{sequence:D4}-raw.json");
        var sanitized = SanitizeForDisk(capture);
        File.WriteAllText(path, JsonSerializer.Serialize(sanitized, JsonOptions));
    }

    public void SaveDecoded(int sequence, string extension, byte[] bytes)
    {
        var safeExtension = string.IsNullOrWhiteSpace(extension) ? "bin" : extension.TrimStart('.');
        var path = Path.Combine(SessionDirectory, $"{sequence:D4}-decoded.{safeExtension}");
        File.WriteAllBytes(path, bytes);
    }

    public void SaveText(int sequence, string name, string content)
    {
        var path = Path.Combine(SessionDirectory, $"{sequence:D4}-{name}.txt");
        File.WriteAllText(path, content);
    }

    public void SaveEvidence(IReadOnlyList<EvidenceItem> items)
    {
        var path = Path.Combine(SessionDirectory, "evidence.json");
        File.WriteAllText(path, JsonSerializer.Serialize(items, JsonOptions));
    }

    public void SaveReport(RadioReport report, string hubResponse)
    {
        var path = Path.Combine(SessionDirectory, "report.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    report,
                    hubResponse
                },
                JsonOptions));
    }

    private static object SanitizeForDisk(RadioCapture capture)
    {
        return new
        {
            capture.Code,
            capture.Message,
            capture.Transcription,
            capture.Meta,
            capture.Filesize,
            attachment = capture.HasAttachment ? $"[base64 omitted, {capture.Attachment!.Length} chars]" : null
        };
    }
}
