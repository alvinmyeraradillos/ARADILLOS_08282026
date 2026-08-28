using System.Security.Cryptography;
using System.Text;
using FileProcessing.Core.Domain;
using FileProcessing.Core.Io;
using FileProcessing.Core.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileProcessing.UnitTests.Processing;

public sealed class FileIngestionServiceTests
{
    private const string ClientId = "dummy-freight";

    private const string ValidCsv = """
        TransactionId,TransactionDate,Description,Amount,Currency,Category
        TXN-1,2026-07-01,Linehaul,100.00,AUD,Linehaul
        TXN-2,2026-07-02,Fuel,50.00,AUD,Fuel
        """;

    [Fact]
    public async Task Records_a_successful_upload_with_its_digest_and_size()
    {
        var repository = new InMemoryProcessedFileRepository();
        var bytes = Encoding.UTF8.GetBytes(ValidCsv);

        var result = await IngestAsync(repository, "july.csv", "text/csv", bytes);

        var success = Assert.IsType<IngestionResult.Success>(result);
        Assert.Equal(ProcessingStatus.Succeeded, success.File.Status);
        Assert.Equal(bytes.Length, success.File.SizeInBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), success.File.Sha256);
        Assert.Equal(2, success.File.TotalRows);
        Assert.Equal(150.00m, success.File.TotalAmount);
        Assert.NotNull(success.File.CompletedAtUtc);

