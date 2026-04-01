namespace NegotiationsTool.Console.Interfaces;

using NegotiationsTool.Console.Models;

public interface IQueryService
{
    /// <summary>
    /// Processes a query request and returns results
    /// </summary>
    Task<QueryResponse> ProcessQueryAsync(QueryRequest request);
}
