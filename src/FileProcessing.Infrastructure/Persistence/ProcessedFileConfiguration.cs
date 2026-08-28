using FileProcessing.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileProcessing.Infrastructure.Persistence;

/// <summary>
/// Maps the audit aggregate onto PostgreSQL. Column names are spelled out rather than left to a
/// naming convention package, so the schema is readable straight from this file.
/// </summary>
public sealed class ProcessedFileConfiguration : IEntityTypeConfiguration<ProcessedFile>
{
    public void Configure(EntityTypeBuilder<ProcessedFile> builder)
    {
        builder.ToTable("processed_files");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(f => f.FileName).HasColumnName("file_name").HasMaxLength(255).IsRequired();
        builder.Property(f => f.ContentType).HasColumnName("content_type").HasMaxLength(128).IsRequired();
        builder.Property(f => f.ClientId).HasColumnName("client_id").HasMaxLength(64).IsRequired();

        builder.Property(f => f.ReceivedAtUtc).HasColumnName("received_at_utc").IsRequired();
        builder.Property(f => f.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(f => f.DurationMilliseconds).HasColumnName("duration_ms");

        builder.Property(f => f.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(f => f.SizeInBytes).HasColumnName("size_bytes");

        // 64 lower-case hex characters. char(64) keeps the index tight and the length self-documenting.
        builder.Property(f => f.Sha256).HasColumnName("sha256").HasMaxLength(64).IsRequired();

        builder.Property(f => f.TotalRows).HasColumnName("total_rows");
        builder.Property(f => f.ValidRows).HasColumnName("valid_rows");
        builder.Property(f => f.InvalidRows).HasColumnName("invalid_rows");
        builder.Property(f => f.TotalAmount).HasColumnName("total_amount").HasPrecision(18, 2);
        builder.Property(f => f.FailureReason).HasColumnName("failure_reason").HasMaxLength(512);
        builder.Property(f => f.ErrorsTruncated).HasColumnName("errors_truncated");

        // Serves the default listing (a client's own files, newest first) with one index.
        builder.HasIndex(f => new { f.ClientId, f.ReceivedAtUtc }).HasDatabaseName("ix_processed_files_client_received");
        builder.HasIndex(f => f.Status).HasDatabaseName("ix_processed_files_status");
        builder.HasIndex(f => new { f.ClientId, f.Sha256 }).HasDatabaseName("ix_processed_files_client_sha256");

        builder.HasMany(f => f.Errors)
            .WithOne()
            .HasForeignKey("processed_file_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(f => f.Errors)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude(false);
    }
}

/// <summary>Maps the row-level errors captured for a file.</summary>
public sealed class ProcessingErrorConfiguration : IEntityTypeConfiguration<ProcessingError>
{
    public void Configure(EntityTypeBuilder<ProcessingError> builder)
    {
        builder.ToTable("processing_errors");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();

        builder.Property<Guid>("processed_file_id");

        builder.Property(e => e.LineNumber).HasColumnName("line_number");
        builder.Property(e => e.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Message).HasColumnName("message").HasMaxLength(512).IsRequired();
        builder.Property(e => e.Field).HasColumnName("field").HasMaxLength(64);

        builder.HasIndex("processed_file_id").HasDatabaseName("ix_processing_errors_file");
    }
}
