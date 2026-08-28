namespace FileProcessing.Core.Csv;

/// <summary>One parsed CSV record together with the physical line it started on.</summary>
/// <param name="LineNumber">1-based line on which the record starts.</param>
/// <param name="Fields">Field values with quoting already resolved.</param>
public sealed record CsvRow(long LineNumber, IReadOnlyList<string> Fields);
