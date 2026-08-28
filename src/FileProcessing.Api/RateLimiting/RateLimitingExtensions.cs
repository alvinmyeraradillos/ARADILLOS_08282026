using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using FileProcessing.Api.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileProcessing.Api.RateLimiting;

public static class RateLimitPolicies
{
    /// <summary>Applied to the upload endpoint, which is the expensive one.</summary>
    public const string Upload = "upload";
}

/// <summary>Bound from the <c>RateLimiting</c> configuration section.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Uploads permitted per client per window.</summary>
    [Range(1, 100_000)]
    public int UploadPermitLimit { get; set; } = 20;

    [Range(1, 3600)]
    public int UploadWindowSeconds { get; set; } = 60;

    /// <summary>Requests permitted per client per window across every other endpoint.</summary>
    [Range(1, 1_000_000)]
    public int GlobalPermitLimit { get; set; } = 300;

    [Range(1, 3600)]
    public int GlobalWindowSeconds { get; set; } = 60;
}

public static class RateLimitingExtensions
{
    /// <summary>
    /// Adds per-client rate limiting.
    /// </summary>
    /// <remarks>
    /// Partitioning is by API client rather than by IP: clients behind a shared NAT would
    /// otherwise throttle each other, and one client's burst would degrade everyone. The IP
    /// fallback only applies to unauthenticated traffic, which in practice is failed auth
    /// attempts — worth limiting, since that is where brute-force pressure lands.
    /// </remarks>
    public static IServiceCollection AddClientRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(RateLimitPolicies.Upload, context =>
            {
                var options = context.RequestServices
                    .GetRequiredService<IOptionsSnapshotAccessor<RateLimitingOptions>>()
                    .Value;

                return RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.UploadPermitLimit,
                        Window = TimeSpan.FromSeconds(options.UploadWindowSeconds),
                        QueueLimit = 0,
                    });
            });

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var options = context.RequestServices
                    .GetRequiredService<IOptionsSnapshotAccessor<RateLimitingOptions>>()
                    .Value;

                return RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.GlobalPermitLimit,
                        Window = TimeSpan.FromSeconds(options.GlobalWindowSeconds),
                        QueueLimit = 0,
                    });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Detail = "The request rate for this API key has been exceeded. Retry after a short delay.",
                    },
                    cancellationToken);
            };
        });

        return services;
    }

    private static string PartitionKey(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? $"client:{context.User.GetClientId()}"
            : $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
