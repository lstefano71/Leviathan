using Hexa.NET.ImGui;

using Leviathan.Core;
using Leviathan.Core.Text;

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Leviathan.UI;

/// <summary>
/// Renders a text editor view using ImGui immediate-mode drawing.
/// Features UTF-8 decoding via <see cref="System.Text.Rune"/>,
/// JIT line wrapping for the visible viewport, and zero-allocation rendering.
/// </summary>
public sealed class TextView
{
  private readonly Document _document;
  private readonly LineWrapEngine _wrapEngine;

  private long _topDocOffset;         // document byte offset at top of viewport
  private int _visibleRows;
  private bool _wordWrap = true;
  private int _tabWidth = 4;

  // Scrollbar / navigation
  private long _scrollTargetLine = -1; // logical line to scroll to next frame
  private long _estimatedTotalLines;   // from LineIndex or heuristic

  // Pre-allocated buffers
  private byte[] _readBuffer = new byte[128 * 1024]; // 128 KB chunk window
  private VisualLine[] _visualLines = new VisualLine[2048];
  private byte[] _countBuffer = new byte[64 * 1024]; // for newline counting

  // Cached line number at the current top offset
  private long _cachedTopOffset = -1;
  private long _cachedTopLineNumber = 1;

  // Maximum virtual height for scroll mapping (same strategy as HexView)
  private const float MaxVirtualHeight = 10_000_000f;

  // Goto line dialog state
  private bool _showGotoDialog;
  private readonly byte[] _gotoInputBuf = new byte[20];
  private bool _gotoFocusInput;

  /// <summary>Current first visible byte offset.</summary>
  public long TopDocOffset => _topDocOffset;

  /// <summary>Word wrap on/off.</summary>
  public bool WordWrap {
    get => _wordWrap;
    set => _wordWrap = value;
  }

  /// <summary>Tab display width in columns.</summary>
  public int TabWidth {
    get => _tabWidth;
    set { _tabWidth = Math.Max(1, value); }
  }

  public TextView(Document document)
  {
    _document = document;
    _wrapEngine = new LineWrapEngine(_tabWidth);
    // Rough heuristic: assume ~80 bytes per line for initial estimate
    _estimatedTotalLines = Math.Max(1, _document.Length / 80);
  }

  /// <summary>
  /// Provides the line index total count from the background scanner.
  /// Call this each frame if a LineIndex is available.
  /// </summary>
  public void UpdateLineEstimate(long totalHardLines)
  {
    if (totalHardLines > 0)
      _estimatedTotalLines = totalHardLines;
  }

  /// <summary>
  /// Scrolls to the given byte offset.
  /// </summary>
  public void ScrollToOffset(long offset)
  {
    _topDocOffset = Math.Clamp(offset, 0, Math.Max(0, _document.Length - 1));
    // Snap to line start
    _topDocOffset = LineWrapEngine.FindLineStart(
        _topDocOffset, _document.Length,
        (off, buf) => _document.Read(off, buf));
    _scrollTargetLine = -1; // will recalc from offset
  }

  /// <summary>
  /// Opens the Goto Line dialog.
  /// </summary>
  public void OpenGotoDialog()
  {
    _showGotoDialog = true;
    Array.Clear(_gotoInputBuf);
    _gotoFocusInput = true;
  }

