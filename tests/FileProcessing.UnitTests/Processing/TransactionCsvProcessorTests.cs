using System.Text;
using FileProcessing.Core.Domain;
using FileProcessing.Core.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileProcessing.UnitTests.Processing;

public sealed class TransactionCsvProcessorTests
{
    private const string Header = "TransactionId,TransactionDate,Description,Amount,Currency,Category";

    [Fact]
    public async Task Computes_totals_and_averages_over_valid_rows()
    {
        var result = await ProcessAsync($"""
            {Header}
            TXN-1,2026-07-01,Linehaul,100.00,AUD,Linehaul
            TXN-2,2026-07-02,Fuel,50.00,AUD,Fuel
            TXN-3,2026-07-03,Fuel again,25.50,AUD,Fuel
            """);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.Equal(3, result.TotalRows);
        Assert.Equal(3, result.ValidRows);
        Assert.Equal(0, result.InvalidRows);
        Assert.Equal(175.50m, result.TotalAmount);
        Assert.Equal(new DateOnly(2026, 7, 1), result.EarliestTransactionDate);
        Assert.Equal(new DateOnly(2026, 7, 3), result.LatestTransactionDate);
        Assert.Equal(75.50m, result.CategoryTotals["Fuel"].Amount);
        Assert.Equal(2, result.CategoryTotals["Fuel"].Count);
        Assert.Equal(175.50m, result.CurrencyTotals["AUD"]);
    }

    [Fact]
    public async Task Keeps_going_past_a_bad_row_and_reports_it()
    {
        var result = await ProcessAsync($"""
            {Header}
            TXN-1,2026-07-01,Good,100.00,AUD,Linehaul
            TXN-2,not-a-date,Bad date,50.00,AUD,Fuel
            TXN-3,2026-07-03,Good,25.00,AUD,Fuel
            """);

        Assert.Equal(ProcessingStatus.CompletedWithErrors, result.Status);
        Assert.Equal(3, result.TotalRows);
        Assert.Equal(2, result.ValidRows);
        Assert.Equal(1, result.InvalidRows);

        // The bad row must not contribute to the aggregate.
        Assert.Equal(125.00m, result.TotalAmount);

        var error = Assert.Single(result.Errors);
        Assert.Equal("transactionDate.invalid_format", error.Code);
        Assert.Equal(3, error.LineNumber);
    }

    [Fact]
    public async Task Reports_every_broken_rule_on_a_row_at_once()
    {
        var result = await ProcessAsync($"""
            {Header}
            ,not-a-date,Bad,not-a-number,XYZ,
            """);

        Assert.Equal(1, result.InvalidRows);
        Assert.Equal(
            ["transactionId.missing", "transactionDate.invalid_format", "amount.not_a_number", "currency.not_allowed", "category.missing"],
            result.Errors.Select(e => e.Code));
    }

    [Fact]
    public async Task Rejects_a_repeated_transaction_id()
    {
        var result = await ProcessAsync($"""
            {Header}
            TXN-1,2026-07-01,First,100.00,AUD,Linehaul
            TXN-1,2026-07-02,Repeat,50.00,AUD,Fuel
            """);

        Assert.Equal(1, result.ValidRows);
        Assert.Equal(1, result.InvalidRows);
        Assert.Equal("transactionId.duplicate", Assert.Single(result.Errors).Code);
        Assert.Equal(100.00m, result.TotalAmount);
    }

    [Fact]
    public async Task Reports_a_row_with_the_wrong_column_count()
    {
        var result = await ProcessAsync($"""
            {Header}
            TXN-1,2026-07-01,Missing the rest
            """);

        Assert.Equal("row.column_count_mismatch", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Fails_when_the_header_is_missing_required_columns()
    {
        var result = await ProcessAsync("""
            Id,Date,Notes,Value
            TXN-1,2026-07-01,Nope,100.00
            """);

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Contains("TransactionId", result.FailureReason);
        Assert.Contains("Currency", result.FailureReason);
    }

    [Fact]
    public async Task Accepts_header_columns_in_any_order_and_any_case()
    {
        var result = await ProcessAsync("""
            category,CURRENCY,amount,description,transactiondate,transactionid
            Fuel,AUD,42.00,Reordered,2026-07-01,TXN-9
            """);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
        Assert.Equal(42.00m, result.TotalAmount);
    }

    [Fact]
    public async Task Treats_the_description_column_as_optional()
    {
        var result = await ProcessAsync("""
            TransactionId,TransactionDate,Amount,Currency,Category
            TXN-1,2026-07-01,10.00,AUD,Fuel
            """);

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Fails_on_an_empty_file()
    {
        var result = await ProcessAsync(string.Empty);

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Equal("The file is empty.", result.FailureReason);
    }

    [Fact]
    public async Task Fails_on_a_header_with_no_data_rows()
    {
        var result = await ProcessAsync(Header);

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Contains("no data rows", result.FailureReason);
    }

    [Fact]
    public async Task Fails_rather_than_partially_processing_a_file_over_the_row_limit()
    {
        var rows = string.Join(
            '\n',
            Enumerable.Range(1, 5).Select(i => $"TXN-{i},2026-07-01,Row,1.00,AUD,Fuel"));

        var result = await ProcessAsync($"{Header}\n{rows}", new FileProcessingOptions { MaxRows = 3 });

        // Half a file is worse than none: the caller would see aggregates that silently omit rows.
        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Contains("maximum of 3 data rows", result.FailureReason);
    }

    [Fact]
    public async Task Stops_retaining_errors_past_the_cap_but_keeps_counting_them()
    {
        var rows = string.Join(
            '\n',
            Enumerable.Range(1, 10).Select(i => $"TXN-{i},not-a-date,Row,1.00,AUD,Fuel"));

        var result = await ProcessAsync(
            $"{Header}\n{rows}",
            new FileProcessingOptions { MaxRetainedErrors = 4 });

        Assert.Equal(10, result.InvalidRows);
        Assert.Equal(4, result.Errors.Count);
        Assert.True(result.ErrorsTruncated);
    }

    [Fact]
    public async Task Strips_a_utf8_byte_order_mark_from_the_first_header_name()
    {
        // Excel writes a BOM. Without stripping it the first column would be named "﻿TransactionId"
        // and every file exported from Excel would be rejected as missing a required column.
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes($"{Header}\nTXN-1,2026-07-01,Row,10.00,AUD,Fuel\n"))
            .ToArray();

        var result = await ProcessAsync(new MemoryStream(bytes));

        Assert.Equal(ProcessingStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Fails_when_the_content_is_not_csv_at_all()
    {
        // The header has to be valid, otherwise the file is rejected before the parser ever
        // reaches the malformed row.
        var result = await ProcessAsync($"{Header}\nTXN-1,2026-07-01,\"never closed,10.00,AUD,Fuel");

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Equal("file.malformed_csv", Assert.Single(result.Errors).Code);
    }

    private static Task<FileProcessingResult> ProcessAsync(string content, FileProcessingOptions? options = null) =>
        ProcessAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), options);

    private static Task<FileProcessingResult> ProcessAsync(Stream content, FileProcessingOptions? options = null)
    {
        var processor = new TransactionCsvProcessor(
            Options.Create(options ?? new FileProcessingOptions()),
            new FixedClock(),
            NullLogger<TransactionCsvProcessor>.Instance);

        return processor.ProcessAsync(content);
    }
}
