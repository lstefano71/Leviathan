using Leviathan.Core.Csv;

namespace Leviathan.Core.Tests;

public sealed class CsvHeaderDetectorTests
{
    [Fact]
    public void Detect_TextHeaderWithNumericData_ReturnsTrue()
    {
        ReadOnlySpan<byte> sample = "Name,Age,Score\nAlice,30,95.5\nBob,25,87.3\nCharlie,35,92.1\n"u8;

        bool hasHeader = CsvHeaderDetector.Detect(sample, CsvDialect.Csv());

        Assert.True(hasHeader);
    }

    [Fact]
    public void Detect_AllNumericRows_ReturnsFalse()
    {
        ReadOnlySpan<byte> sample = "1,2,3\n4,5,6\n7,8,9\n"u8;

        bool hasHeader = CsvHeaderDetector.Detect(sample, CsvDialect.Csv());

        Assert.False(hasHeader);
    }

    [Fact]
    public void Detect_TextHeaderWithDateData_ReturnsTrue()
    {
        ReadOnlySpan<byte> sample = "Event,Date,Count\nMeeting,2024-01-15,5\nLunch,2024-02-20,3\n"u8;

        bool hasHeader = CsvHeaderDetector.Detect(sample, CsvDialect.Csv());

        Assert.True(hasHeader);
    }

    [Fact]
    public void Detect_AllTextRows_ReturnsFalse()
    {
        ReadOnlySpan<byte> sample = "foo,bar,baz\nhello,world,test\nalpha,beta,gamma\n"u8;

        bool hasHeader = CsvHeaderDetector.Detect(sample, CsvDialect.Csv());

        Assert.False(hasHeader);
    }

    [Fact]
    public void Detect_SingleRow_ReturnsFalse()
    {
        ReadOnlySpan<byte> sample = "Name,Age\n"u8;

        bool hasHeader = CsvHeaderDetector.Detect(sample, CsvDialect.Csv());

        Assert.False(hasHeader);
    }

    [Fact]
    public void Detect_EmptySample_ReturnsFalse()
    {
        bool hasHeader = CsvHeaderDetector.Detect(ReadOnlySpan<byte>.Empty, CsvDialect.Csv());

        Assert.False(hasHeader);
    }

    [Fact]
    public void Detect_MixedTypesWithTextHeader_ReturnsTrue()
    {
        ReadOnlySpan<byte> sample = "ID,Status,Value\n1,true,3.14\n2,false,2.72\n3,true,1.41\n"u8;

        bool hasHeader = CsvHeaderDetector.Detect(sample, CsvDialect.Csv());

        Assert.True(hasHeader);
    }

    [Fact]
    public void Detect_TabSeparatedWithHeader_ReturnsTrue()
    {
        ReadOnlySpan<byte> sample = "Name\tAge\tScore\nAlice\t30\t95\nBob\t25\t87\n"u8;

        bool hasHeader = CsvHeaderDetector.Detect(sample, CsvDialect.Tsv());

        Assert.True(hasHeader);
    }
}
