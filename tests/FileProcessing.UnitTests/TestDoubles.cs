using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Domain;
using FileProcessing.Core.Reporting;

namespace FileProcessing.UnitTests;

/// <summary>A clock frozen at a known instant so date rules are deterministic.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public static readonly DateTimeOffset Default = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = now;

    public FixedClock()
        : this(Default)
    {
    }
}

/// <summary>
/// In-memory repository for the ingestion tests. Hand-written rather than mocked: the assertions
/// are about what ends up persisted, which reads far better against a real list than against
/// verified call expectations.
/// </summary>
public sealed class InMemoryProcessedFileRepository : IProcessedFileRepository
{
    public List<ProcessedFile> Files { get; } = [];

    public int SaveCount { get; private set; }

    public Task AddAsync(ProcessedFile file, CancellationToken cancellationToken = default)
    {
        Files.Add(file);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task<ProcessedFile?> GetAsync(
        Guid id,
        string? restrictToClientId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.FirstOrDefault(f =>
            f.Id == id && (restrictToClientId is null || f.ClientId == restrictToClientId)));

    public Task<PagedResult<ProcessedFile>> QueryAsync(
        ProcessedFileQuery query,
        CancellationToken cancellationToken = default)
    {
        var items = Files
            .Where(f => query.RestrictToClientId is null || f.ClientId == query.RestrictToClientId)
            .ToList();

        return Task.FromResult(new PagedResult<ProcessedFile>(items, query.Page, query.PageSize, items.Count));
    }

    public Task<ProcessingSummaryReport> SummariseAsync(
        ReportQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProcessingSummaryReport { TotalFiles = Files.Count });

    public Task<Guid?> FindDuplicateAsync(
        string clientId,
        string sha256,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Files
            .Where(f => f.ClientId == clientId && f.Sha256 == sha256 && f.Status != ProcessingStatus.Failed)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefault());
}
