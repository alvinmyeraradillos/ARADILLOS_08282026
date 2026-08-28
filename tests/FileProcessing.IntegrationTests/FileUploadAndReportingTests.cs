using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FileProcessing.Api.Contracts;

namespace FileProcessing.IntegrationTests;

/// <summary>
/// The upload, tracking and reporting behaviour, end to end over HTTP.
/// </summary>
public sealed class FileUploadAndReportingTests(FileProcessingApiFactory factory)
    : IClassFixture<FileProcessingApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ValidCsv = """
        TransactionId,TransactionDate,Description,Amount,Currency,Category
        TXN-1,2026-07-01,Linehaul,100.00,AUD,Linehaul
        TXN-2,2026-07-02,"Fuel levy, July",50.00,AUD,Fuel
        TXN-3,2026-07-03,Fuel top up,25.00,AUD,Fuel
        """;

    [Fact]
    public async Task Processes_a_valid_file_and_returns_the_aggregates()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.PostAsync("/api/v1/files", Upload(ValidCsv));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var result = await response.Content.ReadFromJsonAsync<FileProcessingResponse>(Json);

        Assert.NotNull(result);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(3, result.Rows.Total);
        Assert.Equal(3, result.Rows.Valid);
        Assert.Equal(0, result.Rows.Invalid);
        Assert.Equal(175.00m, result.Aggregates.TotalAmount);
        Assert.Equal(58.33m, result.Aggregates.AverageAmount);
        Assert.Equal(75.00m, result.Aggregates.ByCategory.Single(c => c.Category == "Fuel").TotalAmount);
        Assert.Equal(37.50m, result.Aggregates.ByCategory.Single(c => c.Category == "Fuel").AverageAmount);
        Assert.Equal(new DateOnly(2026, 7, 1), result.Aggregates.EarliestTransactionDate);
        Assert.Empty(result.Errors);

        // Tracking details the brief asks for: name, timing and size, all recorded.
        Assert.Equal("transactions.csv", result.FileName);
        Assert.True(result.DurationMilliseconds >= 0);
        Assert.True(result.SizeBytes > 0);
        Assert.Equal(64, result.Sha256.Length);
    }

    [Fact]
    public async Task Reports_row_errors_without_discarding_the_good_rows()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);
        const string csv = """
            TransactionId,TransactionDate,Description,Amount,Currency,Category
            TXN-10,2026-07-01,Good,100.00,AUD,Linehaul
            TXN-11,31/07/2026,Bad date,50.00,AUD,Fuel
            TXN-12,2026-07-03,Bad currency,25.00,AUDD,Fuel
            """;

        var response = await client.PostAsync("/api/v1/files", Upload(csv));
        var result = await response.Content.ReadFromJsonAsync<FileProcessingResponse>(Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal("CompletedWithErrors", result.Status);
        Assert.Equal(1, result.Rows.Valid);
        Assert.Equal(2, result.Rows.Invalid);
        Assert.Equal(100.00m, result.Aggregates.TotalAmount);

        Assert.Collection(
            result.Errors.OrderBy(e => e.Line),
            first =>
            {
                Assert.Equal(3, first.Line);
                Assert.Equal("transactionDate.invalid_format", first.Code);
            },
            second =>
            {
                Assert.Equal(4, second.Line);
                Assert.Equal("currency.invalid_format", second.Code);
            });
    }

    [Fact]
    public async Task Returns_422_for_a_file_whose_header_is_wrong()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.PostAsync("/api/v1/files", Upload("Id,Date,Notes\n1,2026-07-01,nope"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("missing required columns", problem.GetProperty("detail").GetString());

        // The attempt is still tracked, and the response says where to find it.
        Assert.True(problem.TryGetProperty("fileId", out _));
    }

    [Fact]
    public async Task Returns_415_for_a_file_extension_that_is_not_allowed()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.PostAsync("/api/v1/files", Upload(ValidCsv, fileName: "notes.txt"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Returns_400_when_no_file_part_is_supplied()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.PostAsync("/api/v1/files", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_400_for_an_empty_file()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.PostAsync("/api/v1/files", Upload(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tracks_an_upload_and_serves_it_back_by_id()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var upload = await client.PostAsync("/api/v1/files", Upload(ValidCsv, fileName: "tracked.csv"));
        var created = await upload.Content.ReadFromJsonAsync<FileProcessingResponse>(Json);
        Assert.NotNull(created);

        var detail = await client.GetFromJsonAsync<ProcessedFileDetailResponse>(
            $"/api/v1/files/{created.FileId}",
            Json);

        Assert.NotNull(detail);
        Assert.Equal("tracked.csv", detail.File.FileName);
        Assert.Equal(FileProcessingApiFactory.UploadClientId, detail.File.ClientId);
        Assert.Equal(3, detail.File.Rows.Total);
        Assert.Equal(175.00m, detail.File.TotalAmount);
    }

    [Fact]
    public async Task Lists_tracked_files_for_the_calling_client()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        await client.PostAsync("/api/v1/files", Upload(ValidCsv, fileName: "listed.csv"));

        var page = await client.GetFromJsonAsync<PagedResponse<ProcessedFileSummaryResponse>>(
            "/api/v1/files?page=1&pageSize=50",
            Json);

        Assert.NotNull(page);
        Assert.Contains(page.Items, f => f.FileName == "listed.csv");
        Assert.All(page.Items, f => Assert.Equal(FileProcessingApiFactory.UploadClientId, f.ClientId));
    }

    [Fact]
    public async Task Filters_the_listing_by_status()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        await client.PostAsync("/api/v1/files", Upload(ValidCsv, fileName: "filtered.csv"));

        var page = await client.GetFromJsonAsync<PagedResponse<ProcessedFileSummaryResponse>>(
            "/api/v1/files?status=Succeeded&pageSize=50",
            Json);

        Assert.NotNull(page);
        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, f => Assert.Equal("Succeeded", f.Status));
    }

    [Fact]
    public async Task Rejects_a_listing_whose_date_range_is_inverted()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.GetAsync(
            "/api/v1/files?receivedFrom=2026-08-01T00:00:00Z&receivedTo=2026-07-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task One_client_cannot_read_another_clients_file()
    {
        var owner = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);
        var stranger = factory.CreateClientWithKey(FileProcessingApiFactory.OtherTenantKey);

        var upload = await owner.PostAsync("/api/v1/files", Upload(ValidCsv, fileName: "private.csv"));
        var created = await upload.Content.ReadFromJsonAsync<FileProcessingResponse>(Json);
        Assert.NotNull(created);

        var response = await stranger.GetAsync($"/api/v1/files/{created.FileId}");

        // 404 rather than 403: a 403 would confirm the id exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_clients_files_do_not_appear_in_the_listing()
    {
        var owner = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);
        var stranger = factory.CreateClientWithKey(FileProcessingApiFactory.OtherTenantKey);

        await owner.PostAsync("/api/v1/files", Upload(ValidCsv, fileName: "owner-only.csv"));

        var page = await stranger.GetFromJsonAsync<PagedResponse<ProcessedFileSummaryResponse>>(
            "/api/v1/files?pageSize=100",
            Json);

        Assert.NotNull(page);
        Assert.DoesNotContain(page.Items, f => f.FileName == "owner-only.csv");
    }

    [Fact]
    public async Task Returns_404_for_an_id_that_does_not_exist()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.GetAsync($"/api/v1/files/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Summary_report_counts_what_the_client_has_processed()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        await client.PostAsync("/api/v1/files", Upload(ValidCsv, fileName: "reported.csv"));

        var report = await client.GetFromJsonAsync<SummaryReportResponse>("/api/v1/reports/summary", Json);

        Assert.NotNull(report);
        Assert.True(report.TotalFiles >= 1);
        Assert.True(report.Rows.Total >= 3);
        Assert.True(report.TotalAmount >= 175.00m);
        Assert.True(report.AverageDurationMilliseconds >= 0);
        Assert.Contains(report.ByClient, c => c.ClientId == FileProcessingApiFactory.UploadClientId);
    }

    [Fact]
    public async Task Summary_report_honours_a_date_window()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        await client.PostAsync("/api/v1/files", Upload(ValidCsv));

        var report = await client.GetFromJsonAsync<SummaryReportResponse>(
            "/api/v1/reports/summary?from=2000-01-01T00:00:00Z&to=2000-01-02T00:00:00Z",
            Json);

        Assert.NotNull(report);
        Assert.Equal(0, report.TotalFiles);
    }

    private static MultipartFormDataContent Upload(string csv, string fileName = "transactions.csv")
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return new MultipartFormDataContent { { content, "file", fileName } };
    }
}
