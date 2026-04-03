using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.OkoEditor;

public sealed class OkoBrowseTool : IAgentTool
{
    public const string ToolName = "oko_browse";
    private const string BaseUrl = "https://oko.ag3nts.org";
    private const string CredentialsFileName = "okocredentials.local.txt";

    private readonly string _apiKey;
    private readonly string _loginUsername;
    private readonly string _loginPassword;
    private readonly ILogger<OkoBrowseTool> _logger;
    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookieContainer;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _isLoggedIn;

    public OkoBrowseTool(IOptions<AgentToolOptions> options, ILogger<OkoBrowseTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        (_loginUsername, _loginPassword) = LoadCredentials();
        _logger = logger;
        _cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookieContainer,
            AllowAutoRedirect = true
        };
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Browse a page or sub-page at https://oko.ag3nts.org/. Handles login automatically. Returns the page HTML content.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    path = new
                    {
                        type = "string",
                        description = "The path or full URL to fetch. Examples: '/', '/report', 'https://oko.ag3nts.org/some/page'."
                    }
                },
                required = new[] { "path" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var path = ParseArguments(toolCall.Function.Arguments);
        var url = BuildUrl(path);

        await EnsureLoggedInAsync(cancellationToken);

        var content = await FetchPageAsync(url, cancellationToken);

        // If the response is still a login page, the session may have expired — re-login once.
        if (IsLoginPage(content))
        {
            _logger.LogWarning("OkoBrowse: session appears expired, re-logging in.");
            _isLoggedIn = false;
            await EnsureLoggedInAsync(cancellationToken);
            content = await FetchPageAsync(url, cancellationToken);
        }

        return new ToolExecutionResult(ObfuscateApiKey(content));
    }

    private async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("OkoBrowse GET {Url} -> {StatusCode}", url, (int)response.StatusCode);
        return content;
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_isLoggedIn) return;

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_isLoggedIn) return;
            await LoginAsync(cancellationToken);
            _isLoggedIn = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        // Fetch root to pick up any CSRF token and discover the form action URL.
        var loginPageResponse = await _httpClient.GetAsync(BaseUrl, cancellationToken);
        var loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync(cancellationToken);

        var formAction = ExtractFormAction(loginPageHtml);
        var loginUrl = formAction is not null
            ? BuildUrl(formAction)
            : $"{BaseUrl}/";

        var csrfToken = ExtractCsrfToken(loginPageHtml);

        var fields = new Dictionary<string, string>
        {
            ["action"] = "login",
            ["login"] = _loginUsername,
            ["password"] = _loginPassword,
            ["access_key"] = _apiKey
        };

        if (csrfToken is not null)
        {
            fields["_token"] = csrfToken;
            fields["csrf_token"] = csrfToken;
        }

        using var formContent = new FormUrlEncodedContent(fields);
        var response = await _httpClient.PostAsync(loginUrl, formContent, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogDebug("OkoBrowse login POST {Url} -> {StatusCode}", loginUrl, (int)response.StatusCode);

        if ((int)response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                $"Login to oko.ag3nts.org failed with HTTP {(int)response.StatusCode}. Response: {ObfuscateApiKey(body)}");
        }
    }

    private static string? ExtractFormAction(string html)
    {
        var match = Regex.Match(html, @"<form[^>]+action=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value
            : null;
    }

    private static string? ExtractCsrfToken(string html)
    {
        // Common patterns: <input name="_token" value="..."> or <meta name="csrf-token" content="...">
        var inputMatch = Regex.Match(
            html,
            @"<input[^>]+name=[""'](?:_token|csrf[_-]token)[""'][^>]+value=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (inputMatch.Success) return inputMatch.Groups[1].Value;

        var metaMatch = Regex.Match(
            html,
            @"<meta[^>]+name=[""']csrf-token[""'][^>]+content=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        return metaMatch.Success ? metaMatch.Groups[1].Value : null;
    }

    private static bool IsLoginPage(string html) =>
        html.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("type='password'", StringComparison.OrdinalIgnoreCase);

    private static string BuildUrl(string path)
    {
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return $"{BaseUrl}/{path.TrimStart('/')}";
    }

    private string ObfuscateApiKey(string content)
    {
        if (string.IsNullOrEmpty(_apiKey)) return content;
        return content.Replace(_apiKey, "[REDACTED]", StringComparison.Ordinal);
    }

    private static (string Username, string Password) LoadCredentials()
    {
        var path = Path.Combine(AppContext.BaseDirectory, CredentialsFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Oko credentials file not found: {path}. Create '{CredentialsFileName}' with login on line 1 and password on line 2.");
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            throw new InvalidOperationException(
                $"Oko credentials file '{CredentialsFileName}' must contain login on line 1 and password on line 2.");
        }

        return (lines[0].Trim(), lines[1].Trim());
    }

    private static string ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'oko_browse' requires argument 'path'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'oko_browse' requires string argument 'path'.");
        }

        var path = pathElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Argument 'path' cannot be empty.");
        }

        return path;
    }
}
