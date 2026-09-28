using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Shellaccess;

public sealed class ShellaccessSolver
{
    private readonly ShellaccessApiClient _apiClient;
    private readonly ILogger<ShellaccessSolver> _logger;

    public ShellaccessSolver(ShellaccessApiClient apiClient, ILogger<ShellaccessSolver> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var listing = await RequireOutputAsync("ls -la /data", cancellationToken);
        _logger.LogInformation("Archive listing:{NewLine}{Listing}", Environment.NewLine, listing.Trim());

        var bodyLines = await RequireOutputAsync("grep -n 'ciało' /data/time_logs.csv", cancellationToken);
        if (string.IsNullOrWhiteSpace(bodyLines))
        {
            bodyLines = await RequireOutputAsync("grep -n 'znaleziono' /data/time_logs.csv", cancellationToken);
        }

        var discovery = ShellaccessArchive.SelectBodyDiscovery(bodyLines.Split('\n'));
        _logger.LogInformation(
            "Body discovered on {Date} at location {LocationId}, GPS entry {EntryId}: {Description}",
            discovery.Date,
            discovery.LocationId,
            discovery.EntryId,
            discovery.Description);

        var city = await RequireOutputAsync(
            $"jq -r '.[] | select(.location_id == {discovery.LocationId}) | .name' /data/locations.json",
            cancellationToken);
        var gps = await RequireOutputAsync(
            $"jq -r '.[] | select(.entry_id == {discovery.EntryId}) | [.latitude, .longitude, .location_id, .type] | @tsv' /data/gps.json",
            cancellationToken);

        var answer = ShellaccessArchive.BuildAnswer(discovery, city, gps);
        var json = ShellaccessArchive.FormatAnswer(answer);
        _logger.LogInformation("Submitting meeting point {Json}", json);

        var submitted = await _apiClient.RunAsync(ShellaccessArchive.EchoCommand(json), cancellationToken);
        if (submitted.Code != 0 || string.IsNullOrWhiteSpace(submitted.Message))
        {
            throw new InvalidOperationException($"Shellaccess did not accept the meeting point. {submitted.Describe()}");
        }

        return submitted.Message;
    }

    private async Task<string> RequireOutputAsync(string command, CancellationToken cancellationToken)
    {
        var reply = await _apiClient.RunAsync(command, cancellationToken);
        if (reply.Code != 100)
        {
            throw new InvalidOperationException($"Command '{command}' failed. {reply.Describe()}");
        }

        return reply.Output ?? string.Empty;
    }
}
