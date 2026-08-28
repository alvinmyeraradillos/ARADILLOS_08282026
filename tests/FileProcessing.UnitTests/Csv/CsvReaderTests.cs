using FileProcessing.Core.Csv;

namespace FileProcessing.UnitTests.Csv;

/// <summary>
/// The parser is hand-written, so the RFC 4180 edge cases are pinned down here rather than
/// assumed: quoting is where CSV readers usually go wrong, and a reader that silently mangles a
/// quoted field would corrupt data without ever raising an error.
/// </summary>
public sealed class CsvReaderTests
{
    [Fact]
    public async Task Reads_simple_rows()
    {
        var rows = await ReadAsync("a,b,c\n1,2,3\n4,5,6\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0].Fields);
        Assert.Equal(["4", "5", "6"], rows[2].Fields);
    }

    [Fact]
    public async Task Handles_crlf_and_a_missing_final_newline()
    {
        var rows = await ReadAsync("a,b\r\n1,2\r\n3,4");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["3", "4"], rows[2].Fields);
    }

    [Fact]
    public async Task Keeps_delimiters_inside_quoted_fields()
    {
        var rows = await ReadAsync("id,description\n1,\"Fuel levy, July\"\n");

        Assert.Equal("Fuel levy, July", rows[1].Fields[1]);
    }

    [Fact]
    public async Task Unescapes_doubled_quotes()
    {
        var rows = await ReadAsync("id,description\n1,\"Customer credit \"\"goodwill\"\"\"\n");

        Assert.Equal("Customer credit \"goodwill\"", rows[1].Fields[1]);
    }

    [Fact]
    public async Task Keeps_newlines_inside_quoted_fields_and_still_tracks_line_numbers()
    {
        var rows = await ReadAsync("id,description\n1,\"line one\nline two\"\n2,after\n");

        Assert.Equal("line one\nline two", rows[1].Fields[1]);

        // The record after a multi-line field must report the physical line it starts on,
        // otherwise every error reported past that point points at the wrong row.
        Assert.Equal(4, rows[2].LineNumber);
    }

    [Fact]
    public async Task Preserves_empty_fields()
    {
        var rows = await ReadAsync("a,b,c\n1,,3\n");

        Assert.Equal(["1", "", "3"], rows[1].Fields);
    }

    [Fact]
    public async Task Skips_blank_lines()
    {
        var rows = await ReadAsync("a,b\n\n1,2\n\n");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Rejects_an_unterminated_quoted_field()
    {
        var exception = await Assert.ThrowsAsync<CsvParseException>(
            () => ReadAsync("id,description\n1,\"never closed\n"));

        Assert.Equal(2, exception.LineNumber);
    }

    [Fact]
    public async Task Rejects_a_field_longer_than_the_configured_limit()
    {
        var oversized = new string('x', 64);
        var options = new CsvReaderOptions { MaxFieldLength = 16 };

        await Assert.ThrowsAsync<CsvParseException>(() => ReadAsync($"a\n{oversized}\n", options));
    }

    [Fact]
    public async Task Rejects_a_row_with_too_many_columns()
    {
        var options = new CsvReaderOptions { MaxFieldsPerRow = 3 };

        await Assert.ThrowsAsync<CsvParseException>(() => ReadAsync("a,b,c,d,e\n", options));
    }

    [Fact]
    public async Task Returns_nothing_for_an_empty_stream()
    {
        var rows = await ReadAsync(string.Empty);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Reads_a_record_that_spans_the_internal_buffer()
    {
        // The buffer is 8 KiB; a field either side of that boundary exercises the refill path,
        // which is the easiest place for a hand-written parser to drop or duplicate a character.
        var wide = new string('y', 9_000);
        var rows = await ReadAsync($"a,b\n{wide},z\n", new CsvReaderOptions { MaxFieldLength = 16_000 });

        Assert.Equal(wide, rows[1].Fields[0]);
        Assert.Equal("z", rows[1].Fields[1]);
    }

    private static async Task<List<CsvRow>> ReadAsync(string content, CsvReaderOptions? options = null)
    {
        var reader = new CsvReader(new StringReader(content), options);
        var rows = new List<CsvRow>();

        await foreach (var row in reader.ReadRecordsAsync())
        {
            rows.Add(row);
        }

        return rows;
    }
}
