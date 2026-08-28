using System.Text.Json.Serialization;
using FileProcessing.Api.Authentication;
using FileProcessing.Api.Middleware;
using FileProcessing.Api.OpenApi;
using FileProcessing.Api.RateLimiting;
using FileProcessing.Core;
using FileProcessing.Core.Processing;
using FileProcessing.Infrastructure;
using FileProcessing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Transport limits.
//
// The file size limit is configured once and enforced at three levels: Kestrel refuses the
// connection, the form reader refuses the multipart section, and HashingReadStream refuses the
// bytes. Each layer protects the one below it, so no single misconfiguration opens the door.
// ---------------------------------------------------------------------------------------------
var maxFileSize = builder.Configuration.GetValue(
    $"{FileProcessingOptions.SectionName}:{nameof(FileProcessingOptions.MaxFileSizeInBytes)}",
    10L * 1024 * 1024);

// Multipart framing costs a little over the payload itself; 64 KiB is ample for the boundary,
// headers and the trailing CRLF without giving a caller meaningful extra room.
const long MultipartOverheadAllowance = 64 * 1024;

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = maxFileSize + MultipartOverheadAllowance;
});

builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = maxFileSize;
    form.MultipartHeadersLengthLimit = 16 * 1024;
    form.ValueLengthLimit = 16 * 1024;
    form.ValueCountLimit = 16;
});

// ---------------------------------------------------------------------------------------------
// Application services.
// ---------------------------------------------------------------------------------------------
builder.Services.AddFileProcessingCore(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiKeyAuthentication(builder.Configuration);
builder.Services.AddClientRateLimiting(builder.Configuration);

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<FileProcessingDbContext>(
        "database",
        HealthStatus.Unhealthy,
        tags: ["ready"]);

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        // Every error carries the correlation id, so a client report can be tied to a log line
        // without the response having to expose anything about the failure itself.
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
    });

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services
    .AddControllers(options => options.ReturnHttpNotAcceptable = true)
    .AddJsonOptions(json =>
    {
        json.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.Configure<ApiBehaviorOptions>(options =>
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Instance = context.HttpContext.Request.Path,
        };
        problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    });

builder.Services.AddApiDocumentation();

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Pipeline. Order matters:
//   exception handling wraps everything, so even a failure inside auth produces a problem
//   response; the correlation id is assigned before anything can log; authentication runs before
//   the rate limiter so buckets can be keyed by client rather than by IP; authorization runs last
//   so a throttled caller is rejected before any endpoint work happens.
// ---------------------------------------------------------------------------------------------
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseApiDocumentation(app.Environment);

app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

// Liveness answers "is the process up"; readiness additionally proves the database is reachable.
// Both are anonymous so an orchestrator does not need a credential to schedule the container.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

await ApplyDevelopmentMigrationsAsync(app);

await app.RunAsync();

// Brings the schema up to date in development only.
//
// Production deployments run migrations as a separate, reviewable step — a web process racing its
// own replicas to alter a schema is a good way to lose a database. A failure here is logged loudly
// but does not stop the host, so the API and its readiness probe still come up and say what is
// wrong.
static async Task ApplyDevelopmentMigrationsAsync(WebApplication app)
{
    if (!app.Environment.IsDevelopment())
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var context = scope.ServiceProvider.GetRequiredService<FileProcessingDbContext>();
        await context.Database.MigrateAsync();
        logger.LogInformation("Database schema is up to date.");
    }
    catch (Exception ex)
    {
        logger.LogCritical(
            ex,
            "Could not reach or migrate the database. Start PostgreSQL (docker compose up -d db) "
            + "and check ConnectionStrings:FileProcessingDb. The API is running but every request "
            + "that touches storage will fail.");
    }
}

/// <summary>Exposed so the integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program;
