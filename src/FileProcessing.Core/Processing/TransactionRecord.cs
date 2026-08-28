namespace FileProcessing.Core.Processing;

/// <summary>A single validated row from a transactions file.</summary>
public sealed record TransactionRecord(
    string TransactionId,
    DateOnly TransactionDate,
    string Description,
    decimal Amount,
    string Currency,
    string Category);
