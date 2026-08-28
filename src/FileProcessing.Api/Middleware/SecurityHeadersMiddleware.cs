namespace FileProcessing.Api.Middleware;

/// <summary>
/// Applies the response headers appropriate to a pure JSON API.
/// </summary>
/// <remarks>
/// This is a JSON service, not a site, so the set is deliberately narrow: stop content sniffing,
/// refuse framing, send no referrer, and keep responses out of shared caches because every one of
/// them is scoped to a particular API key. A full CSP is omitted since nothing here renders HTML —
/// except Swagger UI in development, which is why the Swagger paths are skipped.
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";

        if (!context.Request.Path.StartsWithSegments("/swagger"))
        {
            headers.CacheControl = "no-store";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        }

        await next(context);
    }
}
