using System.ComponentModel.DataAnnotations;

namespace FileProcessing.Core.Processing;

/// <summary>Limits and policy for file processing, bound from the <c>FileProcessing</c> config section.</summary>
public sealed class FileProcessingOptions
{
    public const string SectionName = "FileProcessing";

    /// <summary>Largest upload accepted, in bytes. Enforced by the transport and again while reading.</summary>
    [Range(1, 512L * 1024 * 1024)]
    public long MaxFileSizeInBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Largest number of data rows processed from a single file.</summary>
    [Range(1, 5_000_000)]
    public int MaxRows { get; set; } = 100_000;

    /// <summary>How many individual row errors are retained and returned. Beyond this the count still grows.</summary>
    [Range(1, 10_000)]
    public int MaxRetainedErrors { get; set; } = 100;

    /// <summary>File name extensions accepted, lower-case and including the leading dot.</summary>
    public string[] AllowedExtensions { get; set; } = [".csv"];

    /// <summary>Content types accepted on the multipart section.</summary>
    public string[] AllowedContentTypes { get; set; } =
        ["text/csv", "application/csv", "text/plain", "application/vnd.ms-excel", "application/octet-stream"];

    /// <summary>ISO 4217 codes accepted in the currency column.</summary>
    public string[] AllowedCurrencies { get; set; } = ["AUD", "NZD", "USD", "EUR", "GBP", "SGD"];

    /// <summary>
    /// Rejects a file whose SHA-256 has already been processed successfully by the same client.
    /// </summary>
    /// <remarks>
    /// Off by default, matching appsettings.json. Re-sending identical bytes is often deliberate —
    /// a retry after a timeout, or a nightly export that genuinely has not changed — so refusing it
    /// is a policy decision for the deployment rather than something the service should assume.
    /// </remarks>
    public bool RejectDuplicateUploads { get; set; }
}
