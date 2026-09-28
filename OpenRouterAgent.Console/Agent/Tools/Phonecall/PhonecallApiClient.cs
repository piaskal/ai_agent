using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public sealed class PhonecallApiClient
{
    public const string TaskName = "phonecall";
    private const string VerifyUrl = "https://hub.ag3nts.org/verify";
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<PhonecallApiClient> _logger;

    public PhonecallApiClient(HttpClient httpClient, IOptions<AgentToolOptions> options, ILogger<PhonecallApiClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public Task<PhonecallReply> StartAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new { action = "start" }, cancellationToken);

    public Task<PhonecallReply> SendAudioAsync(string audioBase64, CancellationToken cancellationToken = default) =>
        SendAsync(new { audio = audioBase64 }, cancellationToken);

    private async Task<PhonecallReply> SendAsync(object answer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the phonecall API.");
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
            var reply = PhonecallReply.Parse(responseBody);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest)
            {
                _logger.LogInformation("Phonecall hub {Status}: {Reply}", (int)response.StatusCode, reply.Describe());
                if (!response.IsSuccessStatusCode && reply.Code is null)
                {
                    throw new InvalidOperationException(
                        $"Phonecall API failed with status {(int)response.StatusCode}. {reply.Describe()}");
                }

                return reply;
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Phonecall API failed with status {(int)response.StatusCode} ({response.StatusCode}). {reply.Describe()}");
            }

            var delay = TimeSpan.FromSeconds(5 * attempt);
            _logger.LogWarning(
                "Phonecall API returned {StatusCode}. Retrying in {DelaySeconds}s.",
                (int)response.StatusCode,
                delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in phonecall API client.");
    }
}
