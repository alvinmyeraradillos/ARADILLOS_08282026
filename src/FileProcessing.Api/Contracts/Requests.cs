using System.ComponentModel.DataAnnotations;
using FileProcessing.Core.Domain;

namespace FileProcessing.Api.Contracts;

/// <summary>The multipart body accepted by the upload endpoint.</summary>
public sealed class UploadFileRequest
{
    /// <summary>The CSV file to process. Sent as the <c>file</c> part of a multipart form.</summary>
    [Required(ErrorMessage = "A file part named 'file' is required.")]
    public IFormFile? File { get; set; }
}

/// <summary>Filter and paging arguments for the processed-file listing.</summary>
public sealed class ListFilesRequest
{
    /// <summary>Restrict to one processing status.</summary>
    public ProcessingStatus? Status { get; set; }

    /// <summary>Only files received at or after this instant.</summary>
    public DateTimeOffset? ReceivedFrom { get; set; }

    /// <summary>Only files received at or before this instant.</summary>
    public DateTimeOffset? ReceivedTo { get; set; }

    /// <summary>Case-insensitive substring match on the stored file name.</summary>
    [StringLength(255)]
    public string? FileName { get; set; }

    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>Capped server side; see the repository's maximum page size.</summary>
    [Range(1, 200)]
    public int PageSize { get; set; } = 25;
}

/// <summary>Date-range arguments for the summary report.</summary>
public sealed class SummaryReportRequest
{
    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }
}
