using System.Text;
using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Csv;
using FileProcessing.Core.Domain;
using FileProcessing.Core.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileProcessing.Core.Processing;

/// <summary>
/// Reads a transactions CSV, validates every row and produces the aggregates returned to the
/// caller. The stream is read once, forward-only, so memory use is independent of file size.
/// </summary>
public sealed class TransactionCsvProcessor(
    IOptions<FileProcessingOptions> options,
    IClock clock,
    ILogger<TransactionCsvProcessor> logger) : IFileProcessor
{
    private const string TransactionIdColumn = "TransactionId";
    private const string TransactionDateColumn = "TransactionDate";
    private const string DescriptionColumn = "Description";
    private const string AmountColumn = "Amount";
    private const string CurrencyColumn = "Currency";
    private const string CategoryColumn = "Category";

    private static readonly string[] RequiredColumns =
    [
        TransactionIdColumn,
        TransactionDateColumn,
        AmountColumn,
        CurrencyColumn,
        CategoryColumn,
    ];

    private readonly FileProcessingOptions _options = options.Value;

    public string FormatName => "transactions/csv";

    public async Task<FileProcessingResult> ProcessAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var validator = new TransactionRowValidator(clock);
        var errors = new ErrorSink(_options.MaxRetainedErrors);

        // detectEncodingFromByteOrderMarks strips a UTF-8/UTF-16 BOM instead of letting it become
        // part of the first header name. leaveOpen keeps the caller's hashing wrapper usable.
        using var reader = new StreamReader(
            content,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8 * 1024,
            leaveOpen: true);

        var csv = new CsvReader(reader);

        try
        {
            return await ProcessRecordsAsync(csv, validator, errors, cancellationToken).ConfigureAwait(false);
        }
        catch (CsvParseException ex)
        {
            logger.LogWarning("Rejected a file that is not well-formed CSV at line {Line}.", ex.LineNumber);
            errors.Add(ex.LineNumber, "file.malformed_csv", ex.Message);
            return FileProcessingResult.Failed("The file is not well-formed CSV.", errors.Errors);
        }
    }

    private async Task<FileProcessingResult> ProcessRecordsAsync(
        CsvReader csv,
        TransactionRowValidator validator,
        ErrorSink errors,
        CancellationToken cancellationToken)
    {
        Dictionary<string, int>? header = null;

        var totalRows = 0;
        var validRows = 0;
        var invalidRows = 0;
        var totalAmount = 0m;
        DateOnly? earliest = null;
        DateOnly? latest = null;

        var categoryTotals = new Dictionary<string, CategoryTotal>(StringComparer.OrdinalIgnoreCase);
        var currencyTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var row in csv.ReadRecordsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (header is null)
            {
                if (!TryBuildHeader(row, out header, out var headerFailure))
                {
                    return FileProcessingResult.Failed(headerFailure);
                }

                continue;
            }

            totalRows++;
            if (totalRows > _options.MaxRows)
            {
                logger.LogWarning("Rejected a file with more than {MaxRows} data rows.", _options.MaxRows);
                return FileProcessingResult.Failed(
                    $"The file contains more than the maximum of {_options.MaxRows} data rows.");
            }

            if (row.Fields.Count != header.Count)
            {
                invalidRows++;
                errors.Add(
                    row.LineNumber,
                    "row.column_count_mismatch",
                    $"Expected {header.Count} columns but found {row.Fields.Count}.");
                continue;
            }

            var raw = new RawTransactionRow(
                Field(row, header, TransactionIdColumn),
                Field(row, header, TransactionDateColumn),
                Field(row, header, DescriptionColumn),
                Field(row, header, AmountColumn),
                Field(row, header, CurrencyColumn),
                Field(row, header, CategoryColumn));

            if (!validator.TryValidate(row.LineNumber, in raw, errors, out var record) || record is null)
            {
                invalidRows++;
                continue;
            }

            if (!seenIds.Add(record.TransactionId))
            {
                invalidRows++;
                errors.Add(
                    row.LineNumber,
                    "transactionId.duplicate",
                    "Transaction id appears more than once in this file.",
                    TransactionIdColumn);
                continue;
            }

            validRows++;
            totalAmount += record.Amount;
            earliest = earliest is null || record.TransactionDate < earliest ? record.TransactionDate : earliest;
            latest = latest is null || record.TransactionDate > latest ? record.TransactionDate : latest;

            var existing = categoryTotals.GetValueOrDefault(record.Category, new CategoryTotal(0, 0m));
            categoryTotals[record.Category] = existing with
            {
                Count = existing.Count + 1,
                Amount = existing.Amount + record.Amount,
            };

            currencyTotals[record.Currency] = currencyTotals.GetValueOrDefault(record.Currency) + record.Amount;
        }

        if (header is null)
        {
            return FileProcessingResult.Failed("The file is empty.");
        }

        if (totalRows == 0)
        {
            return FileProcessingResult.Failed("The file contains a header row but no data rows.");
        }

        return new FileProcessingResult
        {
            Status = invalidRows == 0 ? ProcessingStatus.Succeeded : ProcessingStatus.CompletedWithErrors,
            TotalRows = totalRows,
            ValidRows = validRows,
            InvalidRows = invalidRows,
            TotalAmount = totalAmount,
            Errors = errors.Errors,
            ErrorsTruncated = errors.Truncated,
            CategoryTotals = categoryTotals,
            CurrencyTotals = currencyTotals,
            EarliestTransactionDate = earliest,
            LatestTransactionDate = latest,
        };
    }

    private static bool TryBuildHeader(CsvRow row, out Dictionary<string, int>? header, out string failureReason)
    {
        header = null;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < row.Fields.Count; i++)
        {
            var name = row.Fields[i].Trim();
            if (name.Length == 0)
            {
                failureReason = $"Header column {i + 1} has no name.";
                return false;
            }

            if (!map.TryAdd(name, i))
            {
                failureReason = $"Header column '{name}' appears more than once.";
                return false;
            }
        }

        var missing = RequiredColumns.Where(c => !map.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
        {
            failureReason = $"The header is missing required columns: {string.Join(", ", missing)}.";
            return false;
        }

        header = map;
        failureReason = string.Empty;
        return true;
    }

    private static string Field(CsvRow row, Dictionary<string, int> header, string column) =>
        header.TryGetValue(column, out var index) && index < row.Fields.Count
            ? row.Fields[index]
            : string.Empty;
}
