using System.Diagnostics;
using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Domain;
using FileProcessing.Core.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileProcessing.Core.Processing;

/// <summary>An upload handed to the ingestion service, decoupled from ASP.NET's IFormFile.</summary>
public sealed record FileUpload(string FileName, string ContentType, Stream Content);

/// <summary>
/// Outcome of an ingestion attempt. A closed hierarchy rather than exceptions, because a rejected
/// or duplicate upload is an expected result the caller must handle, not an exceptional one.
/// </summary>
public abstract record IngestionResult
{
    private IngestionResult()
    {
    }

    /// <summary>The file was processed. Row-level problems are inside <paramref name="Processing"/>.</summary>
    public sealed record Success(ProcessedFile File, FileProcessingResult Processing) : IngestionResult;

    /// <summary>The same client already submitted a file with an identical digest.</summary>
    public sealed record Duplicate(ProcessedFile File, Guid ExistingFileId) : IngestionResult;

    /// <summary>The upload never reached the processor: wrong type, too large, or unreadable.</summary>
    public sealed record Rejected(ProcessedFile File, string Code, string Message) : IngestionResult;
}

/// <summary>
/// Orchestrates one upload end to end: record the attempt, enforce the transport-independent
/// limits, run the processor, then close out the audit record.
/// </summary>
/// <remarks>
/// The audit row is written before processing starts and updated afterwards. That costs an extra
/// round trip, but it means a process that dies mid-file still leaves evidence that the file
/// arrived, which is the whole point of a tracking table.
/// </remarks>
public sealed class FileIngestionService(
    IFileProcessor processor,
    IProcessedFileRepository repository,
    IOptions<FileProcessingOptions> options,
    IClock clock,
    ILogger<FileIngestionService> logger)
{
    private readonly FileProcessingOptions _options = options.Value;

    public async Task<IngestionResult> IngestAsync(
        FileUpload upload,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var fileName = SanitiseFileName(upload.FileName);
        var contentType = Truncate(upload.ContentType, 128);
        var file = new ProcessedFile(Guid.NewGuid(), fileName, contentType, clientId, clock.UtcNow);

        if (!IsAllowedExtension(fileName))
        {
            return await RejectAsync(
                file,
                "file.unsupported_extension",
                $"Only {string.Join(", ", _options.AllowedExtensions)} files are accepted.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsAllowedContentType(contentType))
        {
            return await RejectAsync(
                file,
                "file.unsupported_content_type",
                $"Content type '{contentType}' is not accepted for this endpoint.",
                cancellationToken).ConfigureAwait(false);
        }

        await repository.AddAsync(file, cancellationToken).ConfigureAwait(false);
        await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        await using var hashing = new HashingReadStream(upload.Content, _options.MaxFileSizeInBytes);

        FileProcessingResult result;
        try
        {
            result = await processor.ProcessAsync(hashing, cancellationToken).ConfigureAwait(false);
            await hashing.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FileTooLargeException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(
                "Client {ClientId} sent a file above the {MaxBytes} byte limit.",
                clientId,
                ex.MaxBytes);
            file.MarkFailed(
                clock.UtcNow,
                stopwatch.ElapsedMilliseconds,
                hashing.BytesRead,
                sha256: string.Empty,
                failureReason: ex.Message);
            await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new IngestionResult.Rejected(file, "file.too_large", ex.Message);
        }

        stopwatch.Stop();
        var digest = hashing.GetHashHex();

        if (_options.RejectDuplicateUploads)
        {
            // The digest is only known once the bytes have been read, so this check necessarily
            // happens after processing. The audit row still records the attempt.
            var existingId = await repository
                .FindDuplicateAsync(clientId, digest, cancellationToken)
                .ConfigureAwait(false);

            if (existingId is { } duplicateOf)
            {
                file.MarkFailed(
                    clock.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    hashing.BytesRead,
                    digest,
                    $"Duplicate of previously processed file {duplicateOf}.");
                await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new IngestionResult.Duplicate(file, duplicateOf);
            }
        }

        if (result.Status == ProcessingStatus.Failed)
        {
            file.MarkFailed(
                clock.UtcNow,
                stopwatch.ElapsedMilliseconds,
                hashing.BytesRead,
                digest,
                result.FailureReason ?? "The file could not be processed.",
                result.Errors);
        }
        else
        {
            file.MarkCompleted(
                result.Status,
                clock.UtcNow,
                stopwatch.ElapsedMilliseconds,
                hashing.BytesRead,
                digest,
                result.TotalRows,
                result.ValidRows,
                result.InvalidRows,
                result.TotalAmount,
                result.ErrorsTruncated,
                result.Errors);
        }

        await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Processed file {FileId} for {ClientId}: {Status}, {ValidRows}/{TotalRows} rows valid in {Duration}ms.",
            file.Id,
            clientId,
            file.Status,
            file.ValidRows,
            file.TotalRows,
            file.DurationMilliseconds);

        return new IngestionResult.Success(file, result);
    }

    private async Task<IngestionResult> RejectAsync(
        ProcessedFile file,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Rejected upload from {ClientId}: {Code}.", file.ClientId, code);
        file.MarkFailed(clock.UtcNow, durationMilliseconds: 0, sizeInBytes: 0, sha256: string.Empty, message);
        await repository.AddAsync(file, cancellationToken).ConfigureAwait(false);
        await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new IngestionResult.Rejected(file, code, message);
    }

    private bool IsAllowedExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0
               && _options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsAllowedContentType(string contentType)
    {
        // Strip any parameters such as "; charset=utf-8" before comparing.
        var separator = contentType.IndexOf(';');
        var mediaType = (separator >= 0 ? contentType[..separator] : contentType).Trim();
        return _options.AllowedContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces a client-supplied name to something safe to store and echo back. The result is
    /// never used to build a path — uploads are streamed, never written to disk under this name —
    /// but stripping directory separators and control characters keeps traversal sequences and
    /// terminal escapes out of the audit log and out of API responses.
    /// </summary>
    internal static string SanitiseFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "upload.csv";
        }

        var name = fileName.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            name = name[(lastSlash + 1)..];
        }

        Span<char> buffer = stackalloc char[Math.Min(name.Length, 255)];
        var length = 0;
        foreach (var c in name)
        {
            if (length == buffer.Length)
            {
                break;
            }

            buffer[length++] = char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c;
        }

        var sanitised = new string(buffer[..length]).Trim().TrimStart('.');
        return sanitised.Length == 0 ? "upload.csv" : sanitised;
    }

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];
}
