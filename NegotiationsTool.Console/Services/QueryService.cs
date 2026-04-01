namespace NegotiationsTool.Console.Services;

using Serilog;
using NegotiationsTool.Console.Interfaces;
using NegotiationsTool.Console.Models;
using System.Text;

public class QueryService : IQueryService
{
    private readonly IDatabaseService _databaseService;
    private readonly ApplicationInitializationState _initializationState;
    private readonly ILogger _logger;

    public QueryService(
        IDatabaseService databaseService,
        ApplicationInitializationState initializationState)
    {
        _databaseService = databaseService;
        _initializationState = initializationState;
        _logger = Log.ForContext<QueryService>();
    }

    public async Task<QueryResponse> ProcessQueryAsync(QueryRequest request)
    {
        if (request?.Params == null)
        {
            return new QueryResponse { Output = "Error: Missing 'params' field in request" };
        }

        if (!_initializationState.IsReady)
        {
            var startupMessage = _initializationState.IsInitializing
                ? "Service is still starting. CSV import is running in the background. Check GET /health and retry the query when isReady=true."
                : $"Service initialization failed: {_initializationState.ErrorMessage}";

            return new QueryResponse { Output = startupMessage };
        }

        var query = request.Params.Trim();

        if (query.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryResponse { Output = await GetHelpResponseAsync() };
        }

        try
        {
            var exactMatch = await _databaseService.FindItemByExactNameAsync(query);
            if (exactMatch.HasValue)
            {
                var exactCities = await _databaseService.GetCitiesForItemAsync(exactMatch.Value.ItemCode);
                return new QueryResponse { Output = BuildCitiesOutput(exactMatch.Value.ItemName, exactCities) };
            }

            var keywords = ExtractValidKeywords(query);
            if (keywords.Count == 0)
            {
                return new QueryResponse
                {
                    Output = "No valid keywords found. Provide words separated by spaces; words shorter than 4 characters are ignored."
                };
            }

            var itemMatches = await _databaseService.FindItemsByKeywordsAsync(keywords);

            if (itemMatches.Count == 0)
            {
                return new QueryResponse
                {
                    Output = $"No items matched keywords: {string.Join(", ", keywords)}"
                };
            }

            if (itemMatches.Count > 1)
            {
                return new QueryResponse { Output = BuildClarificationOutput(keywords, itemMatches) };
            }

            var selectedItem = itemMatches[0];
            var cities = await _databaseService.GetCitiesForItemAsync(selectedItem.ItemCode);
            return new QueryResponse { Output = BuildCitiesOutput(selectedItem.ItemName, cities) };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing query");
            return new QueryResponse { Output = $"Error executing query: {ex.Message}" };
        }
    }

    private async Task<string> GetHelpResponseAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Query Help - Search items by keywords and return cities for one resolved item");
        sb.AppendLine();
        sb.AppendLine("= SCHEMA =");
        
        try
        {
            var schemaInfo = await _databaseService.GetSchemaInfoAsync();
            
            if (schemaInfo.Count == 0)
            {
                sb.AppendLine("No tables found");
            }
            else
            {
                foreach (var table in schemaInfo)
                {
                    sb.AppendLine($"\nTable: [{table.Key}]");
                    sb.AppendLine("Columns:");
                    foreach (var column in table.Value)
                    {
                        sb.AppendLine($"-{column}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error retrieving schema: {ex.Message}");
        }
        
        sb.AppendLine();
        sb.AppendLine("= HOW TO USE =");
        sb.AppendLine("Send keywords separated by spaces in params, for example:");
        sb.AppendLine("{ \"params\": \"rezystor ceramiczny\" }");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Words shorter than 4 characters are ignored");
        sb.AppendLine("- If more than one item matches, the response asks for clarification");
        sb.AppendLine("- If exactly one item matches, the service returns all cities where it is available");
        sb.AppendLine();
        sb.AppendLine("Matching uses [items].name and links to cities through [connections].");
        
        return sb.ToString();
    }

    private static List<string> ExtractValidKeywords(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        return input
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildClarificationOutput(IReadOnlyList<string> keywords, List<(string ItemCode, string ItemName)> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Clarification required. Keywords ({string.Join(", ", keywords)}) matched multiple items:");

        foreach (var item in items)
        {
            sb.AppendLine($"- {item.ItemName}");
        }

        sb.AppendLine();
        sb.AppendLine("Please narrow your query with more specific keywords.");

        return sb.ToString().TrimEnd();
    }

    private static string BuildCitiesOutput(string itemName, List<string> cities)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Item: {itemName}");

        if (cities.Count == 0)
        {
            sb.AppendLine("No cities found for this item.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("Cities:");
        foreach (var city in cities)
        {
            sb.AppendLine($"- {city}");
        }

        return sb.ToString().TrimEnd();
    }
}
