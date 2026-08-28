using FileProcessing.Core.Abstractions;
using FileProcessing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FileProcessing.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "FileProcessingDb";

    /// <summary>
    /// Registers the PostgreSQL-backed audit store. The connection string is required: failing at
    /// start-up with a clear message beats failing on the first upload with a null reference.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. Set it via " +
                $"ConnectionStrings__{ConnectionStringName} or user secrets.");
        }

        services.AddDbContext<FileProcessingDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
                npgsql.EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(5), errorCodesToAdd: null);
            }));

        services.AddScoped<IProcessedFileRepository, ProcessedFileRepository>();

        return services;
    }
}
