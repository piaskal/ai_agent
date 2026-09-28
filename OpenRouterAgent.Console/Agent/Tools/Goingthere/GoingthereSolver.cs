using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Goingthere;

public sealed partial class GoingthereSolver
{
    private const int MaxAttempts = 8;

    private readonly GoingthereApiClient _apiClient;
    private readonly ILogger<GoingthereSolver> _logger;

    public GoingthereSolver(GoingthereApiClient apiClient, ILogger<GoingthereSolver> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public static bool RunSelfTest(TextWriter output)
    {
        var radio = GoingthereRadio.RunSelfTest(output);
        var scanner = GoingthereScanner.RunSelfTest(output);
        var navigator = GoingthereNavigator.RunSelfTest(output);
        return radio && scanner && navigator;
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            _logger.LogInformation("Goingthere attempt {Attempt}", attempt);
            var flag = await PlayOnceAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(flag))
            {
                return flag;
            }
        }

        throw new InvalidOperationException("Goingthere did not reach Grudziądz.");
    }

    private async Task<string?> PlayOnceAsync(CancellationToken cancellationToken)
    {
        var started = await _apiClient.StartAsync(cancellationToken);
        _logger.LogInformation("Start {Status} {Body}", started.Status, Trim(started.Body));
        if (!TryReadPosition(started.Body, out var row, out var column, out var stoneRow, out var baseRow) || baseRow is null)
        {
            return null;
        }

        var targetRow = baseRow.Value;
        for (var step = 0; step < 16; step++)
        {
            if (!await ClearRadarAsync(cancellationToken))
            {
                return null;
            }

            var hint = await _apiClient.GetHintAsync(cancellationToken);
            var danger = GoingthereRadio.Classify(hint);
            var move = GoingthereNavigator.ChooseMove(row, column, stoneRow, targetRow, danger);
            _logger.LogInformation(
                "Position r{Row}c{Column} stone {Stone} base {Base} danger {Danger} move {Move}. Hint: {Hint}",
                row,
                column,
                stoneRow,
                targetRow,
                danger ?? "unknown",
                move ?? "none",
                hint);
            if (danger is null || move is null)
            {
                return null;
            }

            var moved = await _apiClient.MoveAsync(move, cancellationToken);
            _logger.LogInformation("Move {Command} {Status} {Body}", move, moved.Status, Trim(moved.Body));
            var flag = FindFlag(moved.Body);
            if (flag is not null)
            {
                return flag;
            }

            if (moved.Status >= 400 || !TryReadPosition(moved.Body, out row, out column, out stoneRow, out _))
            {
                return null;
            }

            if (column >= GoingthereNavigator.Columns)
            {
                return FindFlag(moved.Body) ?? moved.Body;
            }
        }

        return null;
    }

    private async Task<bool> ClearRadarAsync(CancellationToken cancellationToken)
    {
        for (var scan = 0; scan < 6; scan++)
        {
            var body = await _apiClient.ScanAsync(cancellationToken);
            var threat = GoingthereScanner.Parse(body);
            if (threat is null)
            {
                _logger.LogInformation("Scanner is clear");
                return true;
            }

            if (!threat.IsLock)
            {
                _logger.LogInformation("Scanner reply was unreadable. Retrying.");
                continue;
            }

            var disarmed = await _apiClient.DisarmAsync(threat.Frequency, threat.DetectionCode, cancellationToken);
            _logger.LogInformation("Disarm {Frequency}/{Code} -> {Status} {Body}", threat.Frequency, threat.DetectionCode, disarmed.Status, Trim(disarmed.Body));
            if (disarmed.Status < 400 && !NegativeCode().IsMatch(disarmed.Body))
            {
                return true;
            }
        }

        _logger.LogWarning("Could not clear the radar.");
        return false;
    }

    private static bool TryReadPosition(string body, out int row, out int column, out int stoneRow, out int? baseRow)
    {
        row = 0;
        column = 0;
        stoneRow = 0;
        baseRow = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("player", out var player))
            {
                return false;
            }

            row = player.GetProperty("row").GetInt32();
            column = player.GetProperty("col").GetInt32();
            stoneRow = root.GetProperty("currentColumn").GetProperty("stoneRow").GetInt32();
            if (root.TryGetProperty("base", out var baseElement) && baseElement.TryGetProperty("row", out var baseRowElement))
            {
                baseRow = baseRowElement.GetInt32();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FindFlag(string body)
    {
        var match = FlagPattern().Match(body);
        return match.Success ? match.Value : null;
    }

    private static string Trim(string body) => body.Length <= 500 ? body : body[..500];

    [GeneratedRegex(@"\{FLG:[^}]+\}")]
    private static partial Regex FlagPattern();

    [GeneratedRegex("\"code\"\\s*:\\s*-")]
    private static partial Regex NegativeCode();
}
