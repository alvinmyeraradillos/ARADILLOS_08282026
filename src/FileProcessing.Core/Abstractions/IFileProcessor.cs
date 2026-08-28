using FileProcessing.Core.Processing;

namespace FileProcessing.Core.Abstractions;

/// <summary>
/// Turns an uploaded byte stream into a processing result. Implementations must read the stream
/// once, forward-only, so that large uploads never have to be buffered in memory.
/// </summary>
public interface IFileProcessor
{
    /// <summary>Human-readable name of the format handled, used in logs and responses.</summary>
    string FormatName { get; }

    Task<FileProcessingResult> ProcessAsync(Stream content, CancellationToken cancellationToken = default);
}
