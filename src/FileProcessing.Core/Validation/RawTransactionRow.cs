namespace FileProcessing.Core.Validation;

/// <summary>Untrusted field values for one row, taken straight from the file.</summary>
public readonly record struct RawTransactionRow(
    string TransactionId,
    string TransactionDate,
    string Description,
    string Amount,
    string Currency,
    string Category);
