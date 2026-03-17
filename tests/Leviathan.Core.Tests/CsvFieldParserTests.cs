using Leviathan.Core.Csv;

namespace Leviathan.Core.Tests;

public sealed class CsvFieldParserTests
{
    [Fact]
    public void ParseRecord_SimpleUnquoted_ReturnsCorrectFields()
    {
        ReadOnlySpan<byte> record = "a,b,c"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(3, count);
        Assert.Equal(0, fields[0].Offset);
        Assert.Equal(1, fields[0].Length);
        Assert.False(fields[0].IsQuoted);
        Assert.Equal(2, fields[1].Offset);
        Assert.Equal(1, fields[1].Length);
        Assert.Equal(4, fields[2].Offset);
        Assert.Equal(1, fields[2].Length);
    }

    [Fact]
    public void ParseRecord_QuotedField_MarkedAsQuoted()
    {
        ReadOnlySpan<byte> record = "a,\"hello\",c"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(3, count);
        Assert.True(fields[1].IsQuoted);
    }

    [Fact]
    public void ParseRecord_QuotedFieldWithEscapedQuote_CorrectBoundaries()
    {
        // "he""llo" → he"llo
        ReadOnlySpan<byte> record = "\"he\"\"llo\""u8;
        Span<CsvField> fields = stackalloc CsvField[4];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(1, count);
        Assert.True(fields[0].IsQuoted);
    }

    [Fact]
    public void ParseRecord_QuotedFieldWithEmbeddedSeparator_SingleField()
    {
        // "a,b" should be one field, not two
        ReadOnlySpan<byte> record = "x,\"a,b\",y"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(3, count);
        Assert.True(fields[1].IsQuoted);
    }

    [Fact]
    public void ParseRecord_QuotedFieldWithEmbeddedNewline_SingleField()
    {
        // "line1\nline2" is one field
        ReadOnlySpan<byte> record = "x,\"line1\nline2\",y"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(3, count);
        Assert.True(fields[1].IsQuoted);
    }

    [Fact]
    public void ParseRecord_EmptyFields_ReturnsCorrectCount()
    {
        // ",," → 3 empty fields
        ReadOnlySpan<byte> record = ",,"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(3, count);
        Assert.Equal(0, fields[0].Length);
        Assert.Equal(0, fields[1].Length);
        Assert.Equal(0, fields[2].Length);
    }

    [Fact]
    public void ParseRecord_TrailingSeparator_ExtraEmptyField()
    {
        ReadOnlySpan<byte> record = "a,b,"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(3, count);
        Assert.Equal(0, fields[2].Length);
    }

    [Fact]
    public void ParseRecord_EmptyRecord_ReturnsOneEmptyField()
    {
        ReadOnlySpan<byte> record = [];
        Span<CsvField> fields = stackalloc CsvField[4];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(1, count);
        Assert.Equal(0, fields[0].Length);
    }

    [Fact]
    public void ParseRecord_TabSeparator_CorrectSplit()
    {
        ReadOnlySpan<byte> record = "a\tb\tc"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Tsv(), fields);

        Assert.Equal(3, count);
    }

    [Fact]
    public void ParseRecord_PipeSeparator_CorrectSplit()
    {
        ReadOnlySpan<byte> record = "a|b|c"u8;
        Span<CsvField> fields = stackalloc CsvField[8];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Pipe(), fields);

        Assert.Equal(3, count);
    }

    [Fact]
    public void ParseRecord_BufferTooSmall_ReturnsPartialCount()
    {
        ReadOnlySpan<byte> record = "a,b,c,d,e"u8;
        Span<CsvField> fields = stackalloc CsvField[2];

        int count = CsvFieldParser.ParseRecord(record, CsvDialect.Csv(), fields);

        Assert.Equal(2, count);
    }

    [Fact]
    public void UnescapeField_Unquoted_CopiesDirectly()
    {
        ReadOnlySpan<byte> record = "hello,world"u8;
        CsvField field = new(0, 5, false);
        Span<byte> dest = stackalloc byte[16];

        int written = CsvFieldParser.UnescapeField(record, field, CsvDialect.Csv(), dest);

        Assert.Equal(5, written);
        Assert.True(dest[..5].SequenceEqual("hello"u8));
    }

    [Fact]
    public void UnescapeField_QuotedWithDoubledQuotes_Unescapes()
    {
        // Record: "he""llo"
        ReadOnlySpan<byte> record = "\"he\"\"llo\""u8;
        CsvField field = new(0, record.Length, true);
        Span<byte> dest = stackalloc byte[16];

        int written = CsvFieldParser.UnescapeField(record, field, CsvDialect.Csv(), dest);

        Assert.Equal(6, written);
        Assert.True(dest[..6].SequenceEqual("he\"llo"u8));
    }

    [Fact]
    public void UnescapeField_UnquotedDestinationTooSmall_TruncatesWithoutThrowing()
    {
        ReadOnlySpan<byte> record = "abcdefghijklmnopqrstuvwxyz"u8;
        CsvField field = new(0, record.Length, false);
        Span<byte> dest = stackalloc byte[8];

        int written = CsvFieldParser.UnescapeField(record, field, CsvDialect.Csv(), dest);

        Assert.Equal(8, written);
        Assert.True(dest.SequenceEqual("abcdefgh"u8));
    }

    [Fact]
    public void UnescapeField_QuotedDestinationTooSmall_TruncatesWithoutThrowing()
    {
        ReadOnlySpan<byte> record = "\"ab\"\"cd\"\"ef\""u8;
        CsvField field = new(0, record.Length, true);
        Span<byte> dest = stackalloc byte[6];

        int written = CsvFieldParser.UnescapeField(record, field, CsvDialect.Csv(), dest);

        Assert.Equal(6, written);
        Assert.True(dest.SequenceEqual("ab\"cd\""u8));
    }
}
