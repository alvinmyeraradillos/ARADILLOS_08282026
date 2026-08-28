namespace FileProcessing.Core.Abstractions;

/// <summary>Indirection over the system clock so that time-dependent logic stays testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
