using System.Text.Json.Serialization;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class RadioCapture
{
    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("transcription")]
    public string? Transcription { get; set; }

    [JsonPropertyName("meta")]
    public string? Meta { get; set; }

    [JsonPropertyName("attachment")]
    public string? Attachment { get; set; }

    [JsonPropertyName("filesize")]
    public long? Filesize { get; set; }

    [JsonIgnore]
    public string RawJson { get; set; } = string.Empty;

    public bool HasTranscription => !string.IsNullOrWhiteSpace(Transcription);

    public bool HasAttachment => !string.IsNullOrWhiteSpace(Attachment);
}

public enum RouteKind
{
    SessionComplete,
    Noise,
    Transcript,
    StructuredText,
    Image,
    Pdf,
    Archive,
    UnknownBinary
}

public sealed record RoutedCapture(
    int Sequence,
    RouteKind Kind,
    RadioCapture Capture,
    string? LocalText,
    byte[]? DecodedBytes,
    string? DetectedMime,
    string DecisionReason);

public sealed record EvidenceItem(
    int Sequence,
    RouteKind Kind,
    string Source,
    string Content);

public sealed class RadioReportDraft
{
    public string? CityName { get; set; }
    public string? CityAreaRaw { get; set; }
    public decimal? AreaLength { get; set; }
    public decimal? AreaWidth { get; set; }
    public int? WarehousesCount { get; set; }
    public string[] WarehouseNames { get; set; } = [];
    public string? PhoneNumber { get; set; }
    public string? Notes { get; set; }
}

public sealed class RadioReport
{
    public string CityName { get; set; } = string.Empty;
    public string CityArea { get; set; } = string.Empty;
    public int WarehousesCount { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
}
