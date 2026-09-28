using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class RadiomonitoringOrchestrator
{
    private const int MaxCaptures = 250;

    private readonly RadiomonitoringApiClient _apiClient;
    private readonly CaptureStore _store;
    private readonly CaptureAnalyzer _analyzer;
    private readonly ReportExtractor _extractor;
    private readonly ILogger<RadiomonitoringOrchestrator> _logger;

    public RadiomonitoringOrchestrator(
        RadiomonitoringApiClient apiClient,
        CaptureStore store,
        CaptureAnalyzer analyzer,
        ReportExtractor extractor,
        ILogger<RadiomonitoringOrchestrator> logger)
    {
        _apiClient = apiClient;
        _store = store;
        _analyzer = analyzer;
        _extractor = extractor;
        _logger = logger;
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var evidence = new List<EvidenceItem>();
        var dropped = 0;

        var start = await _apiClient.StartAsync(cancellationToken);
        _store.SaveRaw(0, start);
        _logger.LogInformation("Started radiomonitoring session: {Capture}", RadiomonitoringApiClient.DescribeForLog(start));

        for (var sequence = 1; sequence <= MaxCaptures; sequence++)
        {
            var capture = await _apiClient.ListenAsync(cancellationToken);
            _store.SaveRaw(sequence, capture);
            _logger.LogInformation("Listen {Sequence}: {Capture}", sequence, RadiomonitoringApiClient.DescribeForLog(capture));

            if (SignalRouter.IsSessionComplete(capture))
            {
                _logger.LogInformation("Listening finished at sequence {Sequence}: {Message}", sequence, capture.Message);
                break;
            }

            var routed = SignalRouter.Route(sequence, capture);
            var item = await _analyzer.AnalyzeAsync(routed, cancellationToken);
            if (item is null)
            {
                dropped++;
                continue;
            }

            evidence.Add(item);
        }

        _store.SaveEvidence(evidence);
        _logger.LogInformation(
            "Collected {Kept} evidence items, dropped {Dropped}. Session directory: {Directory}",
            evidence.Count,
            dropped,
            _store.SessionDirectory);

        var report = await _extractor.ExtractAsync(evidence, cancellationToken);
        _logger.LogInformation(
            "Prepared report cityName={CityName} cityArea={CityArea} warehousesCount={WarehousesCount} phoneNumber={PhoneNumber}",
            report.CityName,
            report.CityArea,
            report.WarehousesCount,
            report.PhoneNumber);

        var hub = await _apiClient.TransmitAsync(report, cancellationToken);
        _store.SaveReport(report, hub.RawJson);
        _logger.LogInformation("Transmit response: {Response}", RadiomonitoringApiClient.Truncate(hub.RawJson, 1000));

        return JsonSerializer.Serialize(
            new
            {
                sessionDirectory = _store.SessionDirectory,
                kept = evidence.Count,
                dropped,
                report,
                hubResponse = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(hub.RawJson) ? "{}" : hub.RawJson)
            },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
