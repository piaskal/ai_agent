using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class CaptureAnalyzer
{
    private const int MaxContentChars = 20_000;

    private readonly RadiomonitoringVisionClient _visionClient;
    private readonly CaptureStore _store;
    private readonly ILogger<CaptureAnalyzer> _logger;

    public CaptureAnalyzer(
        RadiomonitoringVisionClient visionClient,
        CaptureStore store,
        ILogger<CaptureAnalyzer> logger)
    {
        _visionClient = visionClient;
        _store = store;
        _logger = logger;
    }

    public async Task<EvidenceItem?> AnalyzeAsync(RoutedCapture routed, CancellationToken cancellationToken = default)
    {
        if (routed.DecodedBytes is { Length: > 0 } bytes)
        {
            _store.SaveDecoded(routed.Sequence, SignalRouter.ExtensionForMime(routed.DetectedMime), bytes);
        }

        switch (routed.Kind)
        {
            case RouteKind.SessionComplete:
            case RouteKind.Noise:
            case RouteKind.UnknownBinary:
                _logger.LogInformation(
                    "Capture {Sequence} dropped as {Kind}: {Reason}",
                    routed.Sequence,
                    routed.Kind,
                    routed.DecisionReason);
                return null;

            case RouteKind.Transcript:
            case RouteKind.StructuredText:
            case RouteKind.Pdf:
            case RouteKind.Archive:
                if (string.IsNullOrWhiteSpace(routed.LocalText))
                {
                    _logger.LogInformation(
                        "Capture {Sequence} ({Kind}) had no extractable text.",
                        routed.Sequence,
                        routed.Kind);
                    return null;
                }

                return Keep(routed, routed.Kind.ToString(), routed.LocalText);

            case RouteKind.Image:
                try
                {
                    var description = await _visionClient.DescribeAsync(
                        routed.DecodedBytes!,
                        routed.DetectedMime ?? "image/png",
                        cancellationToken);
                    _store.SaveText(routed.Sequence, "vision", description);
                    return Keep(routed, routed.DetectedMime ?? "image", description);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Vision analysis failed for capture {Sequence}.", routed.Sequence);
                    return null;
                }

            default:
                return null;
        }
    }

    private EvidenceItem Keep(RoutedCapture routed, string source, string content)
    {
        var clipped = content.Length > MaxContentChars ? content[..MaxContentChars] : content;
        _logger.LogInformation(
            "Capture {Sequence} kept as {Kind} ({Chars} chars). {Reason}",
            routed.Sequence,
            routed.Kind,
            clipped.Length,
            routed.DecisionReason);
        return new EvidenceItem(routed.Sequence, routed.Kind, source, clipped);
    }
}
