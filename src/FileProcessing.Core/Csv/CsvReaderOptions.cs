namespace FileProcessing.Core.Csv;

/// <summary>Hard limits applied while parsing, so that a hostile file cannot exhaust memory.</summary>
public sealed class CsvReaderOptions
{
    public char Delimiter { get; init; } = ',';

    public int MaxFieldLength { get; init; } = 4 * 1024;

    public int MaxFieldsPerRow { get; init; } = 64;

    /// <summary>Skips records that consist of a single empty field.</summary>
    public bool SkipBlankLines { get; init; } = true;
}
