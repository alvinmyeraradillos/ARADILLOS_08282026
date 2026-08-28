namespace FileProcessing.Api.Contracts;

/// <summary>Row tallies for one file.</summary>
public sealed record RowCounts(int Total, int Valid, int Invalid);

/// <summary>Per-category rollup, including the average the brief asks for.</summary>
public sealed record CategoryAggregate(string Category, int Count, decimal TotalAmount, decimal AverageAmount);

/// <summary>
/// The aggregates computed over the rows that passed validation. Invalid rows are excluded, so an
/// average is never skewed by a row the service could not parse.
/// </summary>
public sealed record AmountAggregates(
    decimal TotalAmount,
    decimal AverageAmount,
    IReadOnlyDictionary<string, decimal> TotalsByCurrency,
    IReadOnlyList<CategoryAggregate> ByCategory,
    DateOnly? EarliestTransactionDate,
    DateOnly? LatestTransactionDate);

/// <summary>One row-level problem, addressed by line number so a client can fix the source file.</summary>
public sealed record ProcessingErrorResponse(long Line, string? Field, string Code, string Message);

/// <summary>Response for a completed upload.</summary>
public sealed record FileProcessingResponse(
    Guid FileId,
    string FileName,
    string Status,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long DurationMilliseconds,
    long SizeBytes,
    string Sha256,
    RowCounts Rows,
    AmountAggregates Aggregates,
    IReadOnlyList<ProcessingErrorResponse> Errors,
    bool ErrorsTruncated);

/// <summary>List-view projection of a tracked file.</summary>
public sealed record ProcessedFileSummaryResponse(
    Guid FileId,
    string FileName,
    string ClientId,
    string Status,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long DurationMilliseconds,
    long SizeBytes,
    string Sha256,
    RowCounts Rows,
    decimal TotalAmount,
    string? FailureReason);

/// <summary>Detail view: the list projection plus the retained row errors.</summary>
public sealed record ProcessedFileDetailResponse(
    ProcessedFileSummaryResponse File,
    IReadOnlyList<ProcessingErrorResponse> Errors,
    bool ErrorsTruncated);

/// <summary>Envelope for paged collections.</summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    bool HasNextPage);

/// <summary>Per-client rollup inside the summary report.</summary>
public sealed record ClientActivityResponse(string ClientId, int FileCount, int TotalRows, decimal TotalAmount);

/// <summary>Aggregate reporting across every tracked file in the requested window.</summary>
public sealed record SummaryReportResponse(
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int TotalFiles,
    int SucceededFiles,
    int FilesWithErrors,
    int FailedFiles,
    long TotalBytes,
    RowCounts Rows,
    decimal TotalAmount,
    decimal AverageRowAmount,
    double AverageDurationMilliseconds,
    DateTimeOffset? FirstReceivedAtUtc,
    DateTimeOffset? LastReceivedAtUtc,
    IReadOnlyList<ClientActivityResponse> ByClient);
