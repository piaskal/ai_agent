using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Goingthere;

public sealed class GoingthereApiClient
{
    public const string TaskName = "goingthere";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string ScannerUrl = "https://hub.ag3nts.org/api/frequencyScanner";
    private const string HintUrl = "https://hub.ag3nts.org/api/getmessage";
    private const int MaxRetries = 8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<GoingthereApiClient> _logger;

    public GoingthereApiClient(HttpClient httpClient, IOptions<AgentToolOptions> options, ILogger<GoingthereApiClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public Task<GoingthereHttpReply> StartAsync(CancellationToken cancellationToken) =>
        VerifyAsync("start", cancellationToken);

    public Task<GoingthereHttpReply> MoveAsync(string command, CancellationToken cancellationToken) =>
        VerifyAsync(command, cancellationToken);

    public async Task<string> GetHintAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync(HttpMethod.Post, HintUrl, new { apikey = _apiKey }, cancellationToken);
        if (reply.Status != 200)
        {
            throw new InvalidOperationException($"Radio hint failed with status {reply.Status}. {Trim(reply.Body)}");
        }

        using var document = JsonDocument.Parse(reply.Body);
        var root = document.RootElement;
        var hint = root.TryGetProperty("hint", out var hintElement) ? hintElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(hint) && root.TryGetProperty("message", out var messageElement))
        {
            hint = messageElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(hint))
        {
            throw new InvalidOperationException($"Radio hint response had no hint. {Trim(reply.Body)}");
        }

        return hint;
    }

    public async Task<string> ScanAsync(CancellationToken cancellationToken)
    {
        var url = $"{ScannerUrl}?key={Uri.EscapeDataString(_apiKey)}";
        var reply = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        if (reply.Status != 200)
        {
            throw new InvalidOperationException($"Frequency scanner failed with status {reply.Status}. {Trim(reply.Body)}");
        }

        return reply.Body;
    }

    public Task<GoingthereHttpReply> DisarmAsync(int frequency, string detectionCode, CancellationToken cancellationToken)
    {
        var payload = new
        {
            apikey = _apiKey,
            frequency,
            disarmHash = GoingthereScanner.DisarmHash(detectionCode)
        };
        return SendAsync(HttpMethod.Post, ScannerUrl, payload, cancellationToken);
    }

    private Task<GoingthereHttpReply> VerifyAsync(string command, CancellationToken cancellationToken)
    {
        var payload = new
        {
            apikey = _apiKey,
            task = TaskName,
            answer = new { command }
        };
        return SendAsync(HttpMethod.Post, VerifyUrl, payload, cancellationToken);
    }

    private async Task<GoingthereHttpReply> SendAsync(HttpMethod method, string url, object? payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the goingthere API.");
        }

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload, options: JsonOptions);
            }

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode || IsGameRejection(response.StatusCode, body))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
                    return new GoingthereHttpReply((int)response.StatusCode, body);
                }

                if (!IsRetryable(response.StatusCode, body) || attempt == MaxRetries)
                {
                    throw new InvalidOperationException($"Goingthere API failed with status {(int)response.StatusCode}. {Trim(body)}");
                }

                var delay = TimeSpan.FromSeconds(Math.Min(20, 2 + (attempt - 1) * 2));
                _logger.LogWarning("Goingthere API returned {Status}. Retrying in {DelaySeconds}s.", (int)response.StatusCode, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(15, attempt));
                _logger.LogWarning(exception, "Goingthere request failed. Retrying in {DelaySeconds}s.", delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Unexpected retry flow in the goingthere API client.");
    }

    private static bool IsGameRejection(HttpStatusCode status, string body) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity
        && !body.Contains("Zwolnij", StringComparison.Ordinal);

    private static bool IsRetryable(HttpStatusCode status, string body) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
        || body.Contains("Zwolnij", StringComparison.Ordinal);

    private static string Trim(string body) => body.Length <= 300 ? body : body[..300];
}

public sealed record GoingthereHttpReply(int Status, string Body);
