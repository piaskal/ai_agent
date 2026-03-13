using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OpenRouterAgent.ConsoleApp.OpenRouter;

public sealed class OpenRouterClient : IOpenRouterClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly OpenRouterOptions _options;

    public OpenRouterClient(HttpClient httpClient, IOptions<OpenRouterOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        if (!string.IsNullOrWhiteSpace(_options.AppUrl))
        {
            _httpClient.DefaultRequestHeaders.Remove("HTTP-Referer");
            _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", _options.AppUrl);
        }

        if (!string.IsNullOrWhiteSpace(_options.AppName))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-Title");
            _httpClient.DefaultRequestHeaders.Add("X-Title", _options.AppName);
        }
    }

    public async Task<OpenRouterCompletionResult> GetCompletionAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var useTools = tools.Count > 0;
        var request = new OpenRouterChatRequest(
            _options.Model,
            messages,
            _options.Temperature,
            _options.MaxTokens,
            useTools ? tools : null,
            useTools ? "auto" : null);

        using var response = await _httpClient.PostAsJsonAsync(
            "api/v1/chat/completions",
            request,
            SerializerOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"OpenRouter request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). {errorBody}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OpenRouterChatResponse>(SerializerOptions, cancellationToken);
        var choice = payload?.Choices?.FirstOrDefault();
        var content = choice?.Message?.Content?.Trim();
        var toolCalls = choice?.Message?.ToolCalls ?? [];

        if (string.IsNullOrWhiteSpace(content) && toolCalls.Count == 0)
        {
            throw new InvalidOperationException("OpenRouter returned an empty response.");
        }

        return new OpenRouterCompletionResult(content, toolCalls);
    }
}