using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class RadiomonitoringApiClient
{
    public const string TaskName = "radiomonitoring";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const int MaxRetries = 3;
    private const int QueuePollIntervalMs = 1000;
    private const int MinQueuePollingSeconds = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<RadiomonitoringApiClient> _logger;

    public RadiomonitoringApiClient(
        HttpClient httpClient,
        IOptions<AgentToolOptions> options,
        ILogger<RadiomonitoringApiClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public Task<RadioCapture> StartAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new { action = "start" }, cancellationToken);

    public Task<RadioCapture> ListenAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new { action = "listen" }, cancellationToken);

    public Task<RadioCapture> TransmitAsync(RadioReport report, CancellationToken cancellationToken = default) =>
        SendAsync(
            new
            {
                action = "transmit",
                cityName = report.CityName,
                cityArea = report.CityArea,
                warehousesCount = report.WarehousesCount,
                phoneNumber = report.PhoneNumber
            },
            cancellationToken);

    private async Task<RadioCapture> SendAsync(object answer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the radiomonitoring API.");
        }

        var payload = new
        {
            apikey = _apiKey,
            task = TaskName,
            answer
        };

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var response = await _httpClient.PostAsJsonAsync(VerifyUrl, payload, JsonOptions, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                if (IsQueuedResponse(responseBody))
                {
                    responseBody = await WaitForQueuedResponseAsync(payload, cancellationToken);
                }

                return ParseCapture(responseBody);
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Radiomonitoring API request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {Truncate(responseBody)}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Radiomonitoring API returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in radiomonitoring API client.");
    }

    private async Task<string> WaitForQueuedResponseAsync(object payload, CancellationToken cancellationToken)
    {
        var queuePollingDeadline = DateTime.UtcNow.AddSeconds(MinQueuePollingSeconds);
        var responseBody = string.Empty;

        while (DateTime.UtcNow < queuePollingDeadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(QueuePollIntervalMs), cancellationToken);
            using var queueResponse = await _httpClient.PostAsJsonAsync(VerifyUrl, payload, JsonOptions, cancellationToken);
            responseBody = await queueResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!queueResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Radiomonitoring API request failed with status {(int)queueResponse.StatusCode} ({queueResponse.StatusCode}) while waiting for queued response. Response: {Truncate(responseBody)}");
            }

            if (!IsQueuedResponse(responseBody))
            {
                return responseBody;
            }
        }

        return responseBody;
    }

    internal static RadioCapture ParseCapture(string responseBody)
    {
        try
        {
            var capture = JsonSerializer.Deserialize<RadioCapture>(responseBody, JsonOptions) ?? new RadioCapture();
            capture.RawJson = responseBody;
            return capture;
        }
        catch (JsonException)
        {
            return new RadioCapture
            {
                Message = responseBody,
                RawJson = responseBody
            };
        }
    }

    private static bool IsQueuedResponse(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;
            if (!root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            if (codeElement.GetInt32() != 11)
            {
                return false;
            }

            if (!root.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var message = messageElement.GetString();
            return string.Equals(
                message,
                "No completed queued response is available yet.",
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues))
        {
            var retryAfter = retryAfterValues.FirstOrDefault();
            if (int.TryParse(retryAfter, out var retryAfterSeconds) && retryAfterSeconds > 0)
            {
                return TimeSpan.FromSeconds(retryAfterSeconds);
            }
        }

        return TimeSpan.FromSeconds(5 * Math.Min(2 * attempt, 10));
    }

    internal static string Truncate(string? value, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }

    internal static string DescribeForLog(RadioCapture capture)
    {
        var builder = new StringBuilder();
        builder.Append("code=").Append(capture.Code);
        if (!string.IsNullOrWhiteSpace(capture.Message))
        {
            builder.Append(" message=").Append(Truncate(capture.Message, 160));
        }

        if (capture.HasTranscription)
        {
            builder.Append(" transcriptionChars=").Append(capture.Transcription!.Length);
        }

        if (capture.HasAttachment)
        {
            builder.Append(" meta=").Append(capture.Meta);
            builder.Append(" filesize=").Append(capture.Filesize);
            builder.Append(" attachmentChars=").Append(capture.Attachment!.Length);
        }

        return builder.ToString();
    }
}
