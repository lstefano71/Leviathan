using Leviathan.Core.Text;

namespace Leviathan.Core.Tests;

public class LineWrapEngineTests
{
    private readonly LineWrapEngine _engine = new(tabWidth: 4);
    private readonly Utf8TextDecoder _decoder = new();

    [Fact]
    public void NoWrap_SingleLine_ProducesOneLine()
    {
        byte[] data = "Hello World"u8.ToArray();
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output, _decoder);

        Assert.Equal(1, count);
        Assert.Equal(0, output[0].DocOffset);
        Assert.Equal(data.Length, output[0].ByteLength);
    }

    [Fact]
    public void HardNewline_SplitsIntoTwoLines()
    {
        byte[] data = "Hello\nWorld"u8.ToArray();
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output, _decoder);

        Assert.Equal(2, count);
        Assert.Equal(0, output[0].DocOffset);
        Assert.Equal(6, output[0].ByteLength); // "Hello\n"
        Assert.Equal(6, output[1].DocOffset);
        Assert.Equal(5, output[1].ByteLength); // "World"
    }

    [Fact]
    public void CRLF_TreatedAsSingleNewline()
    {
        byte[] data = "AB\r\nCD"u8.ToArray();
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output, _decoder);

        Assert.Equal(2, count);
        Assert.Equal(0, output[0].DocOffset);
        Assert.Equal(4, output[0].ByteLength); // "AB\r\n"
        Assert.Equal(4, output[1].DocOffset);
        Assert.Equal(2, output[1].ByteLength); // "CD"
    }

    [Fact]
    public void SoftWrap_BreaksAtColumnLimit()
    {
        byte[] data = "ABCDEFGHIJ"u8.ToArray(); // 10 columns
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 5, wrap: true, output, _decoder);

        Assert.Equal(2, count);
        Assert.Equal(0, output[0].DocOffset);
        Assert.Equal(5, output[0].ByteLength); // "ABCDE"
        Assert.Equal(5, output[0].ColumnCount);
        Assert.Equal(5, output[1].DocOffset);
        Assert.Equal(5, output[1].ByteLength); // "FGHIJ"
    }

    [Fact]
    public void SoftWrap_WithUTF8_BreaksOnCharBoundary()
    {
        // "AAéBB" → A(1) A(1) é(1col, 2bytes) B(1) B(1) = 5 columns, 6 bytes
        // With maxColumns=3, first line: "AAé" (3 cols, 4 bytes), second: "BB" (2 cols, 2 bytes)
        byte[] data = [0x41, 0x41, 0xC3, 0xA9, 0x42, 0x42];
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 3, wrap: true, output, _decoder);

        Assert.Equal(2, count);
        Assert.Equal(4, output[0].ByteLength); // "AAé" in bytes
        Assert.Equal(3, output[0].ColumnCount);
        Assert.Equal(2, output[1].ByteLength); // "BB"
    }

    [Fact]
    public void SoftWrap_WithBom_IgnoresBomWidth()
    {
        byte[] data = [0xEF, 0xBB, 0xBF, 0x41, 0x42, 0x43, 0x44, 0x45];
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 5, wrap: true, output, _decoder);

        Assert.Equal(1, count);
        Assert.Equal(data.Length, output[0].ByteLength);
        Assert.Equal(5, output[0].ColumnCount);
    }

    [Fact]
    public void NoWrap_IgnoresColumnLimit()
    {
        byte[] data = "ABCDEFGHIJ"u8.ToArray();
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 5, wrap: false, output, _decoder);

        Assert.Equal(1, count);
        Assert.Equal(10, output[0].ByteLength);
    }

    [Fact]
    public void EmptyData_ReturnsZeroLines()
    {
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(ReadOnlySpan<byte>.Empty, 0, 80, true, output, _decoder);
        Assert.Equal(0, count);
    }

    [Fact]
    public void BaseDocOffset_PropagatedToVisualLines()
    {
        byte[] data = "Hello\n"u8.ToArray();
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 1000, 80, false, output, _decoder);

        Assert.Equal(1, count);
        Assert.Equal(1000, output[0].DocOffset);
    }

    [Fact]
    public void OutputCapacity_Limits_Lines()
    {
        byte[] data = "A\nB\nC\nD\nE"u8.ToArray();
        Span<VisualLine> output = stackalloc VisualLine[2]; // only room for 2
        int count = _engine.ComputeVisualLines(data, 0, 80, false, output, _decoder);

        Assert.Equal(2, count);
    }

    [Fact]
    public void MultipleNewlines_ProduceEmptyLines()
    {
        byte[] data = "A\n\nB"u8.ToArray();
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 80, false, output, _decoder);

        Assert.Equal(3, count);
        Assert.Equal(0, output[0].DocOffset);  // "A\n"
        Assert.Equal(2, output[0].ByteLength);
        Assert.Equal(2, output[1].DocOffset);  // "\n" (empty line)
        Assert.Equal(1, output[1].ByteLength);
        Assert.Equal(3, output[2].DocOffset);  // "B"
        Assert.Equal(1, output[2].ByteLength);
    }

    [Fact]
    public void FindLineStart_AtZero_ReturnsZero()
    {
        byte[] data = "Hello\nWorld"u8.ToArray();
        long result = LineWrapEngine.FindLineStart(0, data.Length,
            (off, buf) => { data.AsSpan((int)off, buf.Length).CopyTo(buf); return buf.Length; }, _decoder);
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindLineStart_MiddleOfSecondLine_ReturnsAfterLF()
    {
        byte[] data = "Hello\nWorld"u8.ToArray();
        // offset=8 is in "World" → line starts at 6 (after \n)
        long result = LineWrapEngine.FindLineStart(8, data.Length,
            (off, buf) => {
                int len = (int)Math.Min(buf.Length, data.Length - off);
                data.AsSpan((int)off, len).CopyTo(buf);
                return len;
            }, _decoder);
        Assert.Equal(6, result);
    }

    // ── UTF-16 LE tests ───────────────────────────────────────────────

    private readonly Utf16LeTextDecoder _utf16LeDecoder = new();

    [Fact]
    public void ComputeVisualLines_Utf16Le_BasicAscii_CorrectLines()
    {
        // "Hello\nWorld" in UTF-16 LE
        byte[] data =
        [
          0x48, 0x00, 0x65, 0x00, 0x6C, 0x00, 0x6C, 0x00, 0x6F, 0x00, // Hello
      0x0A, 0x00,                                                     // \n
      0x57, 0x00, 0x6F, 0x00, 0x72, 0x00, 0x6C, 0x00, 0x64, 0x00  // World
        ];
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output, _utf16LeDecoder);

        Assert.Equal(2, count);
        Assert.Equal(0, output[0].DocOffset);
        Assert.Equal(12, output[0].ByteLength);  // "Hello\n" = 6 code units × 2 bytes
        Assert.Equal(12, output[1].DocOffset);
        Assert.Equal(10, output[1].ByteLength);  // "World" = 5 code units × 2 bytes
    }

    [Fact]
    public void ComputeVisualLines_Utf16Le_WrapAtColumnLimit_SplitsCorrectly()
    {
        // 20 ASCII chars "ABCDEFGHIJKLMNOPQRST" in UTF-16 LE = 40 bytes, no newlines
        byte[] data = new byte[40];
        for (int i = 0; i < 20; i++) {
            data[i * 2] = (byte)(0x41 + i); // A..T
            data[i * 2 + 1] = 0x00;
        }

        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 10, wrap: true, output, _utf16LeDecoder);

        Assert.Equal(2, count);
        Assert.Equal(20, output[0].ByteLength); // 10 chars × 2 bytes
        Assert.Equal(10, output[0].ColumnCount);
        Assert.Equal(20, output[1].ByteLength);
        Assert.Equal(10, output[1].ColumnCount);
    }

    [Fact]
    public void FindLineStart_Utf16Le_FindsPreviousNewline()
    {
        // "Line1\nLine2" in UTF-16 LE
        byte[] data =
        [
          0x4C, 0x00, 0x69, 0x00, 0x6E, 0x00, 0x65, 0x00, 0x31, 0x00, // Line1
      0x0A, 0x00,                                                     // \n
      0x4C, 0x00, 0x69, 0x00, 0x6E, 0x00, 0x65, 0x00, 0x32, 0x00  // Line2
        ];
        // offset 16 is inside "Line2"; line start should be 12 (just after the 0A 00 newline)
        long result = LineWrapEngine.FindLineStart(16, data.Length,
            (off, buf) => {
                int len = (int)Math.Min(buf.Length, data.Length - off);
                data.AsSpan((int)off, len).CopyTo(buf);
                return len;
            }, _utf16LeDecoder);
        Assert.Equal(12, result);
    }

    // ── Windows-1252 tests ────────────────────────────────────────────

    private readonly Windows1252TextDecoder _win1252Decoder = new();

    [Fact]
    public void ComputeVisualLines_Win1252_SpecialChars_CorrectColumnWidths()
    {
        // 0x80 (€), 0x93 ("), 0x94 ("), 0x41 (A), 0x0A (\n), 0x42 (B)
        byte[] data = [0x80, 0x93, 0x94, 0x41, 0x0A, 0x42];
        Span<VisualLine> output = stackalloc VisualLine[10];
        int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output, _win1252Decoder);

        Assert.Equal(2, count);
        Assert.Equal(0, output[0].DocOffset);
        Assert.Equal(5, output[0].ByteLength);  // €""A\n = 5 bytes
        Assert.Equal(4, output[0].ColumnCount); // 4 visible columns (€, ", ", A)
        Assert.Equal(5, output[1].DocOffset);
        Assert.Equal(1, output[1].ByteLength);  // B
    }

    [Fact]
    public void FindLineStart_Win1252_FindsPreviousNewline()
    {
        // "abc\ndef" = [0x61, 0x62, 0x63, 0x0A, 0x64, 0x65, 0x66]
        byte[] data = [0x61, 0x62, 0x63, 0x0A, 0x64, 0x65, 0x66];
        // offset 5 is inside "def"; line start should be 4 (byte after \n)
        long result = LineWrapEngine.FindLineStart(5, data.Length,
            (off, buf) => {
                int len = (int)Math.Min(buf.Length, data.Length - off);
                data.AsSpan((int)off, len).CopyTo(buf);
                return len;
            }, _win1252Decoder);
        Assert.Equal(4, result);
    }
}
