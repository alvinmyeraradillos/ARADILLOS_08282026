using FileProcessing.Api.Authentication;
using FileProcessing.Api.Contracts;
using FileProcessing.Api.RateLimiting;
using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Domain;
using FileProcessing.Core.Processing;
using FileProcessing.Core.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FileProcessing.Api.Controllers;

/// <summary>Upload files for processing and read back what the service has processed.</summary>
[ApiController]
[Route("api/v1/files")]
[Produces("application/json", "application/problem+json")]
public sealed class FilesController(
    FileIngestionService ingestion,
    IProcessedFileRepository repository,
    IOptions<FileProcessingOptions> options,
    ILogger<FilesController> logger) : ControllerBase
{
    private readonly FileProcessingOptions _options = options.Value;

    /// <summary>Uploads a transactions CSV, processes it, and records the result.</summary>
    /// <remarks>
    /// Returns 201 with the aggregates and any row-level errors. A file that could not be
    /// processed at all (bad header, not CSV, no data rows) is 422 — it was understood as a
    /// request but the content could not be acted on — while a file rejected before processing
    /// is 413 or 415.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.UploadFiles)]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting(RateLimitPolicies.Upload)]
    [ProducesResponseType<FileProcessingResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadAsync(
        [FromForm] UploadFileRequest request,
        CancellationToken cancellationToken)
    {
        if (request.File is not { } formFile)
        {
            ModelState.AddModelError(nameof(request.File), "A file part named 'file' is required.");
            return ValidationProblem(ModelState);
        }

        if (formFile.Length == 0)
        {
            ModelState.AddModelError(nameof(request.File), "The uploaded file is empty.");
            return ValidationProblem(ModelState);
        }

        // The declared length is a cheap first gate. HashingReadStream enforces the same limit
        // against the bytes actually delivered, because Content-Length can lie.
        if (formFile.Length > _options.MaxFileSizeInBytes)
        {
            return Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Payload too large",
                detail: $"The upload exceeds the maximum permitted size of {_options.MaxFileSizeInBytes} bytes.");
        }

        var clientId = User.GetClientId();

        await using var content = formFile.OpenReadStream();
        var upload = new FileUpload(formFile.FileName, formFile.ContentType ?? string.Empty, content);
        var outcome = await ingestion.IngestAsync(upload, clientId, cancellationToken);

        return outcome switch
        {
            IngestionResult.Success { Processing.Status: ProcessingStatus.Failed } failed
                => UnprocessableFile(failed),

            // CreatedAtRoute, not CreatedAtAction: the route name survives the framework's
            // stripping of the "Async" suffix from action names.
            IngestionResult.Success success
                => CreatedAtRoute(
                    "GetProcessedFile",
                    new { id = success.File.Id },
                    ResponseMapper.ToProcessingResponse(success.File, success.Processing)),

            IngestionResult.Duplicate duplicate
                => Conflict(WithExtensions(
                    new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "Duplicate file",
                        Detail = "A file with identical content has already been processed for this client.",
                    },
                    ("existingFileId", duplicate.ExistingFileId),
                    ("fileId", duplicate.File.Id))),

            IngestionResult.Rejected { Code: "file.too_large" } rejected
                => Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Payload too large",
                    detail: rejected.Message),

            IngestionResult.Rejected rejected
                => StatusCode(
                    StatusCodes.Status415UnsupportedMediaType,
                    WithExtensions(
                        new ProblemDetails
                        {
                            Status = StatusCodes.Status415UnsupportedMediaType,
                            Title = "Unsupported file",
                            Detail = rejected.Message,
                        },
                        ("code", rejected.Code),
                        ("fileId", rejected.File.Id))),

            _ => throw new InvalidOperationException($"Unhandled ingestion result '{outcome.GetType().Name}'."),
        };
    }

    /// <summary>Lists tracked files, newest first.</summary>
    /// <remarks>
    /// A caller sees only its own uploads unless its key carries the cross-client read scope.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ReadFiles)]
    [ProducesResponseType<PagedResponse<ProcessedFileSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResponse<ProcessedFileSummaryResponse>>> ListAsync(
        [FromQuery] ListFilesRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ReceivedFrom is { } from && request.ReceivedTo is { } to && from > to)
        {
            ModelState.AddModelError(nameof(request.ReceivedFrom), "receivedFrom must not be later than receivedTo.");
            return ValidationProblem(ModelState);
        }

        var page = await repository.QueryAsync(
            new ProcessedFileQuery
            {
                RestrictToClientId = User.GetReadRestriction(),
                Status = request.Status,
                ReceivedFromUtc = request.ReceivedFrom,
                ReceivedToUtc = request.ReceivedTo,
                FileNameContains = request.FileName,
                Page = request.Page,
                PageSize = request.PageSize,
            },
            cancellationToken);

        return Ok(ResponseMapper.ToPagedResponse(page));
    }

    /// <summary>Returns one tracked file with its retained row errors.</summary>
    [HttpGet("{id:guid}", Name = "GetProcessedFile")]
    [Authorize(Policy = AuthorizationPolicies.ReadFiles)]
    [ProducesResponseType<ProcessedFileDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessedFileDetailResponse>> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var file = await repository.GetAsync(id, User.GetReadRestriction(), cancellationToken);

        if (file is null)
        {
            // A file belonging to another client is reported as missing rather than forbidden, so
            // the response cannot be used to probe which ids exist.
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: $"No processed file with id {id} is available to this client.");
        }

        return Ok(ResponseMapper.ToDetail(file));
    }

    private IActionResult UnprocessableFile(IngestionResult.Success failed)
    {
        logger.LogInformation(
            "File {FileId} could not be processed: {Reason}",
            failed.File.Id,
            failed.File.FailureReason);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "File could not be processed",
            Detail = failed.File.FailureReason ?? "The file could not be processed.",
        };

        return UnprocessableEntity(WithExtensions(
            problem,
            ("fileId", failed.File.Id),
            ("errors", failed.Processing.Errors
                .Select(e => new ProcessingErrorResponse(e.LineNumber, e.Field, e.Code, e.Message))
                .ToArray())));
    }

    private static ProblemDetails WithExtensions(
        ProblemDetails problem,
        params (string Key, object? Value)[] extensions)
    {
        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }
}
