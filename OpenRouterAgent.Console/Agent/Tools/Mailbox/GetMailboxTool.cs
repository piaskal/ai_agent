using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenRouterAgent.ConsoleApp.OpenRouter;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Mailbox;

public sealed class GetMailboxTool : IAgentTool
{
    public const string ToolName = "get_mailbox";
    private const string MailboxUrl = "https://hub.ag3nts.org/api/zmail";

    private readonly string _apiKey;

    public GetMailboxTool(IOptions<AgentToolOptions> options)
    {
        _apiKey = options.Value.ApiKey;
    }

    public string Name => ToolName;

    public ChatToolDefinition Definition => new(
        Type: "function",
        Function: new ChatToolDefinitionFunction(
            Name: ToolName,
            Description: "Retrieves mailbox data from the zmail endpoint. Supported actions: help, getInbox, getThread, getMessages, search, reset.",
            ParametersSchema: new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", description = "Mailbox action to execute. One of: help, getInbox, getThread, getMessages, search, reset. Default: help." },
                    page = new { type = "integer", description = "Optional page number for help, getInbox, and search. Default: 1.", minimum = 1 },
                    perPage = new { type = "integer", description = "Optional page size for getInbox and search. Must be between 5 and 20." },
                    threadID = new { type = "integer", description = "Required for getThread. Numeric thread identifier." },
                    query = new { type = "string", description = "Required for search. Supports full-text and Gmail-like operators." },
                    ids = new
                    {
                        description = "Required for getMessages. Can be a numeric rowID, a 32-character messageID string, or an array of them.",
                        oneOf = new object[]
                        {
                            new { type = "integer" },
                            new { type = "string" },
                            new
                            {
                                type = "array",
                                items = new
                                {
                                    oneOf = new object[]
                                    {
                                        new { type = "integer" },
                                        new { type = "string" }
                                    }
                                }
                            }
                        }
                    }
                },
                required = Array.Empty<string>()
            }));

    public async Task<ToolExecutionResult> ExecuteAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Agent tools API key is missing. Set 'AgentTools:ApiKey' in configuration, user secrets, or environment variables.");

        var request = ParseArguments(toolCall.Function.Arguments);
        var payload = BuildPayload(request);

        using var httpClient = new HttpClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.PostAsync(MailboxUrl, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Mailbox request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {responseBody}");

        return new ToolExecutionResult(responseBody);
    }

    private Dictionary<string, object?> BuildPayload(MailboxRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["apikey"] = _apiKey,
            ["action"] = request.Action
        };

        switch (request.Action)
        {
            case "help":
                payload["page"] = request.Page ?? 1;
                break;

            case "reset":
                break;

            case "getInbox":
                payload["page"] = request.Page ?? 1;
                if (request.PerPage.HasValue)
                    payload["perPage"] = request.PerPage.Value;
                break;

            case "getThread":
                payload["threadID"] = request.ThreadId!.Value;
                break;

            case "getMessages":
                payload["ids"] = request.Ids;
                break;

            case "search":
                payload["query"] = request.Query!;
                payload["page"] = request.Page ?? 1;
                if (request.PerPage.HasValue)
                    payload["perPage"] = request.PerPage.Value;
                break;
        }

        return payload;
    }

    private static MailboxRequest ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new MailboxRequest(Action: "help", Page: 1);

        using var json = JsonDocument.Parse(argumentsJson);
        var root = json.RootElement;

        var action = ReadOptionalString(root, "action")?.Trim();
        if (string.IsNullOrWhiteSpace(action))
            action = "help";

        var page = ReadOptionalInt(root, "page");
        var perPage = ReadOptionalInt(root, "perPage");
        var threadId = ReadOptionalInt(root, "threadID");
        var query = ReadOptionalString(root, "query")?.Trim();
        var ids = ReadOptionalIds(root, "ids");

        switch (action)
        {
            case "help":
            case "reset":
                return new MailboxRequest(Action: action, Page: page ?? 1);

            case "getInbox":
                ValidatePage(page ?? 1);
                ValidatePerPage(perPage);
                return new MailboxRequest(Action: action, Page: page ?? 1, PerPage: perPage);

            case "getThread":
                if (!threadId.HasValue || threadId.Value <= 0)
                    throw new InvalidOperationException("Action 'getThread' requires integer argument 'threadID' greater than or equal to 1.");

                return new MailboxRequest(Action: action, ThreadId: threadId.Value);

            case "getMessages":
                if (ids is null)
                    throw new InvalidOperationException("Action 'getMessages' requires argument 'ids'.");

                return new MailboxRequest(Action: action, Ids: ids);

            case "search":
                if (string.IsNullOrWhiteSpace(query))
                    throw new InvalidOperationException("Action 'search' requires non-empty string argument 'query'.");

                ValidatePage(page ?? 1);
                ValidatePerPage(perPage);
                return new MailboxRequest(Action: action, Query: query, Page: page ?? 1, PerPage: perPage);

            default:
                throw new InvalidOperationException("Unsupported mailbox action. Supported actions: help, getInbox, getThread, getMessages, search, reset.");
        }
    }

    private static void ValidatePage(int page)
    {
        if (page <= 0)
            throw new InvalidOperationException("Argument 'page' must be greater than or equal to 1.");
    }

    private static void ValidatePerPage(int? perPage)
    {
        if (perPage.HasValue && (perPage.Value < 5 || perPage.Value > 20))
            throw new InvalidOperationException("Argument 'perPage' must be between 5 and 20.");
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"'{propertyName}' must be a string.");

        return element.GetString();
    }

    private static int? ReadOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw new InvalidOperationException($"'{propertyName}' must be an integer.");

        return value;
    }

    private static object? ReadOptionalIds(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var numericId) => numericId,
            JsonValueKind.String => ReadRequiredIdString(element),
            JsonValueKind.Array => ReadIdsArray(element),
            _ => throw new InvalidOperationException($"'{propertyName}' must be an integer, string, or array of integers/strings.")
        };
    }

    private static string ReadRequiredIdString(JsonElement element)
    {
        var value = element.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Argument 'ids' cannot be empty.");

        return value;
    }

    private static object[] ReadIdsArray(JsonElement element)
    {
        var values = new List<object>();

        foreach (var item in element.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Number when item.TryGetInt32(out var numericId):
                    values.Add(numericId);
                    break;
                case JsonValueKind.String:
                    values.Add(ReadRequiredIdString(item));
                    break;
                default:
                    throw new InvalidOperationException("Argument 'ids' array can contain only integers or strings.");
            }
        }

        if (values.Count == 0)
            throw new InvalidOperationException("Argument 'ids' array cannot be empty.");

        return values.ToArray();
    }

    private sealed record MailboxRequest(
        string Action,
        int? Page = null,
        int? PerPage = null,
        int? ThreadId = null,
        string? Query = null,
        object? Ids = null);
}