using System.Runtime.CompilerServices;
using System.Text;

namespace FileProcessing.Core.Csv;

/// <summary>
/// Streaming RFC 4180 reader. Handles quoted fields, escaped quotes, embedded delimiters and
/// embedded newlines, and never holds more than one record in memory.
/// </summary>
/// <remarks>
/// Written by hand rather than pulled from a package so the parsing behaviour — and in particular
/// the memory limits — is explicit and testable.
/// </remarks>
public sealed class CsvReader(TextReader reader, CsvReaderOptions? options = null)
{
    private const int BufferSize = 8 * 1024;

    private readonly TextReader _reader = reader;
    private readonly CsvReaderOptions _options = options ?? new CsvReaderOptions();
    private readonly char[] _buffer = new char[BufferSize];

    private int _position;
    private int _length;

    public async IAsyncEnumerable<CsvRow> ReadRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;
        long line = 1;
        var recordStartLine = 1L;

        while (true)
        {
            if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
            {
                if (inQuotes)
                {
                    throw new CsvParseException(
                        "The file ended inside a quoted field; the closing quote is missing.",
                        recordStartLine);
                }

                if (fieldStarted || fields.Count > 0 || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    var last = Complete(fields, recordStartLine);
                    if (last is not null)
                    {
                        yield return last;
                    }
                }

                yield break;
            }

            var c = _buffer[_position++];

            if (inQuotes)
            {
                if (c == '"')
                {
                    var next = await PeekAsync(cancellationToken).ConfigureAwait(false);
                    if (next == '"')
                    {
                        _position++;
                        Append(field, '"', recordStartLine);
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    Append(field, c, recordStartLine);
                }

                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
                continue;
            }

            if (c == _options.Delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                if (fields.Count > _options.MaxFieldsPerRow)
                {
                    throw new CsvParseException(
                        $"A record has more than the permitted {_options.MaxFieldsPerRow} columns.",
                        recordStartLine);
                }

                continue;
            }

            if (c is '\r' or '\n')
            {
                if (c == '\r' && await PeekAsync(cancellationToken).ConfigureAwait(false) == '\n')
                {
                    _position++;
                }

                line++;
                fields.Add(field.ToString());
                field.Clear();
                fieldStarted = false;

                var row = Complete(fields, recordStartLine);
                recordStartLine = line;
                if (row is not null)
                {
                    yield return row;
                }

                continue;
            }

            fieldStarted = true;
            Append(field, c, recordStartLine);
        }
    }

    private CsvRow? Complete(List<string> fields, long recordStartLine)
    {
        if (_options.SkipBlankLines && fields is [""])
        {
            fields.Clear();
            return null;
        }

        var row = new CsvRow(recordStartLine, fields.ToArray());
        fields.Clear();
        return row;
    }

    private void Append(StringBuilder field, char c, long recordStartLine)
    {
        if (field.Length >= _options.MaxFieldLength)
        {
            throw new CsvParseException(
                $"A field exceeds the permitted {_options.MaxFieldLength} characters.",
                recordStartLine);
        }

        field.Append(c);
    }

    private async ValueTask<bool> EnsureBufferedAsync(CancellationToken cancellationToken)
    {
        if (_position < _length)
        {
            return true;
        }

        _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        _position = 0;
        return _length > 0;
    }

    private async ValueTask<int> PeekAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureBufferedAsync(cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        return _buffer[_position];
    }
}
