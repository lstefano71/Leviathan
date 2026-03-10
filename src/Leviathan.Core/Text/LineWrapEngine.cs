namespace Leviathan.Core.Text;

/// <summary>
/// A visual line produced by the line-wrap engine.
/// Describes a contiguous run of bytes from the document that fits on one screen line.
/// </summary>
public readonly struct VisualLine
{
  /// <summary>Byte offset within the document where this visual line starts.</summary>
  public readonly long DocOffset;
  /// <summary>Number of bytes in this visual line.</summary>
  public readonly int ByteLength;
  /// <summary>Number of display columns this visual line occupies.</summary>
  public readonly int ColumnCount;

  public VisualLine(long docOffset, int byteLength, int columnCount)
  {
    DocOffset = docOffset;
    ByteLength = byteLength;
    ColumnCount = columnCount;
  }
}

/// <summary>
/// Just-In-Time line wrapping engine. Computes visual line breaks for a small window
/// of bytes around the viewport, avoiding full-file pre-processing.
/// Works entirely on <see cref="ReadOnlySpan{T}"/> with zero heap allocations in the hot path.
/// </summary>
public sealed class LineWrapEngine
{
  private readonly int _tabWidth;

  public LineWrapEngine(int tabWidth = 4)
  {
    _tabWidth = tabWidth;
  }

  /// <summary>
  /// Computes visual lines from a chunk of document bytes.
  /// <paramref name="data"/> is the raw bytes; <paramref name="baseDocOffset"/> is the
  /// document offset of the first byte in <paramref name="data"/>.
  /// <paramref name="maxColumns"/> is the available character columns on screen.
  /// If <paramref name="wrap"/> is false, lines only break on hard newlines (no wrapping).
  /// Fills <paramref name="output"/> and returns the number of visual lines produced.
  /// </summary>
  public int ComputeVisualLines(
      ReadOnlySpan<byte> data,
      long baseDocOffset,
      int maxColumns,
      bool wrap,
      Span<VisualLine> output)
  {
    if (data.IsEmpty || output.IsEmpty) return 0;
    if (maxColumns < 1) maxColumns = 1;

    int count = 0;
    int pos = 0;

    while (pos < data.Length && count < output.Length) {
      int lineStartPos = pos;
      int colCount = 0;

      while (pos < data.Length) {
        byte b = data[pos];

        // Hard newline — end this visual line
        if (b == 0x0A) {
          pos++; // consume the LF
          break;
        }

        // CR+LF — consume both
        if (b == 0x0D) {
          pos++;
          if (pos < data.Length && data[pos] == 0x0A)
            pos++;
          break;
        }

        // Decode the next rune to measure its width
        var (rune, byteLen) = Utf8Utils.DecodeRune(data, pos);
        if (byteLen == 0) { pos++; break; } // safety

        int runeWidth = Utf8Utils.RuneColumnWidth(rune, _tabWidth);

        // Would this rune push us past the column limit?
        if (wrap && colCount > 0 && colCount + runeWidth > maxColumns)
          break; // soft-wrap: stop before this rune

        colCount += runeWidth;
        pos += byteLen;
      }

      int byteLength = pos - lineStartPos;
      output[count++] = new VisualLine(baseDocOffset + lineStartPos, byteLength, colCount);
    }

    return count;
  }

  /// <summary>
  /// Scans backwards from <paramref name="offset"/> to find the start of the hard line
  /// containing that offset. Reads chunks from the document via <paramref name="readFunc"/>.
  /// Returns the document offset of the line start (after the preceding LF, or 0).
  /// </summary>
  public static long FindLineStart(long offset, long docLength, Func<long, Span<byte>, int> readFunc)
  {
    if (offset <= 0) return 0;

    const int ScanChunkSize = 4096;
    Span<byte> buf = stackalloc byte[ScanChunkSize];

    long search = offset;
    while (search > 0) {
      int chunkLen = (int)Math.Min(search, ScanChunkSize);
      long chunkStart = search - chunkLen;
      int bytesRead = readFunc(chunkStart, buf.Slice(0, chunkLen));
      if (bytesRead == 0) return 0;

      // Scan backwards through this chunk for a newline
      for (int i = bytesRead - 1; i >= 0; i--) {
        if (buf[i] == 0x0A) {
          return chunkStart + i + 1; // line starts after the LF
        }
      }

      search = chunkStart;
    }

    return 0;
  }
}
