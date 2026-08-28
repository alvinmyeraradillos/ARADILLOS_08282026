using FileProcessing.Core.Domain;

namespace FileProcessing.Core.Validation;

/// <summary>
/// Collects errors up to a cap. Past the cap it keeps counting but stops allocating, so a file
/// where every row is broken cannot be used to exhaust memory.
/// </summary>
public sealed class ErrorSink(int capacity)
{
    private readonly List<ProcessingError> _errors = new(Math.Min(capacity, 64));

    public int Capacity { get; } = capacity;

    /// <summary>Number of errors found, including those not retained.</summary>
    public int TotalCount { get; private set; }

    public bool Truncated => TotalCount > Capacity;

    public IReadOnlyList<ProcessingError> Errors => _errors;

    public void Add(long lineNumber, string code, string message, string? field = null)
    {
        TotalCount++;
        if (_errors.Count < Capacity)
        {
            _errors.Add(new ProcessingError(lineNumber, code, message, field));
        }
    }
}
