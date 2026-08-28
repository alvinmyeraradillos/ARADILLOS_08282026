namespace FileProcessing.Core.Domain;

/// <summary>A single row- or field-level problem found while processing a file.</summary>
public sealed class ProcessingError
{
    // EF Core materialisation constructor.
    private ProcessingError()
    {
        Code = string.Empty;
        Message = string.Empty;
    }

    public ProcessingError(long lineNumber, string code, string message, string? field = null)
    {
        LineNumber = lineNumber;
        Code = code;
        Message = message;
        Field = field;
    }

    public int Id { get; private set; }

    /// <summary>1-based line number in the source file, including the header line.</summary>
    public long LineNumber { get; private set; }

    /// <summary>Stable machine-readable code, for example <c>amount.not_a_number</c>.</summary>
    public string Code { get; private set; }

    /// <summary>Human-readable explanation. Never contains raw field values.</summary>
    public string Message { get; private set; }

    /// <summary>Column the error relates to, when it is field specific.</summary>
    public string? Field { get; private set; }
}
