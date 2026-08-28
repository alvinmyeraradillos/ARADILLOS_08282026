using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace FileProcessing.IntegrationTests;

/// <summary>
/// The security boundary, exercised through the real pipeline rather than by unit testing the
/// handler in isolation. These are the tests that would catch a middleware ordering mistake or an
/// endpoint that forgot its <c>[Authorize]</c> attribute.
/// </summary>
public sealed class ApiKeyAuthenticationTests(FileProcessingApiFactory factory)
    : IClassFixture<FileProcessingApiFactory>
{
    [Fact]
    public async Task Rejects_a_request_with_no_api_key()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("ApiKey", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Rejects_a_request_with_an_unknown_api_key()
    {
        var client = factory.CreateClientWithKey("definitely-not-a-real-key");

        var response = await client.GetAsync("/api/v1/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_request_with_an_empty_api_key_header()
    {
        var client = factory.CreateClientWithKey(string.Empty);

        var response = await client.GetAsync("/api/v1/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_request_presenting_two_api_key_headers()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FileProcessingApiFactory.UploadKey);
        client.DefaultRequestHeaders.Add("X-Api-Key", "a-second-value");

        var response = await client.GetAsync("/api/v1/files");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Does_not_disclose_why_a_key_was_refused()
    {
        var client = factory.CreateClientWithKey("definitely-not-a-real-key");

        var response = await client.GetAsync("/api/v1/files");
        var body = await response.Content.ReadAsStringAsync();

        // "unknown", "disabled" and "expired" must all look identical to the caller, otherwise the
        // response confirms which guessed keys are real.
        Assert.DoesNotContain("unknown", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Never_echoes_the_presented_key_back_to_the_caller()
    {
        const string presented = "some-guessed-secret-value";
        var client = factory.CreateClientWithKey(presented);

        var response = await client.GetAsync("/api/v1/files");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(presented, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepts_a_valid_key()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.GetAsync("/api/v1/files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_an_upload_from_a_key_without_the_write_scope()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.ReadOnlyKey);

        var response = await client.PostAsync("/api/v1/files", ValidCsvUpload());

        // 403, not 401: the caller is authenticated, it just is not permitted here.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_report_request_from_a_key_without_the_reports_scope()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.OtherTenantKey);

        var response = await client.GetAsync("/api/v1/reports/summary");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_are_reachable_without_a_key()
    {
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Applies_the_api_security_headers()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        var response = await client.GetAsync("/api/v1/files");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Echoes_a_supplied_correlation_id()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "abc-123");

        var response = await client.GetAsync("/api/v1/files");

        Assert.Equal("abc-123", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Replaces_a_correlation_id_that_could_poison_a_log_line()
    {
        var client = factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-Id", "safe\u001b[31m-value");

        var response = await client.GetAsync("/api/v1/files");
        var echoed = response.Headers.GetValues("X-Correlation-Id").Single();

        Assert.DoesNotContain('\u001b', echoed);
    }

    [Fact]
    public async Task Includes_a_correlation_id_on_error_responses()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/files");
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        Assert.NotNull(problem);
        Assert.True(problem.ContainsKey("correlationId"));
    }

    internal static MultipartFormDataContent ValidCsvUpload(
        string fileName = "transactions.csv",
        string? csv = null)
    {
        csv ??= """
            TransactionId,TransactionDate,Description,Amount,Currency,Category
            TXN-1,2026-07-01,Linehaul,100.00,AUD,Linehaul
            TXN-2,2026-07-02,Fuel,50.00,AUD,Fuel
            """;

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return new MultipartFormDataContent { { content, "file", fileName } };
    }
}
