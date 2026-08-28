using FileProcessing.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FileProcessing.Infrastructure.Persistence;

/// <summary>
/// The processing audit store. Deliberately small: this service tracks what it processed, it is
/// not the system of record for the transactions themselves.
/// </summary>
public sealed class FileProcessingDbContext(DbContextOptions<FileProcessingDbContext> options)
    : DbContext(options)
{
    public DbSet<ProcessedFile> ProcessedFiles => Set<ProcessedFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FileProcessingDbContext).Assembly);
    }
}
