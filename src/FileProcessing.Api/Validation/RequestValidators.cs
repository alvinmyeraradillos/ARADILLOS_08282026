using FileProcessing.Api.Contracts;
using FluentValidation;

namespace FileProcessing.Api.Validation;

/// <summary>
/// Rules for the upload request.
/// </summary>
/// <remarks>
/// Presence and emptiness only. Size deliberately is <em>not</em> checked here: an oversized upload
/// is a <c>413</c>, not a <c>400</c>, and this filter's job is to produce validation problems. The
/// size limit is enforced by the transport, by the controller against the declared length, and
/// finally by the byte counter in the upload stream — a declared <c>Content-Length</c> is a claim,
/// not a fact.
/// </remarks>
public sealed class UploadFileRequestValidator : AbstractValidator<UploadFileRequest>
{
    public UploadFileRequestValidator()
    {
        RuleFor(request => request.File)
            .NotNull()
            .WithName("file")
            .WithMessage("A file part named 'file' is required.");

        RuleFor(request => request.File!.Length)
            .GreaterThan(0)
            .WithName("file")
            .WithMessage("The uploaded file is empty.")
            .When(request => request.File is not null);
    }
}

/// <summary>Rules for the processed-file listing.</summary>
public sealed class ListFilesRequestValidator : AbstractValidator<ListFilesRequest>
{
    public const int MaxPageSize = 200;

    public ListFilesRequestValidator()
    {
        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page must be 1 or greater.");

        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, MaxPageSize)
            .WithMessage($"pageSize must be between 1 and {MaxPageSize}.");

        RuleFor(request => request.FileName)
            .MaximumLength(255)
            .WithMessage("fileName must be 255 characters or fewer.");

        // Cross-field rules belong here rather than in the controller: an endpoint should be able
        // to trust that what it receives is already coherent.
        RuleFor(request => request.ReceivedFrom)
            .LessThanOrEqualTo(request => request.ReceivedTo)
            .When(request => request.ReceivedFrom.HasValue && request.ReceivedTo.HasValue)
            .WithMessage("receivedFrom must not be later than receivedTo.");
    }
}

/// <summary>Rules for the summary report.</summary>
public sealed class SummaryReportRequestValidator : AbstractValidator<SummaryReportRequest>
{
    public SummaryReportRequestValidator()
    {
        RuleFor(request => request.From)
            .LessThanOrEqualTo(request => request.To)
            .When(request => request.From.HasValue && request.To.HasValue)
            .WithMessage("from must not be later than to.");
    }
}
