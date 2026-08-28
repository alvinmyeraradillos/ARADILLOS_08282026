using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FileProcessing.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the web host. The connection string is
/// only used to pick the provider's SQL dialect, so migrations can be generated with no database
/// running; set <c>FILEPROCESSING_DESIGN_CONNECTION</c> to point at a real one when scaffolding
/// from an existing schema.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FileProcessingDbContext>
{
    public FileProcessingDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("FILEPROCESSING_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=fileprocessing;Username=fileprocessing;Password=design-time-only";

        var options = new DbContextOptionsBuilder<FileProcessingDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .Options;

        return new FileProcessingDbContext(options);
    }
}