  /// <summary>
  /// Renders the text view. Must be called between ImGui.Begin/End.
  /// </summary>
  public unsafe void Render()
  {
    float lineHeight = ImGui.GetTextLineHeightWithSpacing();
    float charWidth = ImGui.CalcTextSize("M"u8).X;
    var outerAvail = ImGui.GetContentRegionAvail();

    // Gutter sizing: 8 digit positions + 1 space separator = 9 chars
    float gutterWidth = 9 * charWidth;

    // ── Estimated total visual lines for scrollbar ──
    long totalLines = _estimatedTotalLines;
    if (totalLines < 1) totalLines = 1;

    float idealHeight = totalLines * lineHeight;
    float virtualHeight = Math.Min(idealHeight, MaxVirtualHeight);

    // ── Begin scrollable child window ──
    ImGui.BeginChild("##TextScroll"u8, outerAvail, ImGuiChildFlags.None,
        ImGuiWindowFlags.NoMove);

    // Compute maxColumns *inside* the child window so the scrollbar width is excluded
    var innerAvail = ImGui.GetContentRegionAvail();
    float textAreaWidth = innerAvail.X - gutterWidth;
    int maxColumns = Math.Max(1, (int)(textAreaWidth / charWidth));
    _visibleRows = Math.Max(1, (int)(innerAvail.Y / lineHeight));
    long maxFirstLine = Math.Max(0, totalLines - _visibleRows);
    float maxScrollY = virtualHeight - _visibleRows * lineHeight;
    if (maxScrollY < 1f) maxScrollY = 1f;

    // ── Handle keyboard input ──
    HandleScrollInput(totalLines, maxFirstLine);

    // ── Apply programmatic scroll ──
    if (_scrollTargetLine >= 0) {
      float targetScrollY = LineToScrollY(_scrollTargetLine, totalLines, maxFirstLine, idealHeight, maxScrollY, lineHeight);
      ImGui.SetScrollY(targetScrollY);
      _scrollTargetLine = -1;
    }

    // ── Derive top offset from scroll position ──
    float scrollY = ImGui.GetScrollY();
    long currentLine = ScrollYToLine(scrollY, totalLines, maxFirstLine, idealHeight, maxScrollY, lineHeight);
    currentLine = Math.Clamp(currentLine, 0, maxFirstLine);

    // Convert approximate line number to byte offset using heuristic
    // (for large files with a LineIndex, this would use sparse lookup)
    long approxOffset = _document.Length > 0
        ? (long)((double)currentLine / totalLines * _document.Length)
        : 0;
    approxOffset = Math.Clamp(approxOffset, 0, Math.Max(0, _document.Length - 1));

    // Snap to a hard-line boundary
    if (_document.Length > 0) {
      _topDocOffset = LineWrapEngine.FindLineStart(
          approxOffset, _document.Length,
          (off, buf) => _document.Read(off, buf));
    } else {
      _topDocOffset = 0;
    }

    // ── Read a chunk around the viewport ──
    // Read enough bytes to cover the visible rows + some padding
    int chunkSize = Math.Max((_visibleRows + 4) * 256, 16 * 1024);
    chunkSize = Math.Min(chunkSize, _readBuffer.Length);

    long remaining = _document.Length - _topDocOffset;
    int bytesToRead = (int)Math.Min(chunkSize, remaining > 0 ? remaining : 0);

    if (bytesToRead <= 0 && _document.Length == 0) {
      ImGui.TextUnformatted("(empty file)"u8);
      ImGui.SetCursorPosY(virtualHeight);
      ImGui.Dummy(new Vector2(0, 0));
      ImGui.EndChild();
      RenderGotoDialog();
      return;
    }

    int bytesRead = bytesToRead > 0
        ? _document.Read(_topDocOffset, _readBuffer.AsSpan(0, bytesToRead))
        : 0;

    // ── UTF-8 boundary alignment ──
    // If we started mid-sequence, the first few bytes may be continuation bytes.
    // AlignToCharBoundary ensures we start at a valid character.
    int startAdj = 0;
    if (_topDocOffset > 0 && bytesRead > 0) {
      startAdj = Utf8Utils.AlignToCharBoundary(_readBuffer.AsSpan(0, bytesRead), 0);
    }

    // ── Compute visual lines ──
    var dataSpan = _readBuffer.AsSpan(startAdj, bytesRead - startAdj);
    if (_visualLines.Length < _visibleRows + 4)
      _visualLines = new VisualLine[(_visibleRows + 4) * 2];

    int lineCount = _wrapEngine.ComputeVisualLines(
        dataSpan,
        _topDocOffset + startAdj,
        maxColumns,
        _wordWrap,
        _visualLines.AsSpan());

    // ── Render ──
    ImGui.SetCursorPosY(scrollY);
    var drawList = ImGui.GetWindowDrawList();
    Vector2 cursorStart = ImGui.GetCursorScreenPos();

    // Colors
    uint colText = ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.9f, 1.0f));
    uint colLineNum = ImGui.GetColorU32(new Vector4(0.5f, 0.6f, 0.7f, 0.8f));
    uint colControl = ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1.0f));
    uint colWrapIndicator = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.7f, 0.5f));

    float textStartX = cursorStart.X + gutterWidth;

    // Formatting buffers (stack-allocated)
    Span<byte> lineNumBuf = stackalloc byte[10]; // 8 digits + space + null
    // Render buffer for a single line of UTF-8 text
    Span<byte> renderBuf = stackalloc byte[1024];

    // Get the actual line number at the top of the viewport
    long hardLineNumber = GetLineNumberAtOffset(_topDocOffset) - 1; // -1 because first hard line increments it

    int linesRendered = 0;
    for (int i = 0; i < lineCount && linesRendered < _visibleRows; i++) {
      ref readonly VisualLine vl = ref _visualLines[i];
      float y = cursorStart.Y + linesRendered * lineHeight;

      // ── Line number gutter ──
      // Only show line number for the first visual line of a hard line
      bool isHardLineStart = (i == 0) ||
          (vl.DocOffset > 0 && IsAfterNewline(vl.DocOffset));

      if (isHardLineStart) {
        hardLineNumber++;
        FormatLineNumber(hardLineNumber, lineNumBuf);
        fixed (byte* pNum = lineNumBuf) {
          drawList.AddText(new Vector2(cursorStart.X, y), colLineNum, pNum);
        }
      } else {
        // Wrap continuation indicator
        drawList.AddText(new Vector2(cursorStart.X + gutterWidth - 2 * charWidth, y),
            colWrapIndicator, WrapIndicatorPtr);
      }

      // ── Text content ──
      // Read the visual line's bytes from the buffer
      int vlStartInBuf = (int)(vl.DocOffset - _topDocOffset) - startAdj + startAdj; // net offset in _readBuffer
      int vlBufStart = (int)(vl.DocOffset - _topDocOffset);

      if (vlBufStart >= 0 && vlBufStart + vl.ByteLength <= bytesRead) {
        int renderLen = RenderLineToBuffer(
            _readBuffer.AsSpan(vlBufStart, vl.ByteLength),
            renderBuf,
            _tabWidth);

        if (renderLen > 0) {
          fixed (byte* pText = renderBuf) {
            drawList.AddText(new Vector2(textStartX, y), colText, pText, pText + renderLen);
          }
        }
      }

      linesRendered++;
    }

    // ── Set virtual content height for scrollbar ──
    ImGui.SetCursorPosY(virtualHeight);
    ImGui.Dummy(new Vector2(0, 0));
    ImGui.EndChild();

    // ── Goto dialog ──
    RenderGotoDialog();
  }

  /// <summary>
  /// Checks whether the byte just before <paramref name="docOffset"/> was a newline.
  /// Uses the read buffer if possible.
  /// </summary>
  private bool IsAfterNewline(long docOffset)
  {
    if (docOffset <= 0) return true;

    long bufStart = _topDocOffset;
    long posInBuf = docOffset - 1 - bufStart;
    if (posInBuf >= 0 && posInBuf < _readBuffer.Length) {
      byte b = _readBuffer[posInBuf];
      return b == 0x0A;
    }

    // Fallback: read single byte from document
    Span<byte> tmp = stackalloc byte[1];
    int read = _document.Read(docOffset - 1, tmp);
    return read > 0 && tmp[0] == 0x0A;
  }

  /// <summary>
  /// Renders a single visual line's UTF-8 bytes into a null-terminated display buffer,
  /// replacing control characters and expanding tabs.
  /// Returns the number of bytes written (excluding the null terminator).
  /// </summary>
  private static int RenderLineToBuffer(ReadOnlySpan<byte> lineData, Span<byte> output, int tabWidth)
  {
    int outPos = 0;
    int pos = 0;
    int maxOut = output.Length - 1; // leave room for null terminator

    while (pos < lineData.Length && outPos < maxOut) {
      byte b = lineData[pos];

      // Strip CR/LF from rendering
      if (b == 0x0A || b == 0x0D) {
        pos++;
        continue;
      }

      // Tab expansion
      if (b == 0x09) {
        int spaces = Math.Min(tabWidth, maxOut - outPos);
        for (int s = 0; s < spaces; s++)
          output[outPos++] = (byte)' ';
        pos++;
        continue;
      }

      // Control characters → dot
      if (b < 0x20) {
        output[outPos++] = (byte)'.';
        pos++;
        continue;
      }

      // Regular UTF-8: copy the byte sequence as-is (ImGui handles UTF-8)
      var (rune, byteLen) = Utf8Utils.DecodeRune(lineData, pos);
      if (byteLen == 0) { pos++; continue; }

      if (outPos + byteLen > maxOut) break; // not enough space

      lineData.Slice(pos, byteLen).CopyTo(output.Slice(outPos));
      outPos += byteLen;
      pos += byteLen;
    }

    // Null-terminate
    if (outPos < output.Length)
      output[outPos] = 0;

    return outPos;
  }

  private void HandleScrollInput(long totalLines, long maxFirstLine)
  {
    if (!ImGui.IsWindowFocused()) return;

    var io = ImGui.GetIO();

    if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true))
      ScrollByLines(1);
    if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true))
      ScrollByLines(-1);
    if (ImGui.IsKeyPressed(ImGuiKey.PageDown, true))
      ScrollByLines(_visibleRows);
    if (ImGui.IsKeyPressed(ImGuiKey.PageUp, true))
      ScrollByLines(-_visibleRows);

    if (ImGui.IsKeyPressed(ImGuiKey.Home) && io.KeyCtrl) {
      _topDocOffset = 0;
      _scrollTargetLine = 0;
    }
    if (ImGui.IsKeyPressed(ImGuiKey.End) && io.KeyCtrl) {
      // Go to near end of file
      long endOffset = Math.Max(0, _document.Length - 1);
      _topDocOffset = LineWrapEngine.FindLineStart(
          endOffset, _document.Length,
          (off, buf) => _document.Read(off, buf));
      _scrollTargetLine = maxFirstLine;
    }

    // Ctrl+G = Goto line
    if (ImGui.IsKeyPressed(ImGuiKey.G) && io.KeyCtrl)
      OpenGotoDialog();
  }

  /// <summary>
  /// Scrolls by the given number of visual lines (positive = down, negative = up).
  /// </summary>
  private void ScrollByLines(int delta)
  {
    if (delta == 0) return;

    if (delta > 0) {
      // Scroll down: advance past 'delta' newlines/wraps
      long offset = _topDocOffset;
      Span<byte> buf = stackalloc byte[512];
      for (int i = 0; i < delta && offset < _document.Length; i++) {
        int read = _document.Read(offset, buf);
        if (read == 0) break;
        // Find next newline
        int nlPos = buf.Slice(0, read).IndexOf((byte)0x0A);
        if (nlPos >= 0)
          offset += nlPos + 1;
        else
          offset += read;
      }
      _topDocOffset = Math.Min(offset, Math.Max(0, _document.Length - 1));
    } else {
      // Scroll up: go back 'delta' lines
      long offset = _topDocOffset;
      for (int i = 0; i < -delta && offset > 0; i++) {
        // Go to previous line start: step back past the preceding LF
        offset = Math.Max(0, offset - 1);
        offset = LineWrapEngine.FindLineStart(
            offset, _document.Length,
            (off, buf) => _document.Read(off, buf));
      }
      _topDocOffset = offset;
    }
  }

  private unsafe void RenderGotoDialog()
  {
    if (_showGotoDialog) {
      ImGui.OpenPopup("Goto Line"u8);
      _showGotoDialog = false;
    }

    bool open = true;
    if (ImGui.BeginPopupModal("Goto Line"u8, ref open, ImGuiWindowFlags.AlwaysAutoResize)) {
      ImGui.Text("Enter line number:"u8);

      if (_gotoFocusInput) {
        ImGui.SetKeyboardFocusHere();
        _gotoFocusInput = false;
      }

      bool enter;
      fixed (byte* pBuf = _gotoInputBuf) {
        enter = ImGui.InputText("##gotoLine"u8, pBuf, (nuint)_gotoInputBuf.Length,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
      }

      bool doGoto = enter || ImGui.Button("Go"u8);
      ImGui.SameLine();
      bool cancel = ImGui.Button("Cancel"u8);

      if (doGoto) {
        string input = System.Text.Encoding.ASCII.GetString(_gotoInputBuf).TrimEnd('\0').Trim();
        if (long.TryParse(input, out long lineNum) && lineNum > 0) {
          long targetOffset = FindOffsetOfLine(lineNum);
          ScrollToOffset(targetOffset);
          _cachedTopOffset = _topDocOffset;
          _cachedTopLineNumber = lineNum;
        }
        ImGui.CloseCurrentPopup();
      }
      if (cancel || ImGui.IsKeyPressed(ImGuiKey.Escape))
        ImGui.CloseCurrentPopup();

      ImGui.EndPopup();
    }
  }

  // ── Line number tracking ──────────────────────────────────────

  /// <summary>
  /// Returns the 1-based line number at the given byte offset.
  /// Uses incremental scanning for small deltas and falls back to heuristic for huge jumps.
  /// </summary>
  private long GetLineNumberAtOffset(long offset)
  {
    if (offset == 0) {
      _cachedTopOffset = 0;
      _cachedTopLineNumber = 1;
      return 1;
    }
    if (offset == _cachedTopOffset) return _cachedTopLineNumber;

    long delta = offset - _cachedTopOffset;
    const long MaxScanDelta = 10 * 1024 * 1024; // 10 MB

    if (_cachedTopOffset >= 0 && delta > 0 && delta <= MaxScanDelta) {
      _cachedTopLineNumber += CountNewlinesInRange(_cachedTopOffset, offset);
      _cachedTopOffset = offset;
    } else if (_cachedTopOffset >= 0 && delta < 0 && -delta <= MaxScanDelta) {
      _cachedTopLineNumber -= CountNewlinesInRange(offset, _cachedTopOffset);
      _cachedTopOffset = offset;
    } else if (offset <= MaxScanDelta) {
      _cachedTopLineNumber = 1 + CountNewlinesInRange(0, offset);
      _cachedTopOffset = offset;
    } else {
      // Too far — use heuristic
      _cachedTopLineNumber = _estimatedTotalLines > 0
          ? Math.Max(1, (long)((double)offset / _document.Length * _estimatedTotalLines))
          : 1;
      _cachedTopOffset = offset;
    }
    return _cachedTopLineNumber;
  }

  /// <summary>Counts LF bytes in [start, end).</summary>
  private long CountNewlinesInRange(long start, long end)
  {
    long count = 0;
    long pos = start;
    while (pos < end) {
      int chunkLen = (int)Math.Min(end - pos, _countBuffer.Length);
      int read = _document.Read(pos, _countBuffer.AsSpan(0, chunkLen));
      if (read == 0) break;
      for (int i = 0; i < read; i++)
        if (_countBuffer[i] == 0x0A) count++;
      pos += read;
    }
    return count;
  }

  /// <summary>Finds the byte offset where the given 1-based line number starts.</summary>
  private long FindOffsetOfLine(long lineNumber)
  {
    if (lineNumber <= 1) return 0;
    long targetNewlines = lineNumber - 1;
    long nlCount = 0;
    long pos = 0;
    while (pos < _document.Length) {
      int chunkLen = (int)Math.Min(_document.Length - pos, _countBuffer.Length);
      int read = _document.Read(pos, _countBuffer.AsSpan(0, chunkLen));
      if (read == 0) break;
      for (int i = 0; i < read; i++) {
        if (_countBuffer[i] == 0x0A) {
          nlCount++;
          if (nlCount == targetNewlines) return pos + i + 1;
        }
      }
      pos += read;
    }
    return Math.Min(pos, Math.Max(0, _document.Length - 1));
  }

  // ── Scroll mapping (same float-precision strategy as HexView) ──

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static long ScrollYToLine(float scrollY, long totalLines, long maxFirstLine,
      float idealHeight, float maxScrollY, float lineHeight)
  {
    if (idealHeight <= MaxVirtualHeight)
      return (long)(scrollY / lineHeight);

    double fraction = scrollY / maxScrollY;
    return (long)(fraction * maxFirstLine);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static float LineToScrollY(long line, long totalLines, long maxFirstLine,
      float idealHeight, float maxScrollY, float lineHeight)
  {
    if (idealHeight <= MaxVirtualHeight)
      return line * lineHeight;

    double fraction = maxFirstLine > 0 ? (double)line / maxFirstLine : 0;
    return (float)(fraction * maxScrollY);
  }

  /// <summary>Formats a line number as right-justified ASCII, null-terminated.</summary>
  /// <remarks>Buffer layout: 8 digit positions + 1 space + null = 10 bytes.</remarks>
  private static void FormatLineNumber(long lineNum, Span<byte> buf)
  {
    // buf = [d d d d d d d d ' ' \0]  (10 bytes)
    buf.Fill((byte)' ');
    buf[buf.Length - 1] = 0;     // null terminator
    // buf[buf.Length - 2] remains ' ' (separator between numbers and text)

    int pos = buf.Length - 3;    // rightmost digit position

    if (lineNum == 0) {
      buf[pos] = (byte)'0';
      return;
    }

    long n = lineNum;
    while (n > 0 && pos >= 0) {
      buf[pos--] = (byte)('0' + (int)(n % 10));
      n /= 10;
    }
  }

  // Pointer to a static wrap indicator string "↪\0" (UTF-8: E2 86 AA 00)
  private static ReadOnlySpan<byte> WrapIndicatorBytes => [0xE2, 0x86, 0xAA, 0x00];

  private static unsafe byte* WrapIndicatorPtr {
    get {
      fixed (byte* p = WrapIndicatorBytes)
        return p;
    }
  }
}
