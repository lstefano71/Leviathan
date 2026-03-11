using System.Runtime.CompilerServices;
using System.Text;
using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.TUI.Rendering;

namespace Leviathan.TUI.Views;

/// <summary>
/// Text view logic: navigation, editing, line formatting, encoding-aware.
/// Pure logic — no hex1b dependencies.
/// </summary>
internal sealed class TextViewController
{
    private readonly AppState _state;
    private byte[] _readBuffer = new byte[128 * 1024];
    private readonly LineWrapEngine _wrapEngine = new();
    private VisualLine[] _visualLines = new VisualLine[2048];
    private readonly List<int> _charByteOffsets = new(4096);
    private long _cachedTopOffset;
    private long _cachedTopLineNumber = 1;
    private int _lastRenderedLineCount;

    internal TextViewController(AppState state)
    {
        _state = state;
    }

    /// <summary>
    /// Produces visible text rows as ANSI-colored strings.
    /// </summary>
    internal string[] RenderRows(int terminalWidth, int terminalHeight)
    {
        Document? doc = _state.Document;
        if (doc is null)
            return [];

        int visibleRows = terminalHeight - 2;
        if (visibleRows < 1) visibleRows = 1;
        _state.VisibleRows = visibleRows;

        // Text area width = terminal width - gutter(9)
        int textAreaCols = Math.Max(1, terminalWidth - 9);

        EnsureCursorVisible(textAreaCols);

        // Compute visual lines
        ITextDecoder decoder = _state.Decoder;
        int maxCols = _state.WordWrap ? textAreaCols : int.MaxValue;

        // Read enough data to fill the screen. When word-wrap is off and lines are very long,
        // the initial estimate may be too small — retry with larger buffers.
        int readSize = Math.Max((visibleRows + 4) * 256, 16384);
        int bytesRead = 0;
        int lineCount = 0;
        const int MaxReadSize = 16 * 1024 * 1024; // 16 MB cap

        while (true)
        {
            readSize = (int)Math.Min(readSize, doc.Length - _state.TextTopOffset);
            if (readSize <= 0)
                return ["~"];

            EnsureBuffer(readSize);
            Span<byte> buf = _readBuffer.AsSpan(0, readSize);
            bytesRead = doc.Read(_state.TextTopOffset, buf);
            if (bytesRead == 0)
                return ["~"];

            EnsureVisualLines(visibleRows + 8);
            lineCount = _wrapEngine.ComputeVisualLines(
                buf[..bytesRead], _state.TextTopOffset, maxCols, _state.WordWrap, _visualLines, decoder);

            // If we have enough lines or we've read all remaining data, stop
            if (lineCount >= visibleRows ||
                _state.TextTopOffset + bytesRead >= doc.Length ||
                readSize >= MaxReadSize)
                break;

            // Double the read size and retry
            readSize = Math.Min(readSize * 2, MaxReadSize);
        }

        Span<byte> data = _readBuffer.AsSpan(0, bytesRead);

        // Collect visible search matches for highlight
        long viewStart = _state.TextTopOffset;
        long viewEnd = _state.TextTopOffset + bytesRead;
        List<SearchResult> visibleMatches = CollectVisibleMatches(viewStart, viewEnd);
        int currentMatchIdx = _state.CurrentMatchIndex;
        SearchResult? activeMatch = currentMatchIdx >= 0 && currentMatchIdx < _state.SearchResults.Count
            ? _state.SearchResults[currentMatchIdx]
            : null;

        // Track line numbers
        long currentLineNumber = ComputeLineNumber(_state.TextTopOffset);
        _lastRenderedLineCount = lineCount;

        string[] rows = new string[Math.Min(visibleRows, lineCount)];
        for (int i = 0; i < rows.Length; i++)
        {
            if (i >= lineCount) { rows[i] = "~"; continue; }

            VisualLine vl = _visualLines[i];
            long lineDocOffset = vl.DocOffset;
            long relativeStart = lineDocOffset - _state.TextTopOffset;
            if (relativeStart < 0 || relativeStart > bytesRead)
            {
                rows[i] = "~";
                continue;
            }

            int lineStart = (int)relativeStart;
            int lineByteLen = vl.ByteLength;
            if (lineByteLen < 0)
            {
                rows[i] = "~";
                continue;
            }

            int maxLineLen = bytesRead - lineStart;
            if (lineByteLen > maxLineLen)
                lineByteLen = maxLineLen;

            // Determine if this is a hard line start
            bool isHardLine = lineStart == 0 || IsNewlineAt(data, lineStart - 1, decoder);

            // Decode bytes to chars for display, with byte offset mapping
            ReadOnlySpan<byte> lineBytes = data.Slice(lineStart, lineByteLen);
            string text = DecodeLineToDisplay(lineBytes, decoder, _state.TabWidth, _charByteOffsets);

            if (isHardLine)
                currentLineNumber++;

            // Is this the very last visible line? (no more visual lines after this one)
            // Used by BuildTextLine to decide whether to show a block cursor at lineEnd (EOF).
            bool isLastVisibleLine = i + 1 >= lineCount;

            rows[i] = AnsiBuilder.BuildTextLine(
                text.AsSpan(),
                isHardLine ? currentLineNumber - 1 : -1,
                isHardLine,
                lineDocOffset,
                lineByteLen,
                _state.TextCursorOffset,
                _state.TextSelStart,
                _state.TextSelEnd,
                _state.TabWidth,
                isLastVisibleLine,
                _charByteOffsets,
                visibleMatches,
                activeMatch);
        }

        return rows;
    }

