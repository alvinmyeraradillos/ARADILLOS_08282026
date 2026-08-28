namespace FileProcessing.Api.Middleware;

/// <summary>
/// Gives every request a correlation id, echoes it back, and puts it in the log scope.
/// </summary>
/// <remarks>
/// An inbound id is accepted so a caller can stitch its logs to ours, but it is validated first:
/// the value ends up in log output and in a response header, so an unvalidated one would let a
/// caller inject newlines into logs or control characters into a header.
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);

        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId,
               }))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values)
            && values.Count == 1
            && IsAcceptable(values[0]))
        {
            return values[0]!;
        }

        return Guid.NewGuid().ToString("n");
    }

    private static bool IsAcceptable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            // Printable ASCII, minus the characters that would let a value break out of a header
            // or a log line.
            if (c is < ' ' or > '~' || c is '"' or ',' or ';')
            {
                return false;
            }
        }

        return true;
    }
}
