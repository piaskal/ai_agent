using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Timetravel;

public sealed class TimetravelApiClient
{
    public const string HttpClientName = "timetravel";
    private const string TaskName = "timetravel";
    private const int MaxAttempts = 6;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly ILogger<TimetravelApiClient> _logger;

    public TimetravelApiClient(
        IHttpClientFactory httpClientFactory,
        IOptions<AgentToolOptions> options,
        ILogger<TimetravelApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public async Task<HubResponse> ResetAsync(CancellationToken cancellationToken) =>
        await VerifyAsync(Action("reset"), cancellationToken);

    public async Task<HubResponse> GetConfigAsync(CancellationToken cancellationToken) =>
        await VerifyAsync(Action("getConfig"), cancellationToken);

    public async Task<HubResponse> TimeTravelAsync(CancellationToken cancellationToken) =>
        await VerifyAsync(Action("timeTravel"), cancellationToken);

    public async Task<HubResponse> ConfigureAsync(string param, JsonNode value, CancellationToken cancellationToken)
    {
        var answer = new JsonObject
        {
            ["action"] = "configure",
            ["param"] = param,
            ["value"] = value
        };
        return await VerifyAsync(answer, cancellationToken);
    }

    public async Task<HubResponse> SetControlsAsync(
        string? mode,
        bool? pta,
        bool? ptb,
        int? pwr,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["apikey"] = _apiKey
        };
        if (mode is not null)
        {
            body["mode"] = mode;
        }

        if (pta is bool portA)
        {
            body["PTA"] = portA;
        }

        if (ptb is bool portB)
        {
            body["PTB"] = portB;
        }

        if (pwr is int power)
        {
            body["PWR"] = power;
        }

        return await SendAsync(HttpMethod.Post, "timetravel_backend", body, cancellationToken);
    }

    public async Task<string> DownloadDocumentationAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync("dane/timetravel.md", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Documentation download failed with status {(int)response.StatusCode}: {Trim(body)}");
        }

        return body;
    }

    private async Task<HubResponse> VerifyAsync(JsonObject answer, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["apikey"] = _apiKey,
            ["task"] = TaskName,
            ["answer"] = answer
        };
        return await SendAsync(HttpMethod.Post, "verify", payload, cancellationToken);
    }

    private async Task<HubResponse> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? payload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required for the timetravel device.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, path);
                if (payload is not null)
                {
                    request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                }

                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode || !IsRetryable(response.StatusCode) || attempt == MaxAttempts)
                {
                    return new HubResponse(status, TryParse(body), body);
                }

                var delay = RetryDelay(response, attempt);
                _logger.LogWarning(
                    "Timetravel hub returned {StatusCode}. Retrying in {DelaySeconds:0}s (attempt {Next}/{Max}).",
                    status,
                    delay.TotalSeconds,
                    attempt + 1,
                    MaxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                _logger.LogWarning(
                    exception,
                    "Timetravel hub request failed. Retrying in {DelaySeconds:0}s (attempt {Next}/{Max}).",
                    delay.TotalSeconds,
                    attempt + 1,
                    MaxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                _logger.LogWarning(
                    exception,
                    "Timetravel hub request timed out. Retrying in {DelaySeconds:0}s (attempt {Next}/{Max}).",
                    delay.TotalSeconds,
                    attempt + 1,
                    MaxAttempts);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Timetravel hub request exhausted retries.");
    }

    private static JsonObject Action(string action) => new()
    {
        ["action"] = action
    };

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                return wait;
            }
        }

        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
    }

    private static JsonObject? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string body) =>
        body.Length <= 500 ? body : body[..500];
}

public sealed record HubResponse(int StatusCode, JsonObject? Body, string Raw)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