        var tracked = Assert.Single(repository.Files);
        Assert.Equal(ClientId, tracked.ClientId);
        Assert.Equal("july.csv", tracked.FileName);
    }

    [Fact]
    public async Task Writes_the_audit_row_before_processing_so_a_crash_still_leaves_a_trace()
    {
        var repository = new InMemoryProcessedFileRepository();

        await IngestAsync(repository, "july.csv", "text/csv", Encoding.UTF8.GetBytes(ValidCsv));

        // One save to record the attempt, one to close it out.
        Assert.Equal(2, repository.SaveCount);
    }

    [Fact]
    public async Task Rejects_a_file_whose_extension_is_not_allowed()
    {
        var repository = new InMemoryProcessedFileRepository();

        var result = await IngestAsync(repository, "payload.exe", "text/csv", Encoding.UTF8.GetBytes(ValidCsv));

        var rejected = Assert.IsType<IngestionResult.Rejected>(result);
        Assert.Equal("file.unsupported_extension", rejected.Code);

        // A rejected upload is still tracked, so the report shows attempts as well as successes.
        Assert.Equal(ProcessingStatus.Failed, Assert.Single(repository.Files).Status);
    }

    [Fact]
    public async Task Rejects_a_content_type_that_is_not_allowed()
    {
        var repository = new InMemoryProcessedFileRepository();

        var result = await IngestAsync(repository, "july.csv", "application/zip", Encoding.UTF8.GetBytes(ValidCsv));

        Assert.Equal("file.unsupported_content_type", Assert.IsType<IngestionResult.Rejected>(result).Code);
    }

    [Fact]
    public async Task Accepts_a_content_type_carrying_a_charset_parameter()
    {
        var repository = new InMemoryProcessedFileRepository();

        var result = await IngestAsync(
            repository,
            "july.csv",
            "text/csv; charset=utf-8",
            Encoding.UTF8.GetBytes(ValidCsv));

        Assert.IsType<IngestionResult.Success>(result);
    }

    [Fact]
    public async Task Rejects_a_stream_that_exceeds_the_size_limit()
    {
        var repository = new InMemoryProcessedFileRepository();
        var big = Encoding.UTF8.GetBytes(
            "TransactionId,TransactionDate,Description,Amount,Currency,Category\n"
            + string.Join('\n', Enumerable.Range(1, 500).Select(i => $"TXN-{i},2026-07-01,Row,1.00,AUD,Fuel")));

        var result = await IngestAsync(
            repository,
            "big.csv",
            "text/csv",
            big,
            new FileProcessingOptions { MaxFileSizeInBytes = 256 });

        Assert.Equal("file.too_large", Assert.IsType<IngestionResult.Rejected>(result).Code);
        Assert.Equal(ProcessingStatus.Failed, Assert.Single(repository.Files).Status);
    }

    [Fact]
    public async Task Reports_a_file_that_cannot_be_processed_as_failed_but_still_tracks_it()
    {
        var repository = new InMemoryProcessedFileRepository();

        var result = await IngestAsync(
            repository,
            "wrong.csv",
            "text/csv",
            Encoding.UTF8.GetBytes("Id,Date,Notes\n1,2026-07-01,nope"));

        var success = Assert.IsType<IngestionResult.Success>(result);
        Assert.Equal(ProcessingStatus.Failed, success.File.Status);
        Assert.Contains("missing required columns", success.File.FailureReason);

        // The digest is still recorded: knowing which bytes were rejected is the point of the audit.
        Assert.NotEqual(string.Empty, success.File.Sha256);
    }

    [Fact]
    public async Task Detects_a_duplicate_when_that_check_is_enabled()
    {
        var repository = new InMemoryProcessedFileRepository();
        var bytes = Encoding.UTF8.GetBytes(ValidCsv);
        var options = new FileProcessingOptions { RejectDuplicateUploads = true };

        var first = await IngestAsync(repository, "july.csv", "text/csv", bytes, options);
        var second = await IngestAsync(repository, "july-again.csv", "text/csv", bytes, options);

        var original = Assert.IsType<IngestionResult.Success>(first);
        var duplicate = Assert.IsType<IngestionResult.Duplicate>(second);
        Assert.Equal(original.File.Id, duplicate.ExistingFileId);
    }

    [Fact]
    public async Task Allows_a_repeat_upload_when_the_duplicate_check_is_off()
    {
        var repository = new InMemoryProcessedFileRepository();
        var bytes = Encoding.UTF8.GetBytes(ValidCsv);

        await IngestAsync(repository, "july.csv", "text/csv", bytes);
        var second = await IngestAsync(repository, "july.csv", "text/csv", bytes);

        Assert.IsType<IngestionResult.Success>(second);
    }

    [Fact]
    public async Task Another_client_uploading_the_same_bytes_is_not_a_duplicate()
    {
        var repository = new InMemoryProcessedFileRepository();
        var bytes = Encoding.UTF8.GetBytes(ValidCsv);
        var options = new FileProcessingOptions { RejectDuplicateUploads = true };

        await IngestAsync(repository, "july.csv", "text/csv", bytes, options);
        var other = await IngestAsync(repository, "july.csv", "text/csv", bytes, options, clientId: "beta-logistics");

        Assert.IsType<IngestionResult.Success>(other);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\cfg.csv", "cfg.csv")]
    [InlineData("", "upload.csv")]
    [InlineData("   ", "upload.csv")]
    [InlineData("...", "upload.csv")]
    [InlineData("normal name.csv", "normal name.csv")]
    public void Sanitises_the_supplied_file_name(string supplied, string expected) =>
        Assert.Equal(expected, FileIngestionService.SanitiseFileName(supplied));

    [Fact]
    public void Strips_control_characters_from_the_file_name()
    {
        // The name is echoed in API responses and written to logs; a terminal escape sequence in
        // there is a real hazard for whoever tails the log.
        var sanitised = FileIngestionService.SanitiseFileName("re\u001b[31mport.csv");

        Assert.DoesNotContain('\u001b', sanitised);
    }

    private static Task<IngestionResult> IngestAsync(
        InMemoryProcessedFileRepository repository,
        string fileName,
        string contentType,
        byte[] content,
        FileProcessingOptions? options = null,
        string clientId = ClientId)
    {
        options ??= new FileProcessingOptions();

        var processor = new TransactionCsvProcessor(
            Options.Create(options),
            new FixedClock(),
            NullLogger<TransactionCsvProcessor>.Instance);

        var service = new FileIngestionService(
            processor,
            repository,
            Options.Create(options),
            new FixedClock(),
            NullLogger<FileIngestionService>.Instance);

        var upload = new FileUpload(fileName, contentType, new MemoryStream(content));
        return service.IngestAsync(upload, clientId);
    }
}

public sealed class HashingReadStreamTests
{
    [Fact]
    public async Task Hashes_and_counts_everything_that_passes_through()
    {
        var payload = Encoding.UTF8.GetBytes("the quick brown fox");
        await using var stream = new HashingReadStream(new MemoryStream(payload), maxBytes: 1024);

        var buffer = new byte[8];
        while (await stream.ReadAsync(buffer.AsMemory()) > 0)
        {
        }

        Assert.Equal(payload.Length, stream.BytesRead);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), stream.GetHashHex());
    }

    [Fact]
    public async Task Draining_covers_bytes_the_consumer_never_read()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', 500));
        await using var stream = new HashingReadStream(new MemoryStream(payload), maxBytes: 1024);

        // Read a little, then stop, as the processor does when it hits a limit.
        var buffer = new byte[10];
        _ = await stream.ReadAsync(buffer.AsMemory());
        await stream.DrainAsync();

        Assert.Equal(payload.Length, stream.BytesRead);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), stream.GetHashHex());
    }

    [Fact]
    public async Task Throws_once_the_byte_cap_is_passed()
    {
        var payload = new byte[300];
        await using var stream = new HashingReadStream(new MemoryStream(payload), maxBytes: 128);

        await Assert.ThrowsAsync<FileTooLargeException>(async () => await stream.DrainAsync());
    }
}