    // ─── Navigation ───

    internal void MoveCursorLeft(bool extend)
    {
        Document? doc = _state.Document;
        if (doc is null || _state.TextCursorOffset <= 0) return;

        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;

        // Read up to 4 bytes before cursor and find the start of the previous rune
        int lookBack = (int)Math.Min(4, _state.TextCursorOffset);
        Span<byte> prev = stackalloc byte[lookBack];
        doc.Read(_state.TextCursorOffset - lookBack, prev);

        ITextDecoder decoder = _state.Decoder;
        // Walk forward through the bytes to find the last rune start
        int pos = 0;
        int lastRuneStart = 0;
        while (pos < lookBack)
        {
            lastRuneStart = pos;
            (_, int len) = decoder.DecodeRune(prev, pos);
            if (len <= 0) { pos++; continue; }
            pos += len;
        }
        int stepBack = lookBack - lastRuneStart;
        _state.TextCursorOffset -= stepBack;
    }

    internal void MoveCursorRight(bool extend)
    {
        Document? doc = _state.Document;
        if (doc is null || _state.TextCursorOffset >= doc.Length) return;

        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;

        Span<byte> next = stackalloc byte[4];
        int read = doc.Read(_state.TextCursorOffset, next);
        if (read == 0) return;

        (_, int len) = _state.Decoder.DecodeRune(next[..read], 0);
        if (len <= 0) len = 1;
        _state.TextCursorOffset = Math.Min(_state.TextCursorOffset + len, doc.Length);
    }

    internal void MoveCursorUp(bool extend) => MoveVertical(-1, extend);
    internal void MoveCursorDown(bool extend) => MoveVertical(1, extend);

    internal void PageUp(bool extend)
    {
        for (int i = 0; i < _state.VisibleRows; i++)
            MoveVertical(-1, extend);
    }

    internal void PageDown(bool extend)
    {
        for (int i = 0; i < _state.VisibleRows; i++)
            MoveVertical(1, extend);
    }

    /// <summary>
    /// Scrolls the view up by the specified number of lines without moving the cursor.
    /// </summary>
    internal void ScrollUp(int lines)
    {
        Document? doc = _state.Document;
        if (doc is null || _state.TextTopOffset <= 0) return;

        long offset = _state.TextTopOffset;
        for (int i = 0; i < lines && offset > 0; i++)
        {
            // Move to the end of the previous line, then find its start
            long prevLineEnd = Math.Max(0, offset - 1);
            offset = FindLineStart(prevLineEnd);
        }
        _state.TextTopOffset = offset;
    }

