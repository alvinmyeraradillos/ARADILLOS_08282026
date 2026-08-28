namespace FileProcessing.Core.Domain;

/// <summary>
/// Audit record for one upload. Written before processing starts so that a crash mid-process
/// still leaves a trace, then completed once the processor returns.
/// </summary>
public sealed class ProcessedFile
{
    private readonly List<ProcessingError> _errors = [];

    // EF Core materialisation constructor.
    private ProcessedFile()
    {
        FileName = string.Empty;
        ContentType = string.Empty;
        ClientId = string.Empty;
        Sha256 = string.Empty;
    }

    public ProcessedFile(
        Guid id,
        string fileName,
        string contentType,
        string clientId,
        DateTimeOffset receivedAtUtc)
    {
        Id = id;
        FileName = fileName;
        ContentType = contentType;
        ClientId = clientId;
        ReceivedAtUtc = receivedAtUtc;
        Status = ProcessingStatus.Pending;
        Sha256 = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Sanitised original file name. Never used to build a path on disk.</summary>
    public string FileName { get; private set; }

    public string ContentType { get; private set; }

    /// <summary>Authenticated API client that submitted the file.</summary>
    public string ClientId { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public long DurationMilliseconds { get; private set; }

    public ProcessingStatus Status { get; private set; }

    public long SizeInBytes { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the raw bytes, for de-duplication and audit.</summary>
    public string Sha256 { get; private set; }

    public int TotalRows { get; private set; }

    public int ValidRows { get; private set; }

    public int InvalidRows { get; private set; }

    /// <summary>Sum of the amounts on rows that passed validation.</summary>
    public decimal TotalAmount { get; private set; }

    /// <summary>Set when <see cref="Status"/> is <see cref="ProcessingStatus.Failed"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>True when more errors occurred than the configured retention cap.</summary>
    public bool ErrorsTruncated { get; private set; }

    public IReadOnlyCollection<ProcessingError> Errors => _errors;

    public void MarkCompleted(
        ProcessingStatus status,
        DateTimeOffset completedAtUtc,
        long durationMilliseconds,
        long sizeInBytes,
        string sha256,
        int totalRows,
        int validRows,
        int invalidRows,
        decimal totalAmount,
        bool errorsTruncated,
        IEnumerable<ProcessingError> errors)
    {
        Status = status;
        CompletedAtUtc = completedAtUtc;
        DurationMilliseconds = durationMilliseconds;
        SizeInBytes = sizeInBytes;
        Sha256 = sha256;
        TotalRows = totalRows;
        ValidRows = validRows;
        InvalidRows = invalidRows;
        TotalAmount = totalAmount;
        ErrorsTruncated = errorsTruncated;
        _errors.Clear();
        _errors.AddRange(errors);
    }

    public void MarkFailed(
        DateTimeOffset completedAtUtc,
        long durationMilliseconds,
        long sizeInBytes,
        string sha256,
        string failureReason,
        IEnumerable<ProcessingError>? errors = null)
    {
        Status = ProcessingStatus.Failed;
        CompletedAtUtc = completedAtUtc;
        DurationMilliseconds = durationMilliseconds;
        SizeInBytes = sizeInBytes;
        Sha256 = sha256;
        FailureReason = failureReason;
        _errors.Clear();
        if (errors is not null)
        {
            _errors.AddRange(errors);
        }
    }
}
