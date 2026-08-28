using System.Security.Cryptography;

namespace FileProcessing.Core.Io;

/// <summary>
/// Read-only decorator that hashes and counts bytes as they flow through, so a file can be
/// fingerprinted and size-checked in the same pass that parses it — no second read, no buffering
/// the whole upload to compute a digest.
/// </summary>
/// <remarks>
/// The byte cap is enforced here as well as at the transport layer. The transport limit protects
/// the socket; this one protects everything downstream of it, including a stream handed to the
/// processor by some future code path that forgot to check.
/// </remarks>
public sealed class HashingReadStream(Stream inner, long maxBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _digest;
    private bool _disposed;

    /// <summary>Number of bytes read from the inner stream so far.</summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Reads whatever the consumer left behind, so the digest covers the entire file even when
    /// processing stopped early.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8 * 1024];
        while (await ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
            // Discarded on purpose: we only want the bytes to pass through the hash and counter.
        }
    }

    /// <summary>Lower-case hex SHA-256 of everything read so far. Finalises the hash.</summary>
    public string GetHashHex()
    {
        _digest ??= _hash.GetHashAndReset();
        return Convert.ToHexStringLower(_digest);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Track(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(buffer.Span[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Track(buffer.AsSpan(offset, read));
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Track(ReadOnlySpan<byte> read)
    {
        if (read.Length == 0)
        {
            return;
        }

        BytesRead += read.Length;
        if (BytesRead > maxBytes)
        {
            throw new FileTooLargeException(maxBytes);
        }

        _hash.AppendData(read);
    }
}

/// <summary>Raised when an upload exceeds the configured size limit.</summary>
public sealed class FileTooLargeException(long maxBytes)
    : Exception($"The upload exceeds the maximum permitted size of {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
