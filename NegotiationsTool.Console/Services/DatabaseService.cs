namespace NegotiationsTool.Console.Services;

using Microsoft.Data.Sqlite;
using CsvHelper;
using System.Globalization;
using Serilog;
using NegotiationsTool.Console.Interfaces;

public class DatabaseService : IDatabaseService
{
    private static readonly string[] RequiredTables = ["cities", "connections", "items"];
    private readonly string _dbPath;
    private readonly ICsvDownloadService _csvDownloadService;
    private readonly ILogger _logger;

    public DatabaseService(ICsvDownloadService csvDownloadService)
    {
        _csvDownloadService = csvDownloadService;
        _dbPath = "negotiations.db";
        _logger = Log.ForContext<DatabaseService>();
    }

    public async Task InitializeAsync(string dbPath, string baseUrl, string[] csvFiles)
    {
        _logger.Information("Initializing database: {DbPath}", dbPath);

        if (await DatabaseHasRequiredTablesAsync(dbPath, csvFiles))
        {
            _logger.Information("Existing database detected with required tables. Skipping CSV download and import.");
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();

        // Drop existing tables if they exist
        foreach (var file in csvFiles)
        {
            var tableName = Path.GetFileNameWithoutExtension(file);
            command.CommandText = $"DROP TABLE IF EXISTS [{tableName}];";
            await command.ExecuteNonQueryAsync();
            _logger.Information("Dropped existing table: {TableName}", tableName);
        }

        // Download and load CSV files
        foreach (var csvFile in csvFiles)
        {
            var csvUrl = $"{baseUrl.TrimEnd('/')}/{csvFile}";
            var csvContent = await _csvDownloadService.DownloadCsvAsync(csvUrl);
            var tableName = Path.GetFileNameWithoutExtension(csvFile);

            await LoadCsvIntoDatabase(connection, csvContent, tableName);
        }

        _logger.Information("Database initialized successfully");
    }

    public async Task<List<Dictionary<string, object>>> ExecuteQueryAsync(string query)
    {
        var results = new List<Dictionary<string, object>>();

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandTimeout = 30;

            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.GetValue(i);
                }
                results.Add(row);
            }

            _logger.Information("Query executed successfully, returned {RowCount} rows", results.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error executing query");
            throw;
        }

