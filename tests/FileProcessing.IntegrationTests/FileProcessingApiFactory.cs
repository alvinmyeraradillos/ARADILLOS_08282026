using FileProcessing.Api.Authentication;
using FileProcessing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FileProcessing.IntegrationTests;

/// <summary>
/// Hosts the real application — real middleware, real authentication handler, real controllers —
/// with only the database swapped out.
/// </summary>
/// <remarks>
/// The store is replaced with the EF in-memory provider so <c>dotnet test</c> runs on any machine
/// with no PostgreSQL and no Docker. That is a deliberate trade-off: it keeps the suite fast and
/// portable, at the cost of not exercising provider-specific SQL. The queries this service issues
/// are ordinary LINQ with no raw SQL, so the risk is small — but it is real, and a production
/// pipeline should add a Testcontainers-backed run against PostgreSQL alongside this one.
///
/// Everything that the challenge is actually about — the API key check, scopes, tenant isolation,
/// status codes, limits and the tracking behaviour — is exercised here end to end.
/// </remarks>
public class FileProcessingApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Full access: upload, read own files, read reports.</summary>
    public const string UploadKey = "integration-upload-key";

    /// <summary>A second tenant, used to prove one client cannot see another's files.</summary>
    public const string OtherTenantKey = "integration-other-tenant-key";

    /// <summary>Authenticates, but carries no files:write scope.</summary>
    public const string ReadOnlyKey = "integration-read-only-key";

    public const string UploadClientId = "integration-client";
    public const string OtherTenantClientId = "other-tenant";

    /// <summary>Overridden by derived fixtures that need different limits.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> AdditionalSettings => [];

    private readonly string _databaseName = $"fileprocessing-tests-{Guid.NewGuid():n}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development (so no start-up migration against a database that is not there) and not
        // Production (so no HTTPS redirect in front of the test client).
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: under minimal hosting the top-level statements
        // in Program.cs read builder.Configuration while the host is being built, which is before
        // ConfigureAppConfiguration callbacks run. Host settings are in place early enough.
        foreach (var (key, value) in Settings().Concat(AdditionalSettings))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            // EF 10 records the provider choice as an IDbContextOptionsConfiguration as well as on
            // DbContextOptions. Leaving that behind means Npgsql and the in-memory provider are
            // both configured, and EF refuses to resolve a context with two providers.
            services.RemoveAll<IDbContextOptionsConfiguration<FileProcessingDbContext>>();
            services.RemoveAll<DbContextOptions<FileProcessingDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<FileProcessingDbContext>();

            services.AddDbContext<FileProcessingDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
        });
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        // AddInfrastructure insists on a connection string. It is never dialled, because the
        // provider is replaced above, but the value has to be present for start-up to pass.
        ["ConnectionStrings:FileProcessingDb"] = "Host=not-used;Database=not-used",

        ["FileProcessing:MaxFileSizeInBytes"] = "1048576",
        ["FileProcessing:MaxRows"] = "1000",
        ["FileProcessing:MaxRetainedErrors"] = "50",
        ["FileProcessing:RejectDuplicateUploads"] = "false",

        // High enough that the throttle never fires by accident; a dedicated test lowers it.
        ["RateLimiting:UploadPermitLimit"] = "10000",
        ["RateLimiting:GlobalPermitLimit"] = "10000",

        ["Authentication:ApiKey:HeaderName"] = "X-Api-Key",

        ["Authentication:ApiKey:Clients:0:ClientId"] = UploadClientId,
        ["Authentication:ApiKey:Clients:0:DisplayName"] = "Integration client",
        ["Authentication:ApiKey:Clients:0:KeySha256"] = ConfiguredApiKeyValidator.Sha256Hex(UploadKey),
        ["Authentication:ApiKey:Clients:0:Scopes:0"] = ApiScopes.FilesWrite,
        ["Authentication:ApiKey:Clients:0:Scopes:1"] = ApiScopes.FilesRead,
        ["Authentication:ApiKey:Clients:0:Scopes:2"] = ApiScopes.ReportsRead,

        ["Authentication:ApiKey:Clients:1:ClientId"] = OtherTenantClientId,
        ["Authentication:ApiKey:Clients:1:DisplayName"] = "Other tenant",
        ["Authentication:ApiKey:Clients:1:KeySha256"] = ConfiguredApiKeyValidator.Sha256Hex(OtherTenantKey),
        ["Authentication:ApiKey:Clients:1:Scopes:0"] = ApiScopes.FilesWrite,
        ["Authentication:ApiKey:Clients:1:Scopes:1"] = ApiScopes.FilesRead,

        ["Authentication:ApiKey:Clients:2:ClientId"] = "read-only-client",
        ["Authentication:ApiKey:Clients:2:DisplayName"] = "Read only client",
        ["Authentication:ApiKey:Clients:2:KeySha256"] = ConfiguredApiKeyValidator.Sha256Hex(ReadOnlyKey),
        ["Authentication:ApiKey:Clients:2:Scopes:0"] = ApiScopes.FilesRead,
        ["Authentication:ApiKey:Clients:2:Scopes:1"] = ApiScopes.ReportsRead,
    };

    /// <summary>Creates a client that presents the given API key on every request.</summary>
    public HttpClient CreateClientWithKey(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }
}
