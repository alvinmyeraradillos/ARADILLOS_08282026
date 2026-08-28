using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FileProcessing.Api.Contracts;

namespace FileProcessing.IntegrationTests;

/// <summary>
/// Uploads the files in <c>samples/</c> and pins the exact figures published in the README.
/// </summary>
/// <remarks>
/// Documentation that nothing verifies drifts away from the code, and a worked example with the
/// wrong numbers in it is worse than no example: a reader trusts it and then cannot reproduce it.
/// These tests fail if either the sample files or the aggregation change.
/// </remarks>
public sealed class SampleFileTests(FileProcessingApiFactory factory)
    : IClassFixture<FileProcessingApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Clean_sample_produces_the_documented_aggregates()
    {
        var response = await UploadSampleAsync("transactions-valid.csv");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<FileProcessingResponse>(Json);
        Assert.NotNull(result);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(10, result.Rows.Total);
        Assert.Equal(10, result.Rows.Valid);
        Assert.Equal(0, result.Rows.Invalid);
        Assert.Empty(result.Errors);

        Assert.Equal(3721.85m, result.Aggregates.TotalAmount);
        Assert.Equal(372.19m, result.Aggregates.AverageAmount);
        Assert.Equal(3111.85m, result.Aggregates.TotalsByCurrency["AUD"]);
        Assert.Equal(610.00m, result.Aggregates.TotalsByCurrency["NZD"]);
        Assert.Equal(new DateOnly(2026, 7, 1), result.Aggregates.EarliestTransactionDate);
        Assert.Equal(new DateOnly(2026, 7, 22), result.Aggregates.LatestTransactionDate);

        var linehaul = result.Aggregates.ByCategory.Single(c => c.Category == "Linehaul");
        Assert.Equal(3, linehaul.Count);
        Assert.Equal(3040.25m, linehaul.TotalAmount);
        Assert.Equal(1013.42m, linehaul.AverageAmount);
    }

    [Fact]
    public async Task Error_sample_reports_the_documented_row_failures()
    {
        var response = await UploadSampleAsync("transactions-with-errors.csv");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<FileProcessingResponse>(Json);
        Assert.NotNull(result);

        Assert.Equal("CompletedWithErrors", result.Status);
        Assert.Equal(10, result.Rows.Total);
        Assert.Equal(2, result.Rows.Valid);
        Assert.Equal(8, result.Rows.Invalid);

        // Only the two good rows contribute to the aggregate: 500.00 + 275.50.
        Assert.Equal(775.50m, result.Aggregates.TotalAmount);
        Assert.Equal(387.75m, result.Aggregates.AverageAmount);

        // Every distinct failure mode the sample is built to exercise.
        Assert.Equal(
            [
                "transactionDate.invalid_format",
                "amount.not_a_number",
                "currency.not_allowed",
                "category.missing",
                "transactionId.duplicate",
                "amount.too_many_decimals",
                "row.column_count_mismatch",
                "transactionDate.in_future",
            ],
            result.Errors.OrderBy(e => e.Line).Select(e => e.Code));
    }

    [Fact]
    public async Task Bad_header_sample_is_rejected_as_unprocessable()
    {
        var response = await UploadSampleAsync("transactions-bad-header.csv");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("missing required columns", problem.GetProperty("detail").GetString());
    }

    private async Task<HttpResponseMessage> UploadSampleAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", fileName);
        Assert.True(File.Exists(path), $"Sample file not found: {path}");

        var content = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);
        return await client.PostAsync(
            "/api/v1/files",
            new MultipartFormDataContent { { content, "file", fileName } });
    }
}
