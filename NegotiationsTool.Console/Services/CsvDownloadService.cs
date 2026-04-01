namespace NegotiationsTool.Console.Services;

using Serilog;
using NegotiationsTool.Console.Interfaces;

public class CsvDownloadService : ICsvDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public CsvDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _logger = Log.ForContext<CsvDownloadService>();
    }

    public async Task<string> DownloadCsvAsync(string csvUrl)
    {
        try
        {
            _logger.Information("Downloading CSV file: {CsvUrl}", csvUrl);
            var csvContent = await _httpClient.GetStringAsync(csvUrl);
            _logger.Information("CSV file downloaded successfully");
            return csvContent;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error downloading CSV file from {CsvUrl}", csvUrl);
            throw;
        }
    }
}