    /// <summary>
    /// Scrolls the view down by the specified number of lines without moving the cursor.
    /// </summary>
    internal void ScrollDown(int lines)
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        long offset = _state.TextTopOffset;
        for (int i = 0; i < lines && offset < doc.Length; i++)
        {
            long lineEnd = FindLineEnd(offset);
            long nextStart = lineEnd + 1;
            if (nextStart > doc.Length) nextStart = doc.Length;
            offset = nextStart;
        }
        _state.TextTopOffset = Math.Min(offset, doc.Length);
    }

    internal void Home(bool extend)
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;

        // Find start of current line
        _state.TextCursorOffset = FindLineStart(_state.TextCursorOffset);
    }

    internal void End(bool extend)
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;

        _state.TextCursorOffset = FindLineEnd(_state.TextCursorOffset);
    }

    internal void CtrlHome(bool extend)
    {
        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;
        _state.TextCursorOffset = 0;
    }

    internal void CtrlEnd(bool extend)
    {
        if (_state.Document is null) return;
        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;
        _state.TextCursorOffset = _state.Document.Length;
    }

    internal void GotoLine(long lineNumber)
    {
        Document? doc = _state.Document;
        if (doc is null || lineNumber < 1) return;

        long offset = FindOffsetOfLine(lineNumber);
        _state.TextCursorOffset = offset;
        _state.TextSelectionAnchor = -1;
    }

    internal void GotoOffset(long offset)
    {
        if (_state.Document is null) return;
        _state.TextCursorOffset = Math.Clamp(offset, 0, _state.Document.Length);
        _state.TextSelectionAnchor = -1;
    }

    /// <summary>
    /// Selects the entire document (anchor = 0, cursor = Length).
    /// </summary>
    internal void SelectAll()
    {
        Document? doc = _state.Document;
        if (doc is null || doc.Length == 0) return;

        _state.TextSelectionAnchor = 0;
        _state.TextCursorOffset = doc.Length - 1;
    }

    // ─── Editing ───

    internal void InsertChar(char c)
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        DeleteSelection();

        Span<byte> encoded = stackalloc byte[8];
        int len = _state.Decoder.EncodeRune(new Rune(c), encoded);
        if (len > 0)
        {
            doc.Insert(_state.TextCursorOffset, encoded[..len]);
            _state.TextCursorOffset += len;
        }
    }

    internal void InsertNewline()
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        DeleteSelection();

        ITextDecoder decoder = _state.Decoder;
        if (decoder.Encoding == TextEncoding.Utf16Le)
        {
            doc.Insert(_state.TextCursorOffset, [0x0A, 0x00]);
            _state.TextCursorOffset += 2;
        }
        else
        {
            doc.Insert(_state.TextCursorOffset, [(byte)'\n']);
            _state.TextCursorOffset++;
        }
    }

    internal void Backspace()
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        if (_state.TextSelectionAnchor >= 0)
        {
            DeleteSelection();
            return;
        }

        if (_state.TextCursorOffset <= 0) return;

        int lookBack = (int)Math.Min(4, _state.TextCursorOffset);
        Span<byte> prev = stackalloc byte[lookBack];
        doc.Read(_state.TextCursorOffset - lookBack, prev);

        ITextDecoder decoder = _state.Decoder;
        int pos = 0;
        int lastRuneStart = 0;
        while (pos < lookBack)
        {
            lastRuneStart = pos;
            (_, int len) = decoder.DecodeRune(prev, pos);
            if (len <= 0) { pos++; continue; }
            pos += len;
        }

        int deleteLen = lookBack - lastRuneStart;
        _state.TextCursorOffset -= deleteLen;
        doc.Delete(_state.TextCursorOffset, deleteLen);
    }

    internal void Delete()
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        if (_state.TextSelectionAnchor >= 0)
        {
            DeleteSelection();
            return;
        }

        if (_state.TextCursorOffset >= doc.Length) return;

        Span<byte> next = stackalloc byte[4];
        int read = doc.Read(_state.TextCursorOffset, next);
        if (read == 0) return;

        (_, int len) = _state.Decoder.DecodeRune(next[..read], 0);
        if (len <= 0) len = 1;
        doc.Delete(_state.TextCursorOffset, len);
    }

    internal string? CopySelection()
    {
        Document? doc = _state.Document;
        if (doc is null || _state.TextSelectionAnchor < 0) return null;

        long start = _state.TextSelStart;
        long len = _state.TextSelEnd - start + 1;
        if (len <= 0 || len > 10 * 1024 * 1024) return null;

        byte[] selBytes = new byte[(int)len];
        doc.Read(start, selBytes);

        // Transcode to UTF-8 for clipboard
        ITextDecoder decoder = _state.Decoder;
        if (decoder.Encoding == TextEncoding.Utf8)
            return Encoding.UTF8.GetString(selBytes);

        StringBuilder sb = new();
        int pos = 0;
        while (pos < selBytes.Length)
        {
            (Rune rune, int runeLen) = decoder.DecodeRune(selBytes, pos);
            if (runeLen <= 0) { pos++; continue; }
            sb.Append(char.ConvertFromUtf32(rune.Value));
            pos += runeLen;
        }
        return sb.ToString();
    }

    internal void Paste(string text)
    {
        Document? doc = _state.Document;
        if (doc is null || string.IsNullOrEmpty(text)) return;

        DeleteSelection();

        ITextDecoder decoder = _state.Decoder;
        if (decoder.Encoding == TextEncoding.Utf8)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            doc.Insert(_state.TextCursorOffset, utf8);
            _state.TextCursorOffset += utf8.Length;
        }
        else
        {
            // Transcode from UTF-8 string to target encoding
            Span<byte> runeBuf = stackalloc byte[8];
            List<byte> encoded = [];
            foreach (int cp in StringToCodePoints(text))
            {
                if (Rune.TryCreate(cp, out Rune rune))
                {
                    int len = decoder.EncodeRune(rune, runeBuf);
                    if (len > 0)
                        encoded.AddRange(runeBuf[..len].ToArray());
                }
            }
            byte[] data = encoded.ToArray();
            doc.Insert(_state.TextCursorOffset, data);
            _state.TextCursorOffset += data.Length;
        }
    }

    // ─── Mouse ───

    /// <summary>
    /// Moves the cursor to the byte offset corresponding to the given screen position.
    /// Gutter: 9 chars (8-char line number + 1 space). Text starts at column 9.
    /// Uses the visual lines from the most recent render pass.
    /// </summary>
    /// <summary>
    /// Moves the cursor to the byte offset corresponding to the given screen position.
    /// When <paramref name="extend"/> is true, the selection anchor is preserved (drag select).
    /// </summary>
    internal void ClickAtPosition(int viewRow, int viewCol, int textAreaCols, bool extend = false)
    {
        Document? doc = _state.Document;
        if (doc is null) return;
        if (viewRow < 0 || viewRow >= _lastRenderedLineCount) return;

        VisualLine vl = _visualLines[viewRow];

        const int GutterWidth = 9;
        int textCol = viewCol - GutterWidth;
        if (textCol < 0) textCol = 0;

        long relativeStart = vl.DocOffset - _state.TextTopOffset;
        if (relativeStart < 0) return;

        int lineStart = (int)relativeStart;
        int lineByteLen = vl.ByteLength;
        int maxLen = _readBuffer.Length - lineStart;
        if (lineByteLen > maxLen) lineByteLen = maxLen;
        if (lineByteLen <= 0)
        {
            if (!extend) _state.TextSelectionAnchor = -1;
            else if (_state.TextSelectionAnchor < 0) _state.TextSelectionAnchor = _state.TextCursorOffset;
            _state.TextCursorOffset = vl.DocOffset;
            return;
        }

        ReadOnlySpan<byte> lineBytes = _readBuffer.AsSpan(lineStart, lineByteLen);
        List<int> offsets = new(lineByteLen);
        ITextDecoder decoder = _state.Decoder;
        DecodeLineToDisplay(lineBytes, decoder, _state.TabWidth, offsets);

        int charIdx = Math.Min(textCol, offsets.Count);
        long newOffset;
        if (charIdx >= offsets.Count)
        {
            newOffset = vl.DocOffset + lineByteLen;
            if (newOffset > doc.Length) newOffset = doc.Length;
            if (newOffset > 0 && newOffset == doc.Length) newOffset = doc.Length - 1;
        }
        else
        {
            newOffset = vl.DocOffset + offsets[charIdx];
        }

        newOffset = Math.Clamp(newOffset, 0, Math.Max(0, doc.Length - 1));

        if (!extend)
        {
            _state.TextSelectionAnchor = -1;
        }
        else if (_state.TextSelectionAnchor < 0)
        {
            _state.TextSelectionAnchor = _state.TextCursorOffset;
        }

        _state.TextCursorOffset = newOffset;
    }

    // ─── Helpers ───

    private void DeleteSelection()
    {
        Document? doc = _state.Document;
        if (doc is null || _state.TextSelectionAnchor < 0) return;

        long start = _state.TextSelStart;
        long len = _state.TextSelEnd - start + 1;
        doc.Delete(start, len);
        _state.TextCursorOffset = start;
        _state.TextSelectionAnchor = -1;
    }

    private void MoveVertical(int direction, bool extend)
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;

        if (direction < 0)
        {
            long lineStart = FindLineStart(_state.TextCursorOffset);
            if (lineStart == 0) return;
            long prevLineEnd = lineStart - 1;
            long prevLineStart = FindLineStart(prevLineEnd);
            long col = _state.TextCursorOffset - lineStart;
            _state.TextCursorOffset = Math.Min(prevLineStart + col, prevLineEnd);
        }
        else
        {
            long lineEnd = FindLineEnd(_state.TextCursorOffset);
            if (lineEnd >= doc.Length) return;
            long lineStart = FindLineStart(_state.TextCursorOffset);
            long col = _state.TextCursorOffset - lineStart;
            long nextLineStart = lineEnd + _state.Decoder.MinCharBytes;
            long nextLineEnd = FindLineEnd(nextLineStart);
            _state.TextCursorOffset = Math.Min(nextLineStart + col, nextLineEnd);
        }
    }

    private long FindLineStart(long offset)
    {
        Document? doc = _state.Document;
        if (doc is null || offset <= 0) return 0;

        int lookBack = (int)Math.Min(4096, offset);
        byte[] buf = new byte[lookBack];
        doc.Read(offset - lookBack, buf);

        ITextDecoder decoder = _state.Decoder;
        int minChar = decoder.MinCharBytes;

        for (int i = lookBack - minChar; i >= 0; i -= minChar)
        {
            if (decoder.IsNewline(buf, i, out _))
                return offset - lookBack + i + minChar;
        }
        return offset - lookBack;
    }

    private long FindLineEnd(long offset)
    {
        Document? doc = _state.Document;
        if (doc is null) return offset;

        int lookAhead = (int)Math.Min(4096, doc.Length - offset);
        if (lookAhead <= 0) return offset;

        byte[] buf = new byte[lookAhead];
        doc.Read(offset, buf);

        ITextDecoder decoder = _state.Decoder;
        int minChar = decoder.MinCharBytes;

        for (int i = 0; i + minChar <= lookAhead; i += minChar)
        {
            if (decoder.IsNewline(buf, i, out _))
                return offset + i;
        }
        return Math.Min(offset + lookAhead, doc.Length);
    }

    private void EnsureCursorVisible(int textAreaCols)
    {
        if (_state.TextCursorOffset < 0) return;

        if (_state.TextCursorOffset < _state.TextTopOffset)
            _state.TextTopOffset = FindLineStart(_state.TextCursorOffset);

        // Compute the actual byte range covered by visibleRows lines from the top
        long visibleEnd = ComputeVisibleEnd(_state.TextTopOffset, _state.VisibleRows);
        if (_state.TextCursorOffset > visibleEnd)
        {
            // Place cursor roughly in the middle
            long newTop = FindLineStart(_state.TextCursorOffset);
            // Back up half-a-screen worth of lines
            for (int i = 0; i < _state.VisibleRows / 2 && newTop > 0; i++)
            {
                long prev = FindLineStart(Math.Max(0, newTop - 1));
                if (prev >= newTop) break;
                newTop = prev;
            }
            _state.TextTopOffset = newTop;
        }
    }

    /// <summary>
    /// Finds the byte offset just past the last byte of the Nth line from startOffset.
    /// Scans at most 4 MB or to EOF.
    /// </summary>
    private long ComputeVisibleEnd(long startOffset, int lineCount)
    {
        Document? doc = _state.Document;
        if (doc is null) return startOffset;

        ITextDecoder decoder = _state.Decoder;
        int linesFound = 0;
        long pos = startOffset;
        byte[] scanBuf = new byte[32768];

        while (pos < doc.Length && linesFound < lineCount)
        {
            int readLen = (int)Math.Min(scanBuf.Length, doc.Length - pos);
            int read = doc.Read(pos, scanBuf.AsSpan(0, readLen));
            if (read == 0) break;

            int minChar = decoder.MinCharBytes;
            for (int i = 0; i + minChar <= read; i += minChar)
            {
                if (decoder.IsNewline(scanBuf, i, out _))
                {
                    linesFound++;
                    if (linesFound >= lineCount)
                        return pos + i + minChar;
                }
            }
            pos += read;
        }
        return pos;
    }

    private long ComputeLineNumber(long offset)
    {
        Document? doc = _state.Document;
        if (doc is null) return 1;

        // Incremental from cache
        long from, to;
        long lineNum;
        if (offset >= _cachedTopOffset)
        {
            from = _cachedTopOffset;
            to = offset;
            lineNum = _cachedTopLineNumber;
        }
        else
        {
            from = 0;
            to = offset;
            lineNum = 1;
        }

        // Don't scan more than 10MB
        if (to - from > 10 * 1024 * 1024)
        {
            lineNum = Math.Max(1, (long)((double)offset / doc.Length * _state.EstimatedTotalLines));
        }
        else
        {
            lineNum += CountNewlines(from, to);
        }

        _cachedTopOffset = offset;
        _cachedTopLineNumber = lineNum;
        return lineNum;
    }

    private long CountNewlines(long from, long to)
    {
        Document? doc = _state.Document;
        if (doc is null) return 0;

        ITextDecoder decoder = _state.Decoder;
        long count = 0;
        long pos = from;
        byte[] buf = new byte[8192];

        while (pos < to)
        {
            int readLen = (int)Math.Min(buf.Length, to - pos);
            int read = doc.Read(pos, buf.AsSpan(0, readLen));
            if (read == 0) break;

            int minChar = decoder.MinCharBytes;
            for (int i = 0; i + minChar <= read; i += minChar)
            {
                if (decoder.IsNewline(buf, i, out _))
                    count++;
            }
            pos += read;
        }
        return count;
    }

    private long FindOffsetOfLine(long targetLine)
    {
        Document? doc = _state.Document;
        if (doc is null || targetLine <= 1) return 0;

        ITextDecoder decoder = _state.Decoder;
        long linesFound = 0;
        long pos = 0;
        byte[] buf = new byte[8192];

        while (pos < doc.Length && linesFound < targetLine - 1)
        {
            int readLen = (int)Math.Min(buf.Length, doc.Length - pos);
            int read = doc.Read(pos, buf.AsSpan(0, readLen));
            if (read == 0) break;

            int minChar = decoder.MinCharBytes;
            for (int i = 0; i + minChar <= read; i += minChar)
            {
                if (decoder.IsNewline(buf, i, out _))
                {
                    linesFound++;
                    if (linesFound >= targetLine - 1)
                        return pos + i + minChar;
                }
            }
            pos += read;
        }
        return pos;
    }

    private static bool IsNewlineAt(ReadOnlySpan<byte> buf, int index, ITextDecoder decoder)
    {
        if (index < 0 || index >= buf.Length) return false;
        return decoder.IsNewline(buf, index, out _);
    }

    /// <summary>
    /// Collects search matches that overlap the visible byte range [viewStart, viewEnd).
    /// Uses binary search for efficiency on large result sets.
    /// </summary>
    private List<SearchResult> CollectVisibleMatches(long viewStart, long viewEnd)
    {
        List<SearchResult> results = _state.SearchResults;
        List<SearchResult> visible = [];
        if (results.Count == 0) return visible;

        // Binary search to find first match that could overlap
        int lo = 0, hi = results.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (results[mid].Offset + results[mid].Length <= viewStart)
                lo = mid + 1;
            else
                hi = mid;
        }

        for (int i = lo; i < results.Count; i++)
        {
            SearchResult m = results[i];
            if (m.Offset >= viewEnd) break;
            if (m.Offset + m.Length > viewStart)
                visible.Add(m);
        }
        return visible;
    }

    private static string DecodeLineToDisplay(ReadOnlySpan<byte> bytes, ITextDecoder decoder, int tabWidth,
        List<int>? byteOffsets = null)
    {
        StringBuilder sb = new(bytes.Length);
        byteOffsets?.Clear();
        int pos = 0;
        while (pos < bytes.Length)
        {
            (Rune rune, int len) = decoder.DecodeRune(bytes, pos);
            if (len <= 0) { pos++; continue; }

            int codePoint = rune.Value;
            if (codePoint == '\n' || codePoint == '\r')
            {
                pos += len;
                continue;
            }
            if (codePoint == '\t')
            {
                for (int i = 0; i < tabWidth; i++)
                {
                    sb.Append(' ');
                    byteOffsets?.Add(pos);
                }
                pos += len;
                continue;
            }
            if (codePoint < 0x20)
            {
                sb.Append('.');
                byteOffsets?.Add(pos);
                pos += len;
                continue;
            }

            if (codePoint <= 0xFFFF)
            {
                sb.Append((char)codePoint);
                byteOffsets?.Add(pos);
            }
            else
            {
                string surr = char.ConvertFromUtf32(codePoint);
                sb.Append(surr);
                // Surrogate pair: both chars map to same byte offset
                byteOffsets?.Add(pos);
                if (surr.Length > 1)
                    byteOffsets?.Add(pos);
            }

            pos += len;
        }
        return sb.ToString();
    }

    private static IEnumerable<int> StringToCodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else
            {
                yield return s[i];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureBuffer(int size)
    {
        if (_readBuffer.Length < size)
            _readBuffer = new byte[size];
    }

    private void EnsureVisualLines(int count)
    {
        if (_visualLines.Length < count)
            _visualLines = new VisualLine[count];
    }
}
