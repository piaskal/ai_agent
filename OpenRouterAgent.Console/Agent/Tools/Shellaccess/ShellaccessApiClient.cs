using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Shellaccess;

public sealed class ShellaccessApiClient
{
    public const string TaskName = "shellaccess";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<ShellaccessApiClient> _logger;

    public ShellaccessApiClient(HttpClient httpClient, IOptions<AgentToolOptions> options, ILogger<ShellaccessApiClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public async Task<ShellaccessReply> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the shellaccess API.");
        }

        var payload = new
        {
            apikey = _apiKey,
            task = TaskName,
            answer = new { cmd = command }
        };

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var response = await _httpClient.PostAsJsonAsync(VerifyUrl, payload, JsonOptions, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var reply = ShellaccessReply.Parse(responseBody);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogInformation(
                    "Shellaccess hub {Status} code {Code}: {Message}",
                    (int)response.StatusCode,
                    reply.Code,
                    reply.Message);
                return reply;
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Shellaccess API failed with status {(int)response.StatusCode} ({response.StatusCode}). {reply.Describe()}");
            }

            var delay = TimeSpan.FromSeconds(5 * attempt);
            _logger.LogWarning(
                "Shellaccess API returned {StatusCode}. Retrying in {DelaySeconds}s.",
                (int)response.StatusCode,
                delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in shellaccess API client.");
    }
}

public sealed record ShellaccessReply(int? Code, string? Message, string? Output)
{
    public string Describe() => $"code={Code?.ToString() ?? "none"}; message={Message}; output={Output}";

    public static ShellaccessReply Parse(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            int? code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : null;
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            var output = root.TryGetProperty("output", out var outputElement) ? outputElement.GetString() : null;
            return new ShellaccessReply(code, message, output);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Shellaccess API returned non-JSON: {responseBody}", exception);
        }
    }
}
