using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Domain;
using FileProcessing.Core.Reporting;
using Microsoft.EntityFrameworkCore;

namespace FileProcessing.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the audit store. Every read path applies the client restriction
/// first, so tenant isolation is a property of the query rather than of the caller.
/// </summary>
public sealed class ProcessedFileRepository(FileProcessingDbContext context) : IProcessedFileRepository
{
    private const int MaxPageSize = 200;

    public async Task AddAsync(ProcessedFile file, CancellationToken cancellationToken = default) =>
        await context.ProcessedFiles.AddAsync(file, cancellationToken).ConfigureAwait(false);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        context.SaveChangesAsync(cancellationToken);

    public Task<ProcessedFile?> GetAsync(
        Guid id,
        string? restrictToClientId,
        CancellationToken cancellationToken = default)
    {
        var query = context.ProcessedFiles
            .AsNoTracking()
            .Include(f => f.Errors)
            .Where(f => f.Id == id);

        if (restrictToClientId is not null)
        {
            query = query.Where(f => f.ClientId == restrictToClientId);
        }

        return query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<PagedResult<ProcessedFile>> QueryAsync(
        ProcessedFileQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        var filtered = Filter(context.ProcessedFiles.AsNoTracking(), query);

        var totalCount = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await filtered
            .OrderByDescending(f => f.ReceivedAtUtc)
            .ThenByDescending(f => f.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<ProcessedFile>(items, page, pageSize, totalCount);
    }

    public async Task<ProcessingSummaryReport> SummariseAsync(
        ReportQuery query,
        CancellationToken cancellationToken = default)
    {
        var filtered = context.ProcessedFiles.AsNoTracking().AsQueryable();

        if (query.RestrictToClientId is { } clientId)
        {
            filtered = filtered.Where(f => f.ClientId == clientId);
        }

        if (query.FromUtc is { } from)
        {
            filtered = filtered.Where(f => f.ReceivedAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            filtered = filtered.Where(f => f.ReceivedAtUtc <= to);
        }

        // A single grouped projection keeps this to one round trip instead of a dozen scalar queries.
        var totals = await filtered
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalFiles = g.Count(),
                SucceededFiles = g.Count(f => f.Status == ProcessingStatus.Succeeded),
                FilesWithErrors = g.Count(f => f.Status == ProcessingStatus.CompletedWithErrors),
                FailedFiles = g.Count(f => f.Status == ProcessingStatus.Failed),
                TotalBytes = g.Sum(f => f.SizeInBytes),
                TotalRows = g.Sum(f => f.TotalRows),
                ValidRows = g.Sum(f => f.ValidRows),
                InvalidRows = g.Sum(f => f.InvalidRows),
                TotalAmount = g.Sum(f => f.TotalAmount),
                AverageDuration = g.Average(f => (double)f.DurationMilliseconds),
                FirstReceived = g.Min(f => f.ReceivedAtUtc),
                LastReceived = g.Max(f => f.ReceivedAtUtc),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Projected into an anonymous type rather than straight into ClientActivity: EF cannot
        // translate a grouped aggregate into a constructor call, so the record is built once the
        // rows are back. The aggregation itself still happens in the database.
        var byClientRows = await filtered
            .GroupBy(f => f.ClientId)
            .Select(g => new
            {
                ClientId = g.Key,
                FileCount = g.Count(),
                TotalRows = g.Sum(f => f.TotalRows),
                TotalAmount = g.Sum(f => f.TotalAmount),
            })
            .OrderByDescending(row => row.FileCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byClient = byClientRows
            .Select(row => new ClientActivity(row.ClientId, row.FileCount, row.TotalRows, row.TotalAmount))
            .ToArray();

        return new ProcessingSummaryReport
        {
            FromUtc = query.FromUtc,
            ToUtc = query.ToUtc,
            TotalFiles = totals?.TotalFiles ?? 0,
            SucceededFiles = totals?.SucceededFiles ?? 0,
            FilesWithErrors = totals?.FilesWithErrors ?? 0,
            FailedFiles = totals?.FailedFiles ?? 0,
            TotalBytes = totals?.TotalBytes ?? 0,
            TotalRows = totals?.TotalRows ?? 0,
            ValidRows = totals?.ValidRows ?? 0,
            InvalidRows = totals?.InvalidRows ?? 0,
            TotalAmount = totals?.TotalAmount ?? 0m,
            AverageDurationMilliseconds = totals?.AverageDuration ?? 0d,
            FirstReceivedAtUtc = totals?.FirstReceived,
            LastReceivedAtUtc = totals?.LastReceived,
            ByClient = byClient,
        };
    }

    public async Task<Guid?> FindDuplicateAsync(
        string clientId,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var match = await context.ProcessedFiles
            .AsNoTracking()
            .Where(f => f.ClientId == clientId
                        && f.Sha256 == sha256
                        && f.Status != ProcessingStatus.Failed)
            .OrderBy(f => f.ReceivedAtUtc)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return match;
    }

    private static IQueryable<ProcessedFile> Filter(
        IQueryable<ProcessedFile> source,
        ProcessedFileQuery query)
    {
        if (query.RestrictToClientId is { } clientId)
        {
            source = source.Where(f => f.ClientId == clientId);
        }

        if (query.Status is { } status)
        {
            source = source.Where(f => f.Status == status);
        }

        if (query.ReceivedFromUtc is { } from)
        {
            source = source.Where(f => f.ReceivedAtUtc >= from);
        }

        if (query.ReceivedToUtc is { } to)
        {
            source = source.Where(f => f.ReceivedAtUtc <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.FileNameContains))
        {
            // Contains is used rather than a hand-built LIKE pattern so EF parameterises the term
            // and escapes wildcards for us; a caller cannot smuggle % or _ into the predicate.
            // ToLower keeps this translatable on any provider at the cost of the name index, which
            // is acceptable for an optional secondary filter.
            var term = query.FileNameContains.Trim().ToLowerInvariant();
            source = source.Where(f => f.FileName.ToLower().Contains(term));
        }

        return source;
    }
}
