using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public sealed class PhonecallTranscriber
{
    public const string Model = "google/gemini-2.5-flash";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public PhonecallTranscriber(HttpClient httpClient, IOptions<OpenRouterOptions> options)
    {
        _httpClient = httpClient;
        _apiKey = options.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(_apiKey) &&
            string.Equals(options.Value.GetNormalizedProvider(), OpenRouterOptions.ProviderOpenRouter, StringComparison.Ordinal))
        {
            _apiKey = options.Value.GetEffectiveApiKey();
        }
    }

    public async Task<string> TranscribeAsync(byte[] mp3, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("OpenRouter:ApiKey is required to transcribe phonecall audio.");
        }

        var payload = new
        {
            model = Model,
            temperature = 0,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Transcribe this Polish phone audio verbatim. Return only the transcript." },
                        new { type = "input_audio", input_audio = new { data = Convert.ToBase64String(mp3), format = "mp3" } }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenRouter transcription failed with status {(int)response.StatusCode}. {Truncate(body)}");
        }

        using var json = JsonDocument.Parse(body);
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenRouter transcription was empty.");
        }

        return content.Trim();
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
