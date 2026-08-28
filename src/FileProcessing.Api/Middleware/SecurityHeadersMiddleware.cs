namespace FileProcessing.Api.Middleware;

/// <summary>
/// Applies response headers appropriate to what is being served.
/// </summary>
/// <remarks>
/// Three cases, because one policy cannot fit them all:
/// <list type="bullet">
/// <item><b>API responses</b> get the tightest policy. Nothing should ever be loaded or executed
/// from a JSON payload, and every response is scoped to one API key so it must not be cached by
/// anything shared.</item>
/// <item><b>The demo console</b> is a real HTML document, so it needs to load its own stylesheet
/// and script and call the API. It is granted exactly that and nothing more — note the absence of
/// <c>'unsafe-inline'</c>: the page carries no inline script or style, so it does not need it.</item>
/// <item><b>Swagger UI</b> is third-party markup that does rely on inline script and style, so its
/// own headers are left alone rather than shipping a policy that silently breaks it.</item>
/// </list>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ApiPolicy = "default-src 'none'; frame-ancestors 'none'";

    private const string DocumentPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
        + "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        var path = context.Request.Path;

        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";

        if (path.StartsWithSegments("/swagger"))
        {
            // Left to Swagger's own defaults.
        }
        else if (IsApiRequest(path))
        {
            headers.CacheControl = "no-store";
            headers["Content-Security-Policy"] = ApiPolicy;
        }
        else
        {
            headers["Content-Security-Policy"] = DocumentPolicy;
        }

        await next(context);
    }

    private static bool IsApiRequest(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/health");
}
