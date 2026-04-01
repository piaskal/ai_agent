namespace NegotiationsTool.Console.Interfaces;

public interface ICsvDownloadService
{
    /// <summary>
    /// Downloads a CSV file from the given URL
    /// </summary>
    Task<string> DownloadCsvAsync(string csvUrl);
}
