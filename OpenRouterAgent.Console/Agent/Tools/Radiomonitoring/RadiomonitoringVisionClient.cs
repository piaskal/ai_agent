using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public sealed class RadiomonitoringVisionClient
{
    private const int MaxRetries = 3;
    private const int MaxImageEdge = 1280;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly OpenRouterOptions _options;
    private readonly ILogger<RadiomonitoringVisionClient> _logger;

    public RadiomonitoringVisionClient(
        IOptions<OpenRouterOptions> options,
        ILogger<RadiomonitoringVisionClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> DescribeAsync(
        byte[] imageBytes,
        string mimeType,
        CancellationToken cancellationToken = default)
    {
        var (resizedBytes, resizedMime) = Shrink(imageBytes, mimeType);
        var imageBase64 = Convert.ToBase64String(resizedBytes);
        var model = string.IsNullOrWhiteSpace(_options.ToolModel) ? _options.Model : _options.ToolModel;
        var prompt =
            "Extract every readable fact from this intercepted radio attachment. " +
            "Prefer names of cities, the codename Syjon, surface area, warehouse counts or lists, and phone numbers. " +
            "Return plain text only.";

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_options.GetEffectiveBaseUrl()),
            Timeout = TimeSpan.FromSeconds(120)
        };

        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.GetEffectiveApiKey());

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
                        new { type = "input_image", image_url = $"data:{resizedMime};base64,{imageBase64}" }
                    }
                }
            }
        };

        _logger.LogInformation(
            "Describing radiomonitoring image ({Bytes} bytes, {Mime}) with OpenRouter model '{Model}'.",
            resizedBytes.Length,
            resizedMime,
            model);

        using var response = await SendWithRetryOn429Async(httpClient, request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
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

    private static (byte[] Bytes, string Mime) Shrink(byte[] imageBytes, string mimeType)
    {
        try
        {
            using var image = Image.Load(imageBytes);
            if (image.Width > MaxImageEdge || image.Height > MaxImageEdge)
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxImageEdge, MaxImageEdge)
                }));
            }

            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 75 });
            return (output.ToArray(), "image/jpeg");
        }
        catch (Exception)
        {
            return (imageBytes, string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryOn429Async(
        HttpClient httpClient,
        object request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var response = await httpClient.PostAsJsonAsync(
                _options.GetResponsesPath(),
                request,
                SerializerOptions,
                cancellationToken);

            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == MaxRetries)
            {
                return response;
            }

            var retryDelay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Radiomonitoring vision received 429. Waiting {DelaySeconds}s before retry {RetryAttempt}/{MaxRetries}.",
                retryDelay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            response.Dispose();
            await Task.Delay(retryDelay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in radiomonitoring vision client.");
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
