using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Foodwarehouse;

internal static class FoodwarehouseApiClient
{
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const string TaskName = "foodwarehouse";
    private const int MaxRetries = 3;

    public static async Task<string> SendAsync(
        string apiKey,
        object answer,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the foodwarehouse API.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            apikey = apiKey,
            task = TaskName,
            answer
        });

        using var httpClient = new HttpClient();

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var requestContent = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(VerifyUrl, requestContent, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return responseBody;
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Foodwarehouse API request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            logger.LogWarning(
                "Foodwarehouse API returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in foodwarehouse tools.");
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
}
