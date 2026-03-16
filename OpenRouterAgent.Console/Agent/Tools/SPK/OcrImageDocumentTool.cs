using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.SPK;

public sealed class OcrImageDocumentTool : IAgentTool
{
    public const string ToolName = "ocr_image_document";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly OpenRouterOptions _options;
    private readonly ILogger<OcrImageDocumentTool> _logger;

    public OcrImageDocumentTool(IOptions<OpenRouterOptions> options, ILogger<OcrImageDocumentTool> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Runs OCR on an image document and returns extracted text. Uses a dedicated OCR model, optionally overridden per call.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    image_url = new { type = "string", description = "Publicly accessible image URL to OCR." },
                    prompt = new { type = "string", description = "Optional OCR instructions, e.g. language or formatting expectations." },
                    model = new { type = "string", description = "Optional OpenRouter model override for this OCR request." }
                },
                required = new[] { "image_url" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var (imageUrl, prompt, model) = ParseArguments(toolCall.Function.Arguments);

        if (!Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute))
        {
            throw new InvalidOperationException("Tool 'ocr_image_document' requires a valid absolute 'image_url'.");
        }

        var selectedModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : !string.IsNullOrWhiteSpace(_options.OcrModel)
                ? _options.OcrModel
                : _options.Model;

        var extractedText = await ExecuteOcrAsync(imageUrl, prompt, selectedModel, cancellationToken);
        return new ToolExecutionResult(extractedText);
    }

    private (string ImageUrl, string Prompt, string? Model) ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'ocr_image_document' requires arguments with string field 'image_url'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("image_url", out var imageUrlElement) || imageUrlElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'ocr_image_document' requires string argument 'image_url'.");
        }

        var imageUrl = imageUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            throw new InvalidOperationException("Tool 'ocr_image_document' argument 'image_url' cannot be empty.");
        }

        var prompt = root.TryGetProperty("prompt", out var promptElement) && promptElement.ValueKind == JsonValueKind.String
            ? promptElement.GetString()
            : null;

        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString()
            : null;

        return (
            imageUrl,
            string.IsNullOrWhiteSpace(prompt)
                ? "Extract all visible text from this document image. Preserve line breaks and return only the extracted text."
                : prompt!,
            model);
    }

    private async Task<string> ExecuteOcrAsync(string imageUrl, string prompt, string model, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl)
        };

        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        if (!string.IsNullOrWhiteSpace(_options.AppUrl))
        {
            httpClient.DefaultRequestHeaders.Remove("HTTP-Referer");
            httpClient.DefaultRequestHeaders.Add("HTTP-Referer", _options.AppUrl);
        }

        if (!string.IsNullOrWhiteSpace(_options.AppName))
        {
            httpClient.DefaultRequestHeaders.Remove("X-Title");
            httpClient.DefaultRequestHeaders.Add("X-Title", _options.AppName);
        }

        var request = new
        {
            model,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = prompt },
                        new { type = "input_image", image_url = imageUrl }
                    }
                }
            }
        };

        _logger.LogInformation("Executing OCR for image '{ImageUrl}' with model '{Model}'.", imageUrl, model);

        using var response = await httpClient.PostAsJsonAsync("api/v1/responses", request, SerializerOptions, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OCR request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}");
        }

        using var json = JsonDocument.Parse(responseBody);
        var extractedText = ExtractOutputText(json.RootElement);

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            throw new InvalidOperationException("OCR response did not contain extracted text.");
        }

        return extractedText;
    }

    private static string? ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputElement) || outputElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var outputItem in outputElement.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!outputItem.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentPart in contentElement.EnumerateArray())
            {
                if (contentPart.ValueKind == JsonValueKind.String)
                {
                    builder.Append(contentPart.GetString());
                    continue;
                }

                if (contentPart.ValueKind == JsonValueKind.Object &&
                    contentPart.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textElement.GetString());
                }
            }
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
}
