using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace FileProcessing.IntegrationTests;

/// <summary>A host whose upload throttle is low enough to trip deliberately.</summary>
public sealed class ThrottledApiFactory : FileProcessingApiFactory
{
    public const int UploadPermitLimit = 2;

    protected override IEnumerable<KeyValuePair<string, string?>> AdditionalSettings =>
    [
        new("RateLimiting:UploadPermitLimit", UploadPermitLimit.ToString()),
        new("RateLimiting:UploadWindowSeconds", "60"),
    ];
}

/// <summary>
/// Each test gets its own host. A rate-limit bucket is per-client state that survives across
/// requests, so sharing a fixture would let one test spend another test's allowance.
/// </summary>
public sealed class RateLimitingTests : IDisposable
{
    private readonly ThrottledApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private const string Csv = """
        TransactionId,TransactionDate,Description,Amount,Currency,Category
        TXN-1,2026-07-01,Linehaul,100.00,AUD,Linehaul
        """;

    [Fact]
    public async Task Throttles_a_client_that_exceeds_the_upload_limit()
    {
        var client = _factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);

        for (var i = 0; i < ThrottledApiFactory.UploadPermitLimit; i++)
        {
            var allowed = await client.PostAsync("/api/v1/files", Upload());
            Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        }

        var throttled = await client.PostAsync("/api/v1/files", Upload());

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("application/problem+json", throttled.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Throttling_one_client_does_not_affect_another()
    {
        var noisy = _factory.CreateClientWithKey(FileProcessingApiFactory.UploadKey);
        var quiet = _factory.CreateClientWithKey(FileProcessingApiFactory.OtherTenantKey);

        for (var i = 0; i <= ThrottledApiFactory.UploadPermitLimit; i++)
        {
            await noisy.PostAsync("/api/v1/files", Upload());
        }

        // Buckets are partitioned by API client, so a neighbour's burst must not spend this
        // client's allowance.
        var response = await quiet.PostAsync("/api/v1/files", Upload());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static MultipartFormDataContent Upload()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Csv));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return new MultipartFormDataContent { { content, "file", "transactions.csv" } };
    }
}
