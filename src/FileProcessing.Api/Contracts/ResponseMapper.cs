using FileProcessing.Core.Domain;
using FileProcessing.Core.Processing;
using FileProcessing.Core.Reporting;

namespace FileProcessing.Api.Contracts;

/// <summary>
/// Projects domain types onto the wire contract. Kept in one place so the public shape of the API
/// cannot drift accidentally when a domain type is refactored.
/// </summary>
public static class ResponseMapper
{
    public static FileProcessingResponse ToProcessingResponse(ProcessedFile file, FileProcessingResult result) =>
        new(
            file.Id,
            file.FileName,
            file.Status.ToString(),
            file.ReceivedAtUtc,
            file.CompletedAtUtc,
            file.DurationMilliseconds,
            file.SizeInBytes,
            file.Sha256,
            new RowCounts(result.TotalRows, result.ValidRows, result.InvalidRows),
            ToAggregates(result),
            result.Errors.Select(ToErrorResponse).ToArray(),
            result.ErrorsTruncated);

    public static ProcessedFileSummaryResponse ToSummary(ProcessedFile file) =>
        new(
            file.Id,
            file.FileName,
            file.ClientId,
            file.Status.ToString(),
            file.ReceivedAtUtc,
            file.CompletedAtUtc,
            file.DurationMilliseconds,
            file.SizeInBytes,
            file.Sha256,
            new RowCounts(file.TotalRows, file.ValidRows, file.InvalidRows),
            file.TotalAmount,
            file.FailureReason);

    public static ProcessedFileDetailResponse ToDetail(ProcessedFile file) =>
        new(
            ToSummary(file),
            file.Errors.OrderBy(e => e.LineNumber).Select(ToErrorResponse).ToArray(),
            file.ErrorsTruncated);

    public static PagedResponse<ProcessedFileSummaryResponse> ToPagedResponse(PagedResult<ProcessedFile> page) =>
        new(
            page.Items.Select(ToSummary).ToArray(),
            page.Page,
            page.PageSize,
            page.TotalCount,
            page.TotalPages,
            page.HasNextPage);

    public static SummaryReportResponse ToReportResponse(ProcessingSummaryReport report) =>
        new(
            report.FromUtc,
            report.ToUtc,
            report.TotalFiles,
            report.SucceededFiles,
            report.FilesWithErrors,
            report.FailedFiles,
            report.TotalBytes,
            new RowCounts(report.TotalRows, report.ValidRows, report.InvalidRows),
            report.TotalAmount,
            Average(report.TotalAmount, report.ValidRows),
            Math.Round(report.AverageDurationMilliseconds, 2),
            report.FirstReceivedAtUtc,
            report.LastReceivedAtUtc,
            report.ByClient
                .Select(c => new ClientActivityResponse(c.ClientId, c.FileCount, c.TotalRows, c.TotalAmount))
                .ToArray());

    private static AmountAggregates ToAggregates(FileProcessingResult result) =>
        new(
            result.TotalAmount,
            Average(result.TotalAmount, result.ValidRows),
            result.CurrencyTotals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal),
            result.CategoryTotals
                .Select(kvp => new CategoryAggregate(
                    kvp.Key,
                    kvp.Value.Count,
                    kvp.Value.Amount,
                    Average(kvp.Value.Amount, kvp.Value.Count)))
                .OrderByDescending(c => c.TotalAmount)
                .ToArray(),
            result.EarliestTransactionDate,
            result.LatestTransactionDate);

    private static ProcessingErrorResponse ToErrorResponse(ProcessingError error) =>
        new(error.LineNumber, error.Field, error.Code, error.Message);

    /// <summary>
    /// Mean amount, rounded to cents. Away-from-zero matches how money is normally rounded in
    /// reporting, and the guard keeps an empty file from dividing by zero.
    /// </summary>
    private static decimal Average(decimal total, int count) =>
        count <= 0 ? 0m : Math.Round(total / count, 2, MidpointRounding.AwayFromZero);
}
