namespace FileProcessing.Core.Domain;

/// <summary>Lifecycle state of a single uploaded file.</summary>
public enum ProcessingStatus
{
    /// <summary>Accepted and recorded, but processing has not finished yet.</summary>
    Pending = 0,

    /// <summary>Every row parsed and validated successfully.</summary>
    Succeeded = 1,

    /// <summary>The file was processed, but one or more rows were rejected.</summary>
    CompletedWithErrors = 2,

    /// <summary>The file could not be processed at all (for example a malformed header).</summary>
    Failed = 3,
}
