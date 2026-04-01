namespace NegotiationsTool.Console.Interfaces;

public interface IDatabaseService
{
    /// <summary>
    /// Initializes the SQLite database and loads CSV data
    /// </summary>
    Task InitializeAsync(string dbPath, string baseUrl, string[] csvFiles);

    /// <summary>
    /// Executes a SQL query against the database
    /// </summary>
    Task<List<Dictionary<string, object>>> ExecuteQueryAsync(string query);

    /// <summary>
    /// Gets schema information for all tables
    /// </summary>
    Task<Dictionary<string, List<string>>> GetSchemaInfoAsync();

    /// <summary>
    /// Finds an item by exact name match (case-insensitive).
    /// </summary>
    Task<(string ItemCode, string ItemName)?> FindItemByExactNameAsync(string itemName);

    /// <summary>
    /// Finds items that match at least one of the provided keywords.
    /// </summary>
    Task<List<(string ItemCode, string ItemName)>> FindItemsByKeywordsAsync(IReadOnlyList<string> keywords);

    /// <summary>
    /// Returns all city names where the given item is available.
    /// </summary>
    Task<List<string>> GetCitiesForItemAsync(string itemCode);
}
