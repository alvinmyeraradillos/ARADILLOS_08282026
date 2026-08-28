using FileProcessing.Core.Domain;

namespace FileProcessing.Core.Processing;

/// <summary>Per-category rollup produced while processing a file.</summary>
public sealed record CategoryTotal(int Count, decimal Amount);

/// <summary>Outcome of processing one file. Purely a value object; nothing here touches storage.</summary>
public sealed class FileProcessingResult
{
    public required ProcessingStatus Status { get; init; }

    public int TotalRows { get; init; }

    public int ValidRows { get; init; }

    public int InvalidRows { get; init; }

    public decimal TotalAmount { get; init; }

    public IReadOnlyList<ProcessingError> Errors { get; init; } = [];

    /// <summary>True when more errors were found than <see cref="FileProcessingOptions.MaxRetainedErrors"/>.</summary>
    public bool ErrorsTruncated { get; init; }

    public IReadOnlyDictionary<string, CategoryTotal> CategoryTotals { get; init; } =
        new Dictionary<string, CategoryTotal>();

    public IReadOnlyDictionary<string, decimal> CurrencyTotals { get; init; } =
        new Dictionary<string, decimal>();

    public DateOnly? EarliestTransactionDate { get; init; }

    public DateOnly? LatestTransactionDate { get; init; }

    /// <summary>Populated only when <see cref="Status"/> is <see cref="ProcessingStatus.Failed"/>.</summary>
    public string? FailureReason { get; init; }

    public static FileProcessingResult Failed(string reason, IReadOnlyList<ProcessingError>? errors = null) =>
        new()
        {
            Status = ProcessingStatus.Failed,
            FailureReason = reason,
            Errors = errors ?? [],
        };
}
