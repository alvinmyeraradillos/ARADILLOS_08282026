using FileProcessing.Core.Abstractions;
using FileProcessing.Core.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FileProcessing.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the format-agnostic processing pipeline. Options are validated at start-up so a
    /// bad limit in configuration fails the deployment rather than the first upload.
    /// </summary>
    public static IServiceCollection AddFileProcessingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<FileProcessingOptions>()
            .Bind(configuration.GetSection(FileProcessingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IFileProcessor, TransactionCsvProcessor>();
        services.AddScoped<FileIngestionService>();

        return services;
    }
}
