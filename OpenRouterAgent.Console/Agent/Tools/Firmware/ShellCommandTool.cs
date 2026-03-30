using System.Net;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Firmware;

public sealed class ShellCommandTool : IAgentTool
{
    public const string ToolName = "shell_command";
    private const string ApiUrl = "https://hub.ag3nts.org/api/shell";
    private const int MaxRetries = 3;
    private static readonly string[] AllowedCommands =
    [
        "help",
        "ls",
        "cat",
        "cd",
        "pwd",
        "rm",
        "editline",
        "reboot",
        "date",
        "uptime",
        "find",
        "history",
        "whoami"
    ];

    private static readonly string[] ForbiddenPathPrefixes = ["/proc", "/etc", "/root"];

    private readonly string _apiKey;
    private readonly ILogger<ShellCommandTool> _logger;

    public ShellCommandTool(IOptions<AgentToolOptions> options, ILogger<ShellCommandTool> logger)
    {
        _apiKey = options.Value.ApiKey;
        _logger = logger;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Execute shell commands on the remote server. Available commands: help, ls [path], cat <path>, cd [path], pwd, rm <file>, editline <file> <line-number> <content>, reboot, date, uptime, find <pattern>, history, whoami, run arbitrary binary files.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    cmd = new
                    {
                        type = "string",
                        description = "The shell command to execute (e.g., 'help', 'ls', 'pwd', 'cat filename.txt', 'binary file to execute')"
                    }
                },
                required = new[] { "cmd" }
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var command = ParseArguments(toolCall.Function.Arguments);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("AgentTools:ApiKey is required to call the shell API.");
        }

        using var httpClient = new HttpClient();
        await ValidateCommandRulesAsync(httpClient, command, cancellationToken);

        var payload = new
        {
            apikey = _apiKey,
            cmd = command
        };

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var requestContent = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await httpClient.PostAsync(ApiUrl, requestContent, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (TryGetTooManyRequestsDelay(response, responseBody, attempt, out var tooManyRequestsDelay, out var tooManyRequestsReason))
            {
                if (attempt == MaxRetries)
                {
                    throw new InvalidOperationException(
                        $"Shell API rate limit persisted after {MaxRetries} attempts. Last reason: {tooManyRequestsReason}. Response: {responseBody}");
                }

                _logger.LogWarning(
                    "Shell API rate limit detected for command '{Command}'. Waiting {DelaySeconds}s before retry (attempt {NextAttempt}/{MaxRetries}). Reason: {Reason}",
                    command,
                    tooManyRequestsDelay.TotalSeconds,
                    attempt + 1,
                    MaxRetries,
                    tooManyRequestsReason);

                await Task.Delay(tooManyRequestsDelay, cancellationToken);
                continue;
            }

            if (TryGetTemporaryBanDelay(responseBody, out var banDelay, out var banReason))
            {
                _logger.LogWarning(
                    "Shell API temporary ban detected for command '{Command}'. Waiting {DelaySeconds}s before returning response. Reason: {Reason}",
                    command,
                    banDelay.TotalSeconds,
                    banReason);

                await Task.Delay(banDelay, cancellationToken);
                return new ToolExecutionResult(responseBody);
            }

            if (response.IsSuccessStatusCode)
            {
                    if(responseBody.Length > 4096)
                    {
                        _logger.LogWarning("Received long response from shell API for command '{Command}': {ResponseSnippet}...", command, responseBody.Substring(0, 4096));
                        return new ToolExecutionResult("RESPONSE_TOO_LONG: " + responseBody.Substring(0, 4096));
                    }
                    else
                    {
                        _logger.LogInformation("Received response from shell API for command '{Command}': {Response}", command, responseBody);
                    }


                return new ToolExecutionResult(responseBody);
            }

            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (!retryable || attempt == MaxRetries)
            {
                throw new InvalidOperationException(
                    $"Shell API request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "Shell API returned {StatusCode}. Retrying in {DelaySeconds}s (attempt {NextAttempt}/{MaxRetries}).",
                (int)response.StatusCode,
                delay.TotalSeconds,
                attempt + 1,
                MaxRetries);

            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected retry flow in shell_command tool.");
    }

    private async Task ValidateCommandRulesAsync(HttpClient httpClient, string command, CancellationToken cancellationToken)
    {
        var tokens = SplitCommand(command);
        if (tokens.Count == 0)
        {
            throw new InvalidOperationException("Tool 'shell_command' argument 'cmd' cannot be empty.");
        }

        var actionToken = tokens[0];
        var action = tokens[0].ToLowerInvariant();
        var isPathBasedExecution = IsPathLikeToken(actionToken);

        if (!AllowedCommands.Contains(action, StringComparer.Ordinal) && !isPathBasedExecution)
        {
            throw new InvalidOperationException($"Command '{action}' is not allowed.");
        }

        if (isPathBasedExecution)
        {
            EnsurePathIsAllowed(actionToken);
        }

        foreach (var candidate in GetPathCandidates(action, tokens))
        {
            EnsurePathIsAllowed(candidate);
        }

        if (action is "rm" or "editline")
        {
            if (tokens.Count < 2)
            {
                throw new InvalidOperationException($"Command '{action}' requires a file path argument.");
            }

            var cwd = await GetCurrentRemoteDirectoryAsync(httpClient, cancellationToken);
            var targetPath = ResolvePath(cwd, tokens[1]);
            EnsurePathIsAllowed(targetPath);

            var gitignore = await TryGetGitignoreAsync(httpClient, cancellationToken);
            if (gitignore is not null)
            {
                var relative = GetRelativeToBase(cwd, targetPath);
                if (IsIgnoredByGitignore(gitignore, relative))
                {
                    throw new InvalidOperationException(
                        $"Refusing to execute '{action}' on '{tokens[1]}' because it is ignored by .gitignore in '{cwd}'.");
                }
            }
        }
    }

    private static List<string> SplitCommand(string command)
    {
        return command.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool IsPathLikeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.StartsWith("/", StringComparison.Ordinal) ||
               token.StartsWith("./", StringComparison.Ordinal) ||
               token.StartsWith("../", StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetPathCandidates(string action, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2)
        {
            yield break;
        }

        if (action is "ls" or "cd" or "cat" or "rm" or "find")
        {
            yield return tokens[1];
            yield break;
        }

        if (action == "editline")
        {
            yield return tokens[1];
        }
    }

    private static void EnsurePathIsAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var normalized = path.Replace('\\', '/').Trim();
        var normalizedWithLeadingSlash = normalized.StartsWith('/') ? normalized : $"/{normalized}";

        foreach (var prefix in ForbiddenPathPrefixes)
        {
            if (normalizedWithLeadingSlash.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalizedWithLeadingSlash.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Access to '{path}' is not allowed.");
            }
        }
    }

    private async Task<string> GetCurrentRemoteDirectoryAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        var response = await SendShellAsync(httpClient, "pwd", cancellationToken);
        var value = ParseTextResponse(response);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Unable to determine remote working directory before command execution.");
        }

        return value;
    }

    private async Task<string?> TryGetGitignoreAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendShellAsync(httpClient, "cat .gitignore", cancellationToken);
            var value = ParseTextResponse(response);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("no such", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return value;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> SendShellAsync(HttpClient httpClient, string command, CancellationToken cancellationToken)
    {
        var payload = new
        {
            apikey = _apiKey,
            cmd = command
        };

        string responseBody;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var requestContent = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await httpClient.PostAsync(ApiUrl, requestContent, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (TryGetTooManyRequestsDelay(response, responseBody, attempt, out var delay, out var reason))
            {
                if (attempt == MaxRetries)
                {
                    throw new InvalidOperationException(
                        $"Shell API preflight command '{command}' hit rate limit after {MaxRetries} attempts. Last reason: {reason}. Response: {responseBody}");
                }

                _logger.LogWarning(
                    "Shell API rate limit detected during preflight command '{Command}'. Waiting {DelaySeconds}s before retry (attempt {NextAttempt}/{MaxRetries}). Reason: {Reason}",
                    command,
                    delay.TotalSeconds,
                    attempt + 1,
                    MaxRetries,
                    reason);

                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Shell API preflight command '{command}' failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");
            }

            if(responseBody.Length > 4096)
            {
                _logger.LogWarning("Received long response from shell API for command '{Command}': {ResponseSnippet}...", command, responseBody.Substring(0, 4096));
                return "RESPONSE_TOO_LONG: " + responseBody.Substring(0, 4096);
            }
            else
            {
                _logger.LogInformation("Received response from shell API for command '{Command}': {Response}", command, responseBody);
            }

            return responseBody;
        }

        throw new InvalidOperationException($"Unexpected retry flow in shell preflight command '{command}'.");
    }

    private static string ParseTextResponse(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.String)
            {
                return root.GetString() ?? string.Empty;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("reply", out var replyElement) && replyElement.ValueKind == JsonValueKind.String)
                {
                    return replyElement.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
                {
                    return messageElement.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String)
                {
                    return outputElement.GetString() ?? string.Empty;
                }
            }
        }
        catch
        {
            // Some shell responses may be plain text and not JSON.
        }

        return rawResponse;
    }

    private static string ResolvePath(string baseDirectory, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return baseDirectory;
        }

        var normalizedCandidate = candidate.Replace('\\', '/').Trim();
        if (normalizedCandidate.StartsWith('/'))
        {
            return NormalizePath(normalizedCandidate);
        }

        var combined = $"{baseDirectory.TrimEnd('/')}/{normalizedCandidate}";
        return NormalizePath(combined);
    }

    private static string NormalizePath(string path)
    {
        var segments = new Stack<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.Pop();
                }

                continue;
            }

            segments.Push(segment);
        }

        return "/" + string.Join('/', segments.Reverse());
    }

    private static string GetRelativeToBase(string baseDirectory, string targetPath)
    {
        var baseNormalized = NormalizePath(baseDirectory).TrimEnd('/');
        var targetNormalized = NormalizePath(targetPath);

        if (targetNormalized.Equals(baseNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        if (targetNormalized.StartsWith(baseNormalized + "/", StringComparison.OrdinalIgnoreCase))
        {
            return targetNormalized[(baseNormalized.Length + 1)..];
        }

        return targetNormalized.TrimStart('/');
    }

    private static bool IsIgnoredByGitignore(string gitignoreContent, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
        var lines = gitignoreContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var ignored = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var isNegation = line.StartsWith('!');
            var pattern = isNegation ? line[1..] : line;
            if (pattern.Length == 0)
            {
                continue;
            }

            if (GitignorePatternMatches(pattern, normalizedPath))
            {
                ignored = !isNegation;
            }
        }

        return ignored;
    }

    private static bool GitignorePatternMatches(string pattern, string relativePath)
    {
        var normalizedPattern = pattern.Replace('\\', '/').Trim();
        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');

        if (normalizedPattern.EndsWith('/'))
        {
            var dir = normalizedPattern.TrimEnd('/');
            return normalizedPath.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);
        }

        var anchored = normalizedPattern.StartsWith('/');
        if (anchored)
        {
            normalizedPattern = normalizedPattern.TrimStart('/');
        }

        var regexPattern = "^" + Regex.Escape(normalizedPattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]") + "$";

        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
        if (anchored)
        {
            return regex.IsMatch(normalizedPath);
        }

        return regex.IsMatch(normalizedPath) || regex.IsMatch(Path.GetFileName(normalizedPath));
    }

    private static string ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new InvalidOperationException("Tool 'shell_command' requires argument 'cmd'.");
        }

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        if (!root.TryGetProperty("cmd", out var cmdElement) || cmdElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Tool 'shell_command' requires string argument 'cmd'.");
        }

        var command = cmdElement.GetString();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Tool 'shell_command' argument 'cmd' cannot be empty.");
        }

        return command;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta.HasValue == true)
        {
            return response.Headers.RetryAfter.Delta.Value;
        }

        if (response.Headers.RetryAfter?.Date.HasValue == true)
        {
            var retryAt = response.Headers.RetryAfter.Date.Value;
            var delay = retryAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        if (TryGetHeaderDelaySeconds(response, "X-RateLimit-Reset-After", out var resetAfterDelay))
        {
            return resetAfterDelay;
        }

        if (TryGetHeaderDelaySeconds(response, "Retry-After", out var retryAfterDelay))
        {
            return retryAfterDelay;
        }

        if (TryGetUnixTimestampDelaySeconds(response, "X-RateLimit-Reset", out var resetDelay))
        {
            return resetDelay;
        }

        return TimeSpan.FromSeconds(60);
    }

    private static bool TryGetHeaderDelaySeconds(HttpResponseMessage response, string headerName, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;

        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        var rawValue = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        if (!double.TryParse(rawValue, out var seconds))
        {
            return false;
        }

        delay = TimeSpan.FromSeconds(Math.Max(1, seconds));
        return true;
    }

    private static bool TryGetUnixTimestampDelaySeconds(HttpResponseMessage response, string headerName, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;

        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return false;
        }

        var rawValue = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        if (!long.TryParse(rawValue, out var unixTimestamp))
        {
            return false;
        }

        var retryAt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        var computedDelay = retryAt - DateTimeOffset.UtcNow;
        if (computedDelay <= TimeSpan.Zero)
        {
            return false;
        }

        delay = computedDelay;
        return true;
    }

    private static bool TryGetTemporaryBanDelay(string responseBody, out TimeSpan delay, out string reason)
    {
        delay = TimeSpan.Zero;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasBanCode = root.TryGetProperty("code", out var codeElement)
                             && codeElement.ValueKind == JsonValueKind.Number
                             && codeElement.TryGetInt32(out var code)
                             && code == -735;

            var hasBanMessage = root.TryGetProperty("message", out var messageElement)
                                && messageElement.ValueKind == JsonValueKind.String
                                && (messageElement.GetString() ?? string.Empty)
                                    .Contains("temporarily banned", StringComparison.OrdinalIgnoreCase);

            if (!hasBanCode && !hasBanMessage)
            {
                return false;
            }

            if (!root.TryGetProperty("ban", out var banElement) || banElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!banElement.TryGetProperty("seconds_left", out var secondsElement)
                || secondsElement.ValueKind != JsonValueKind.Number
                || !secondsElement.TryGetInt32(out var secondsLeft))
            {
                return false;
            }

            if (banElement.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String)
            {
                reason = reasonElement.GetString() ?? string.Empty;
            }

            delay = TimeSpan.FromSeconds(Math.Max(1, secondsLeft));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetTooManyRequestsDelay(HttpResponseMessage response, string responseBody, int attempt, out TimeSpan delay, out string reason)
    {
        delay = TimeSpan.Zero;
        reason = string.Empty;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            delay = GetRetryDelay(response, attempt);
            reason = ExtractRateLimitReason(responseBody) ?? "HTTP 429 TooManyRequests";
            return true;
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasRateLimitCode = root.TryGetProperty("code", out var codeElement)
                                   && codeElement.ValueKind == JsonValueKind.Number
                                   && codeElement.TryGetInt32(out var code)
                                   && code == 429;

            var hasRateLimitMessage = root.TryGetProperty("message", out var messageElement)
                                      && messageElement.ValueKind == JsonValueKind.String
                                      && (messageElement.GetString() ?? string.Empty)
                                          .Contains("too many requests", StringComparison.OrdinalIgnoreCase);

            if (!hasRateLimitCode && !hasRateLimitMessage)
            {
                return false;
            }

            if (TryGetRetrySecondsFromJson(root, out var seconds))
            {
                delay = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                delay = GetRetryDelay(response, attempt);
            }

            reason = ExtractRateLimitReason(responseBody) ?? "Too many requests";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetRetrySecondsFromJson(JsonElement root, out int seconds)
    {
        seconds = 0;

        if (root.TryGetProperty("seconds_left", out var secondsElement)
            && secondsElement.ValueKind == JsonValueKind.Number
            && secondsElement.TryGetInt32(out seconds))
        {
            seconds = Math.Max(1, seconds);
            return true;
        }

        if (root.TryGetProperty("retry_after", out var retryAfterElement)
            && retryAfterElement.ValueKind == JsonValueKind.Number
            && retryAfterElement.TryGetInt32(out seconds))
        {
            seconds = Math.Max(1, seconds);
            return true;
        }

        if (root.TryGetProperty("ban", out var banElement)
            && banElement.ValueKind == JsonValueKind.Object
            && banElement.TryGetProperty("seconds_left", out var banSecondsElement)
            && banSecondsElement.ValueKind == JsonValueKind.Number
            && banSecondsElement.TryGetInt32(out seconds))
        {
            seconds = Math.Max(1, seconds);
            return true;
        }

        return false;
    }

    private static string? ExtractRateLimitReason(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString();
            }

            if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
