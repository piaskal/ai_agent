using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Timetravel;

public sealed partial class TimetravelSolver
{
    private static readonly DateOnly BatteryDate = new(2238, 11, 5);
    private static readonly DateOnly TunnelDate = new(2024, 11, 12);

    private readonly TimetravelApiClient _api;
    private readonly ILogger<TimetravelSolver> _logger;

    public TimetravelSolver(TimetravelApiClient api, ILogger<TimetravelSolver> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<string> SolveAsync(CancellationToken cancellationToken = default)
    {
        var reset = await _api.ResetAsync(cancellationToken);
        EnsureSuccess(reset, "reset");

        var start = await ReadConfigAsync(cancellationToken);
        var today = start.CurrentDate;
        _logger.LogInformation(
            "Device is at {Date} with battery {Battery}.",
            today,
            start.BatteryStatus);

        var markdown = await _api.DownloadDocumentationAsync(cancellationToken);
        var protection = TimetravelMath.ParseProtectionTable(markdown);
        if (protection.Count != 1000)
        {
            throw new InvalidOperationException($"Protection table has {protection.Count} years; expected 1000.");
        }

        var charged = await JumpAsync("batteries", BatteryDate, tunnel: false, protection, cancellationToken);
        if (BatteryThirds(charged.BatteryStatus) < 3)
        {
            throw new InvalidOperationException(
                $"Expected a full battery after 2238-11-05, got {charged.BatteryStatus}.");
        }

        var returned = await JumpAsync("return", today, tunnel: false, protection, cancellationToken);
        if (returned.CurrentDate != today)
        {
            throw new InvalidOperationException($"Return jump landed on {returned.CurrentDate} instead of {today:yyyy-MM-dd}.");
        }

        var tunnel = await JumpAsync("tunnel", TunnelDate, tunnel: true, protection, cancellationToken);
        var flag = TryFlag(tunnel.Response) ?? throw new InvalidOperationException(
            "Tunnel opened without a flag: " + tunnel.Response.ToJsonString());
        _logger.LogInformation("Flag {Flag}", flag);
        return flag;
    }

    private async Task<JumpResult> JumpAsync(
        string label,
        DateOnly target,
        bool tunnel,
        IReadOnlyDictionary<int, int> protection,
        CancellationToken cancellationToken)
    {
        var before = await EnsureStandbyAsync(cancellationToken);
        if (target == before.CurrentDate)
        {
            throw new InvalidOperationException($"{label}: target {target:yyyy-MM-dd} is already the current date.");
        }

        var thirds = BatteryThirds(before.BatteryStatus);
        if (thirds <= 0)
        {
            throw new InvalidOperationException($"{label}: battery is empty.");
        }

        if (tunnel && thirds < 2)
        {
            throw new InvalidOperationException(
                $"{label}: a tunnel needs battery at least 2/3, current {before.BatteryStatus}.");
        }

        if (!protection.TryGetValue(target.Year, out var pwr))
        {
            throw new InvalidOperationException($"{label}: no protection level for year {target.Year}.");
        }

        var ratio = TimetravelMath.SyncRatio(target.Year, target.Month, target.Day);
        var (pta, ptb) = TimetravelMath.Ports(before.CurrentDate, target, tunnel);
        var expectedMode = TimetravelMath.InternalMode(target.Year);
        _logger.LogInformation(
            "{Label}: {Current} -> {Target} tunnel={Tunnel} sync={Sync} stabilization=pending pwr={Pwr} PTA={Pta} PTB={Ptb} internalMode={Mode}",
            label,
            before.CurrentDate,
            target,
            tunnel,
            ratio,
            pwr,
            pta,
            ptb,
            expectedMode);

        await ConfigureAsync("year", JsonValue.Create(target.Year), cancellationToken);
        await ConfigureAsync("month", JsonValue.Create(target.Month), cancellationToken);
        var dated = await ConfigureAsync("day", JsonValue.Create(target.Day), cancellationToken);
        var hint = dated.Body?["needConfig"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(hint))
        {
            throw new InvalidOperationException($"{label}: the full date did not return a stabilization hint.");
        }

        var stabilization = TimetravelMath.ParseStabilization(hint);
        _logger.LogInformation("{Label}: stabilization {Stabilization}.", label, stabilization);
        await ConfigureAsync("syncRatio", JsonValue.Create(ratio), cancellationToken);
        await ConfigureAsync("stabilization", JsonValue.Create(stabilization), cancellationToken);

        var controls = await _api.SetControlsAsync(mode: null, pta, ptb, pwr, cancellationToken);
        EnsureSuccess(controls, $"{label} controls");

        return await WaitAndTravelAsync(label, target, expectedMode, cancellationToken);
    }

    private async Task<JumpResult> WaitAndTravelAsync(
        string label,
        DateOnly target,
        int expectedMode,
        CancellationToken cancellationToken)
    {
        var lowFluxSamples = 0;
        string? lastRejection = null;
        for (var attempt = 1; attempt <= 45; attempt++)
        {
            var config = await ReadConfigAsync(cancellationToken);
            _logger.LogInformation(
                "{Label} wait {Attempt}: internalMode={InternalMode} flux={Flux} condition={Condition} mode={Mode}",
                label,
                attempt,
                config.InternalMode,
                config.FluxDensity,
                config.Condition,
                config.Mode);

            var phaseMatches = config.InternalMode == expectedMode;
            var ready = phaseMatches && config.FluxDensity == 100 && config.Condition == "stable";
            if (ready)
            {
                if (!string.Equals(config.Mode, "active", StringComparison.OrdinalIgnoreCase))
                {
                    var activated = await _api.SetControlsAsync("active", null, null, null, cancellationToken);
                    EnsureSuccess(activated, $"{label} activate");
                }

                var travel = await _api.TimeTravelAsync(cancellationToken);
                if (travel.IsSuccess && travel.Body?["code"]?.GetValue<int>() == 13 && travel.Body is not null)
                {
                    var landed = DeviceConfig.Parse(travel.Body);
                    if (landed.CurrentDate != target)
                    {
                        throw new InvalidOperationException(
                            $"{label}: landed on {landed.CurrentDate:yyyy-MM-dd}, expected {target:yyyy-MM-dd}.");
                    }

                    _logger.LogInformation(
                        "{Label}: arrived {Date}, battery {Battery}.",
                        label,
                        landed.CurrentDate,
                        landed.BatteryStatus);
                    return new JumpResult(landed, travel.Body);
                }

                lastRejection = travel.Body?["message"]?.ToString() ?? travel.Raw;
                if (IsFatalTravelError(lastRejection))
                {
                    throw new InvalidOperationException($"{label}: timeTravel rejected: {Trim(lastRejection)}");
                }

                _logger.LogWarning("{Label}: timeTravel not accepted yet: {Message}", label, Trim(lastRejection));
            }
            else if (phaseMatches && config.FluxDensity != 100)
            {
                lowFluxSamples++;
                if (lowFluxSamples >= 3)
                {
                    throw new InvalidOperationException(
                        $"{label}: flux stayed at {config.FluxDensity} while internalMode was {expectedMode}. {config}");
                }
            }
            else
            {
                lowFluxSamples = 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException(
            $"{label}: timed out waiting for a safe jump. Last rejection: {Trim(lastRejection)}");
    }

    private async Task<DeviceConfig> EnsureStandbyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var standby = await _api.SetControlsAsync("standby", null, null, null, cancellationToken);
            EnsureSuccess(standby, "standby");
            var config = await ReadConfigAsync(cancellationToken);
            if (string.Equals(config.Mode, "standby", StringComparison.OrdinalIgnoreCase))
            {
                return config;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        throw new InvalidOperationException("Device did not return to standby.");
    }

    private async Task<HubResponse> ConfigureAsync(string param, JsonNode value, CancellationToken cancellationToken)
    {
        var response = await _api.ConfigureAsync(param, value, cancellationToken);
        EnsureSuccess(response, $"configure {param}");
        return response;
    }

    private async Task<DeviceConfig> ReadConfigAsync(CancellationToken cancellationToken)
    {
        var response = await _api.GetConfigAsync(cancellationToken);
        EnsureSuccess(response, "getConfig");
        if (response.Body is null)
        {
            throw new InvalidOperationException("getConfig returned an empty body.");
        }

        return DeviceConfig.Parse(response.Body);
    }

    private static void EnsureSuccess(HubResponse response, string action)
    {
        if (response.IsSuccess)
        {
            return;
        }

        throw new InvalidOperationException($"{action} failed with status {response.StatusCode}: {Trim(response.Raw)}");
    }

    private static int BatteryThirds(string batteryStatus)
    {
        var slash = batteryStatus.IndexOf('/');
        if (slash <= 0 || !int.TryParse(batteryStatus[..slash], out var thirds))
        {
            throw new InvalidOperationException($"Unrecognised battery status '{batteryStatus}'.");
        }

        return thirds;
    }

    private static string? TryFlag(JsonObject body)
    {
        if (body["flag"] is JsonValue flag && flag.TryGetValue<string>(out var text) && text.Length > 0)
        {
            return text;
        }

        var match = FlagPattern().Match(body.ToJsonString());
        return match.Success ? match.Value : null;
    }

    private static bool IsFatalTravelError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("bater", StringComparison.OrdinalIgnoreCase)
            || message.Contains("battery", StringComparison.OrdinalIgnoreCase)
            || message.Contains("już tutaj", StringComparison.OrdinalIgnoreCase)
            || message.Contains("juz tutaj", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= 500 ? text : text[..500];
    }

    [GeneratedRegex(@"\{FLG:[^}]+\}")]
    private static partial Regex FlagPattern();

    private sealed record JumpResult(DeviceConfig Config, JsonObject Response)
    {
        public DeviceConfig Current => Config;
        public string BatteryStatus => Config.BatteryStatus;
        public DateOnly CurrentDate => Config.CurrentDate;
    }
}
