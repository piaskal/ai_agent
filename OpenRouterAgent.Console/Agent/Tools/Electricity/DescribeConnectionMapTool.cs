using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Electricity;

public sealed class DescribeConnectionMapTool : IAgentTool
{
    public const string ToolName = "describe_connection_map";
    private const int MaxRetries = 3;
    private static readonly Rectangle CurrentMapCropArea = new(235, 95, 530 - 235, 390 - 95);
    private static readonly Rectangle DesiredMapCropArea = new(137, 88, 431 - 137, 380 - 88);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly OpenRouterOptions _options;
    private readonly AgentToolOptions _agentOptions;
    private readonly ILogger<DescribeConnectionMapTool> _logger;

    public DescribeConnectionMapTool(IOptions<OpenRouterOptions> options, IOptions<AgentToolOptions> agentOptions, ILogger<DescribeConnectionMapTool> logger)
    {
        _options = options.Value;
        _agentOptions = agentOptions.Value;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Describes a selected connection map image using a multimodal LLM. The tool accepts a reference to either the 'current' or 'desired' connection map and returns a textual description based on the provided prompt. This is useful for understanding the current grid configuration or verifying the desired state before making changes. The tool does not store conversation history. Each call is independent and should include all necessary context in the prompt.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    map_reference = new { type = "string", description = "Map state can be either 'current' or 'desired'." },
                    prompt = new { type = "string", description = "Optional custom description prompt." },
                },
                required = new[] { "map_reference" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var (mapReference, prompt) = ParseArguments(toolCall.Function.Arguments);
        var normalizedMapReference = mapReference.ToLowerInvariant();
        string imageUrl = mapReference.ToLowerInvariant() switch
        {
            "current" => $"https://hub.ag3nts.org/data/{_agentOptions.ApiKey}/electricity.png",
            //"reset" => $"https://hub.ag3nts.org/data/{_agentOptions.ApiKey}/electricity.png?reset=1",
            "desired" => "https://hub.ag3nts.org/i/solved_electricity.png",
            _ => throw new InvalidOperationException("Invalid 'map_reference' value. Expected 'current' or 'desired'.")
        };

        var cropArea = normalizedMapReference switch
        {
            "current" => CurrentMapCropArea,
            "desired" => DesiredMapCropArea,
            _ => throw new InvalidOperationException("Invalid 'map_reference' value. Expected 'current' or 'desired'.")
        };

        var imageBytes = await new HttpClient().GetByteArrayAsync(imageUrl, cancellationToken);
        imageBytes = CropImageToConnectionArea(imageBytes, cropArea);
        var imageBase64 = Convert.ToBase64String(imageBytes);
        
        var description = await DescribeImageAsync(imageBase64, "image/png", prompt, _options.ToolModel, cancellationToken);
        return new ToolExecutionResult(description);
    }

    private static (string MapReference, string Prompt) ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'describe_connection_map' requires arguments with string field 'map_reference'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("map_reference", out var mapReferenceElement) || mapReferenceElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'describe_connection_map' requires string argument 'map_reference'.");
        }

        var mapReference = mapReferenceElement.GetString();
        if (string.IsNullOrWhiteSpace(mapReference))
        {
            throw new InvalidOperationException("Tool 'describe_connection_map' argument 'map_reference' cannot be empty.");
        }

        var prompt = root.TryGetProperty("prompt", out var promptElement) && promptElement.ValueKind == JsonValueKind.String
            ? promptElement.GetString()
            : null;

        return (
            mapReference.Trim(),
            string.IsNullOrWhiteSpace(prompt)
                ? "Describe this connection map in detail. Mention key objects, connections, and anything notable."
                : prompt!.Trim());
    }

    private async Task<string> DescribeImageAsync(string imageBase64, string mimeType, string prompt, string model, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.GetEffectiveBaseUrl())
        };

        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.GetEffectiveApiKey());

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
                        new { type = "input_image", image_url = $"data:{mimeType};base64,{imageBase64}" }
                    }
                }
            }
        };

        _logger.LogInformation("Describing base64 image with model '{Model}' prompt '{Prompt}'.", model, prompt);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Describe connection map image payload (base64): {ImageBase64}", imageBase64);
        }

        string? responseBody = null;
        using var response = await SendWithRetryOn429Async(httpClient, request, cancellationToken);
        responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Describe connection map model response: {ResponseBody}", responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Image description request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}");
        }

        using var json = JsonDocument.Parse(responseBody);
        var description = ExtractOutputText(json.RootElement);

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("Image description response did not contain any text.");
        }

        return description;
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

    private static byte[] CropImageToConnectionArea(byte[] imageBytes, Rectangle cropArea)
    {
        using var sourceStream = new MemoryStream(imageBytes);
        using var sourceImage = Image.Load(sourceStream);

        var boundedCrop = Rectangle.Intersect(cropArea, new Rectangle(0, 0, sourceImage.Width, sourceImage.Height));
        if (boundedCrop.Width <= 0 || boundedCrop.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Configured crop area ({cropArea.X},{cropArea.Y})-({cropArea.Right},{cropArea.Bottom}) is outside source image bounds {sourceImage.Width}x{sourceImage.Height}.");
        }

        sourceImage.Mutate(context => context.Crop(boundedCrop));
        using var outputStream = new MemoryStream();
        sourceImage.Save(outputStream, PngFormat.Instance);
        return outputStream.ToArray();
    }

    private async Task<HttpResponseMessage> SendWithRetryOn429Async(HttpClient httpClient, object request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var response = await httpClient.PostAsJsonAsync(_options.GetResponsesPath(), request, SerializerOptions, cancellationToken);

            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == MaxRetries)
            {
                return response;
            }

            var retryDelay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Describe connection map received 429. Waiting {DelaySeconds}s before retry {RetryAttempt}/{MaxRetries}.",
                retryDelay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            response.Dispose();
            await Task.Delay(retryDelay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in describe_connection_map tool.");
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

        // Exponential backoff fallback when Retry-After is not present.
        return TimeSpan.FromSeconds(5 * Math.Min(2 * attempt, 10));
    }

}