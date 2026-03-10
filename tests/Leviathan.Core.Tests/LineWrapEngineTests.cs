using Leviathan.Core.Text;

namespace Leviathan.Core.Tests;

public class LineWrapEngineTests
{
  private readonly LineWrapEngine _engine = new(tabWidth: 4);

  [Fact]
  public void NoWrap_SingleLine_ProducesOneLine()
  {
    byte[] data = "Hello World"u8.ToArray();
    Span<VisualLine> output = stackalloc VisualLine[10];
    int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output);

    Assert.Equal(1, count);
    Assert.Equal(0, output[0].DocOffset);
    Assert.Equal(data.Length, output[0].ByteLength);
  }

  [Fact]
  public void HardNewline_SplitsIntoTwoLines()
  {
    byte[] data = "Hello\nWorld"u8.ToArray();
    Span<VisualLine> output = stackalloc VisualLine[10];
    int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output);

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
    int count = _engine.ComputeVisualLines(data, 0, 80, wrap: false, output);

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
    int count = _engine.ComputeVisualLines(data, 0, 5, wrap: true, output);

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
    int count = _engine.ComputeVisualLines(data, 0, 3, wrap: true, output);

    Assert.Equal(2, count);
    Assert.Equal(4, output[0].ByteLength); // "AAé" in bytes
    Assert.Equal(3, output[0].ColumnCount);
    Assert.Equal(2, output[1].ByteLength); // "BB"
  }

  [Fact]
  public void NoWrap_IgnoresColumnLimit()
  {
    byte[] data = "ABCDEFGHIJ"u8.ToArray();
    Span<VisualLine> output = stackalloc VisualLine[10];
    int count = _engine.ComputeVisualLines(data, 0, 5, wrap: false, output);

    Assert.Equal(1, count);
    Assert.Equal(10, output[0].ByteLength);
  }

  [Fact]
  public void EmptyData_ReturnsZeroLines()
  {
    Span<VisualLine> output = stackalloc VisualLine[10];
    int count = _engine.ComputeVisualLines(ReadOnlySpan<byte>.Empty, 0, 80, true, output);
    Assert.Equal(0, count);
  }

  [Fact]
  public void BaseDocOffset_PropagatedToVisualLines()
  {
    byte[] data = "Hello\n"u8.ToArray();
    Span<VisualLine> output = stackalloc VisualLine[10];
    int count = _engine.ComputeVisualLines(data, 1000, 80, false, output);

    Assert.Equal(1, count);
    Assert.Equal(1000, output[0].DocOffset);
  }

  [Fact]
  public void OutputCapacity_Limits_Lines()
  {
    byte[] data = "A\nB\nC\nD\nE"u8.ToArray();
    Span<VisualLine> output = stackalloc VisualLine[2]; // only room for 2
    int count = _engine.ComputeVisualLines(data, 0, 80, false, output);

    Assert.Equal(2, count);
  }

  [Fact]
  public void MultipleNewlines_ProduceEmptyLines()
  {
    byte[] data = "A\n\nB"u8.ToArray();
    Span<VisualLine> output = stackalloc VisualLine[10];
    int count = _engine.ComputeVisualLines(data, 0, 80, false, output);

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
        (off, buf) => { data.AsSpan((int)off, buf.Length).CopyTo(buf); return buf.Length; });
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
        });
    Assert.Equal(6, result);
  }
}
