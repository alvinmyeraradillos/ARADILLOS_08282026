namespace FileProcessing.Core.Csv;

/// <summary>Raised when the byte stream cannot be interpreted as CSV at all.</summary>
public sealed class CsvParseException(string message, long lineNumber) : Exception(message)
{
    public long LineNumber { get; } = lineNumber;
}
