using Leviathan.Core.Csv;

namespace Leviathan.Core.Tests;

public sealed class CsvDialectDetectorTests
{
    [Fact]
    public void Detect_StandardComma_DetectsComma()
    {
        ReadOnlySpan<byte> sample = "a,b,c\n1,2,3\n4,5,6\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        Assert.Equal((byte)',', dialect.Separator);
        Assert.Equal((byte)'"', dialect.Quote);
    }

    [Fact]
    public void Detect_TabSeparated_DetectsTab()
    {
        ReadOnlySpan<byte> sample = "a\tb\tc\n1\t2\t3\n4\t5\t6\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        Assert.Equal((byte)'\t', dialect.Separator);
    }

    [Fact]
    public void Detect_PipeSeparated_DetectsPipe()
    {
        ReadOnlySpan<byte> sample = "a|b|c\n1|2|3\n4|5|6\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        Assert.Equal((byte)'|', dialect.Separator);
    }

    [Fact]
    public void Detect_SemicolonSeparated_DetectsSemicolon()
    {
        ReadOnlySpan<byte> sample = "a;b;c\n1;2;3\n4;5;6\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        Assert.Equal((byte)';', dialect.Separator);
    }

    [Fact]
    public void Detect_QuotedFieldsWithEmbeddedCommas_CorrectDelimiter()
    {
        // The comma inside quotes should not confuse the detector
        ReadOnlySpan<byte> sample = "name,city,state\n\"Smith, John\",\"New York\",NY\n\"Doe, Jane\",\"Los Angeles\",CA\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        Assert.Equal((byte)',', dialect.Separator);
    }

    [Fact]
    public void Detect_QuotedFieldsWithEmbeddedNewlines_CorrectColumnCount()
    {
        // Newline inside quotes should not confuse the detector
        ReadOnlySpan<byte> sample = "id,text\n1,\"line1\nline2\"\n2,\"hello\"\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        Assert.Equal((byte)',', dialect.Separator);
    }

    [Fact]
    public void Detect_SingleColumnFile_ReturnsDefaultComma()
    {
        ReadOnlySpan<byte> sample = "hello\nworld\nfoo\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        // No signal → defaults to comma
        Assert.Equal((byte)',', dialect.Separator);
    }

    [Fact]
    public void Detect_EmptyFile_ReturnsDefaultDialect()
    {
        CsvDialect dialect = CsvDialectDetector.Detect([]);

        Assert.Equal((byte)',', dialect.Separator);
        Assert.Equal((byte)'"', dialect.Quote);
    }

    [Fact]
    public void Detect_MixedConsistency_PicksMostConsistent()
    {
        // 5 rows with tab, all consistent at 3 columns
        // Comma would only match 2 columns in some rows
        ReadOnlySpan<byte> sample = "a\tb\tc\n1\t2\t3\n4\t5\t6\n7\t8\t9\n10\t11\t12\n"u8;

        CsvDialect dialect = CsvDialectDetector.Detect(sample);

        Assert.Equal((byte)'\t', dialect.Separator);
    }
}
