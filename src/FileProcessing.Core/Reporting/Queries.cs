using FileProcessing.Core.Domain;

namespace FileProcessing.Core.Reporting;

/// <summary>One page of results plus the totals a caller needs to render pagination.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;
}

/// <summary>Filter and paging arguments for the processed-file listing.</summary>
public sealed record ProcessedFileQuery
{
    /// <summary>
    /// When set, only files uploaded by this client are returned. The API populates it from the
    /// authenticated principal unless the caller holds the cross-client read scope, so tenant
    /// isolation is enforced server side rather than trusted from the request.
    /// </summary>
    public string? RestrictToClientId { get; init; }

    public ProcessingStatus? Status { get; init; }

    public DateTimeOffset? ReceivedFromUtc { get; init; }

    public DateTimeOffset? ReceivedToUtc { get; init; }

    /// <summary>Case-insensitive substring match on the stored file name.</summary>
    public string? FileNameContains { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

/// <summary>Date-range arguments for the summary report.</summary>
public sealed record ReportQuery
{
    public string? RestrictToClientId { get; init; }

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }
}

/// <summary>Per-client rollup inside <see cref="ProcessingSummaryReport"/>.</summary>
public sealed record ClientActivity(string ClientId, int FileCount, int TotalRows, decimal TotalAmount);

/// <summary>Aggregate view across every file that matches a <see cref="ReportQuery"/>.</summary>
public sealed record ProcessingSummaryReport
{
    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public int TotalFiles { get; init; }

    public int SucceededFiles { get; init; }

    public int FilesWithErrors { get; init; }

    public int FailedFiles { get; init; }

    public long TotalBytes { get; init; }

    public int TotalRows { get; init; }

    public int ValidRows { get; init; }

    public int InvalidRows { get; init; }

    public decimal TotalAmount { get; init; }

    public double AverageDurationMilliseconds { get; init; }

    public DateTimeOffset? FirstReceivedAtUtc { get; init; }

    public DateTimeOffset? LastReceivedAtUtc { get; init; }

    public IReadOnlyList<ClientActivity> ByClient { get; init; } = [];
}
