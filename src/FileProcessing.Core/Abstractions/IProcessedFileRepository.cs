using FileProcessing.Core.Domain;
using FileProcessing.Core.Reporting;

namespace FileProcessing.Core.Abstractions;

/// <summary>
/// Persistence for the processing audit trail. Filtering by client is part of the contract rather
/// than something callers bolt on, so a query cannot accidentally leak another tenant's files.
/// </summary>
public interface IProcessedFileRepository
{
    Task AddAsync(ProcessedFile file, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads one record with its errors. When <c>restrictToClientId</c> is supplied the record is
    /// only returned if it belongs to that client, so an unauthorised id looks the same as a
    /// missing one.
    /// </summary>
    Task<ProcessedFile?> GetAsync(
        Guid id,
        string? restrictToClientId,
        CancellationToken cancellationToken = default);

    Task<PagedResult<ProcessedFile>> QueryAsync(
        ProcessedFileQuery query,
        CancellationToken cancellationToken = default);

    Task<ProcessingSummaryReport> SummariseAsync(
        ReportQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>True when the same client has already had a file with this digest processed.</summary>
    Task<Guid?> FindDuplicateAsync(
        string clientId,
        string sha256,
        CancellationToken cancellationToken = default);
}
