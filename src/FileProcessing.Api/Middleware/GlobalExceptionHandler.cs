using FileProcessing.Core.Csv;
using FileProcessing.Core.Io;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FileProcessing.Api.Middleware;

/// <summary>
/// Turns unhandled exceptions into RFC 7807 problem responses.
/// </summary>
/// <remarks>
/// The mapped cases are the ones that can still escape a controller — a body that overruns the
/// transport limit, or a stream that turns out not to be CSV. Everything else becomes an opaque
/// 500: the exception is logged in full with the correlation id, and the caller is told nothing
/// about the internals. Stack traces and type names are diagnostic gold for an attacker and of no
/// use to a legitimate client.
/// </remarks>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = Map(exception);

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                exception,
                "Unhandled exception for {Method} {Path} (correlation {CorrelationId}).",
                httpContext.Request.Method,
                httpContext.Request.Path,
                httpContext.TraceIdentifier);
        }
        else
        {
            logger.LogWarning(
                "Request for {Method} {Path} failed with {StatusCode}: {Reason}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                statusCode,
                exception.Message);
        }

        httpContext.Response.StatusCode = statusCode;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
                Instance = httpContext.Request.Path,
            },
        });
    }

    private static (int StatusCode, string Title, string Detail) Map(Exception exception) => exception switch
    {
        FileTooLargeException ex => (
            StatusCodes.Status413PayloadTooLarge,
            "Payload too large",
            ex.Message),

        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => (
            StatusCodes.Status413PayloadTooLarge,
            "Payload too large",
            "The request body exceeds the configured limit."),

        BadHttpRequestException ex => (
            StatusCodes.Status400BadRequest,
            "Malformed request",
            ex.Message),

        CsvParseException => (
            StatusCodes.Status422UnprocessableEntity,
            "File could not be processed",
            "The uploaded file is not well-formed CSV."),

        OperationCanceledException => (
            StatusCodesExtra.ClientClosedRequest,
            "Request cancelled",
            "The request was cancelled before it completed."),

        _ => (
            StatusCodes.Status500InternalServerError,
            "An unexpected error occurred",
            "The request could not be completed. Quote the correlation id when reporting this."),
    };
}

/// <summary>Status codes ASP.NET Core does not define but that this service uses.</summary>
internal static class StatusCodesExtra
{
    /// <summary>Non-standard but widely understood: the client went away mid-request.</summary>
    public const int ClientClosedRequest = 499;
}