        return results;
    }

    private async Task LoadCsvIntoDatabase(SqliteConnection connection, string csvContent, string tableName)
    {
        using var reader = new StringReader(csvContent);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        await csv.ReadAsync();
        csv.ReadHeader();

        if (csv.HeaderRecord == null || csv.HeaderRecord.Length == 0)
        {
            _logger.Warning("No headers found in {TableName}", tableName);
            return;
        }

        // Create table
        var columns = csv.HeaderRecord;
        var createTableSql = $"CREATE TABLE [{tableName}] ({string.Join(", ", columns.Select(c => $"[{c}] TEXT"))});";

        var command = connection.CreateCommand();
        command.CommandText = createTableSql;
        await command.ExecuteNonQueryAsync();

        // Insert data
        var insertSql = $"INSERT INTO [{tableName}] VALUES ({string.Join(", ", Enumerable.Range(0, columns.Length).Select(i => $"@p{i}"))});";
        var rowCount = 0;

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = insertSql;

        while (await csv.ReadAsync())
        {
            command.Parameters.Clear();

            for (int i = 0; i < columns.Length; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", csv[i] ?? (object)DBNull.Value);
            }

            await command.ExecuteNonQueryAsync();
            rowCount++;

            if (rowCount % 1000 == 0)
            {
                _logger.Information("Loaded {RowCount} rows into table [{TableName}]", rowCount, tableName);
            }
        }

        await transaction.CommitAsync();

        _logger.Information("Table [{TableName}] loaded with {RowCount} rows", tableName, rowCount);
    }

    private async Task<bool> DatabaseHasRequiredTablesAsync(string dbPath, string[] csvFiles)
    {
        if (!File.Exists(dbPath))
        {
            return false;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var existingTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tableName = reader[0]?.ToString();
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                existingTables.Add(tableName);
            }
        }

        return csvFiles
            .Select(Path.GetFileNameWithoutExtension)
            .All(tableName => tableName is not null && existingTables.Contains(tableName));
    }

    public async Task<Dictionary<string, List<string>>> GetSchemaInfoAsync()
    {
        var schemaInfo = new Dictionary<string, List<string>>();

        try
        {
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();

                // Get all table names
                var tableCommand = connection.CreateCommand();
                tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";

                var tableNames = new List<string>();
                using (var reader = await tableCommand.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var tableName = reader[0]?.ToString();
                        if (!string.IsNullOrEmpty(tableName))
                        {
                            tableNames.Add(tableName);
                        }
                    }
                }

                // Get columns for each table
                foreach (var tableName in tableNames)
                {
                    var columnCommand = connection.CreateCommand();
                    columnCommand.CommandText = $"PRAGMA table_info([{tableName}])";

                    var columns = new List<string>();
                    using (var reader = await columnCommand.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var columnName = reader[1]?.ToString() ?? string.Empty;
                            var columnType = reader[2]?.ToString() ?? "TEXT";
                            if (!string.IsNullOrEmpty(columnName))
                            {
                                columns.Add($"{columnName} ({columnType})");
                            }
                        }
                    }

                    schemaInfo[tableName] = columns;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error retrieving schema information");
        }

        return schemaInfo;
    }

    public async Task<List<(string ItemCode, string ItemName)>> FindItemsByKeywordsAsync(IReadOnlyList<string> keywords)
    {
        var matches = new List<(string ItemCode, string ItemName)>();

        if (keywords.Count == 0)
        {
            return matches;
        }

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        var conditions = new List<string>();

        for (var i = 0; i < keywords.Count; i++)
        {
            var parameterName = $"@kw{i}";
            conditions.Add($"lower(i.name) LIKE '%' || lower({parameterName}) || '%' ");
            command.Parameters.AddWithValue(parameterName, keywords[i]);
        }

        command.CommandText = $@"
SELECT DISTINCT i.code, i.name
FROM [items] i
WHERE {string.Join(" OR ", conditions)}
ORDER BY i.name;";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var itemCode = reader[0]?.ToString();
            var itemName = reader[1]?.ToString();

            if (!string.IsNullOrWhiteSpace(itemCode) && !string.IsNullOrWhiteSpace(itemName))
            {
                matches.Add((itemCode, itemName));
            }
        }

        _logger.Information("Keyword search for [{Keywords}] matched {Count} items", string.Join(", ", keywords), matches.Count);
        return matches;
    }

    public async Task<(string ItemCode, string ItemName)?> FindItemByExactNameAsync(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
SELECT i.code, i.name
FROM [items] i
WHERE lower(i.name) = lower(@itemName)
ORDER BY i.code
LIMIT 1;";
        command.Parameters.AddWithValue("@itemName", itemName.Trim());

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var itemCode = reader[0]?.ToString();
        var foundItemName = reader[1]?.ToString();

        if (string.IsNullOrWhiteSpace(itemCode) || string.IsNullOrWhiteSpace(foundItemName))
        {
            return null;
        }

        _logger.Information("Exact item match found for query '{ItemName}'", itemName);
        return (itemCode, foundItemName);
    }

    public async Task<List<string>> GetCitiesForItemAsync(string itemCode)
    {
        var cities = new List<string>();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
SELECT DISTINCT c.name
FROM [connections] cn
INNER JOIN [cities] c ON c.code = cn.cityCode
WHERE cn.itemCode = @itemCode
ORDER BY c.name;";
        command.Parameters.AddWithValue("@itemCode", itemCode);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cityName = reader[0]?.ToString();
            if (!string.IsNullOrWhiteSpace(cityName))
            {
                cities.Add(cityName);
            }
        }

        _logger.Information("Item {ItemCode} is available in {Count} cities", itemCode, cities.Count);
        return cities;
    }
}
