using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Views;

/// <summary>
/// High-performance text editor control. Uses LineWrapEngine for JIT visual-line
/// computation within the viewport, rendering via Avalonia DrawingContext.
/// </summary>
internal sealed class TextViewControl : Control
{
    private static readonly Typeface MonoTypeface = new("Consolas, Courier New, monospace");
    private const double FontSize = 14;
    private const double LinePadding = 2;

    private readonly AppState _state;
    private byte[] _readBuffer = new byte[131072]; // 128 KB initial, grows up to 16 MB
    private byte[] _scanBuffer = new byte[4096];
    private VisualLine[] _visualLines = new VisualLine[2048];
    private VisualLine[] _navVisualLines = new VisualLine[256];
    private readonly LineWrapEngine _wrapEngine;
    private const int MaxReadSize = 16 * 1024 * 1024; // 16 MB max buffer
    private ViewTheme _theme = ViewTheme.Resolve();
    private int _lastRenderedLineCount;
    private int _desiredColumn = -1;
    private int _userScrollFrames;
    private int _lastTextAreaCols;
    private long _cachedTopOffset;
    private long _cachedTopLineNumber = 1;

    internal Action? StateChanged;

    /// <summary>Scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? ScrollBar { get; set; }
    private bool _updatingScroll;
    private bool _scrollUpdateQueued;

    public TextViewControl(AppState state)
    {
        _state = state;
        _wrapEngine = new LineWrapEngine(state.TabWidth);
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _theme = ViewTheme.Resolve();
            InvalidateVisual();
        };
    }

    internal void OnScrollBarValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScroll || _state.Document is null || ScrollBar is null) return;
        Core.Document doc = _state.Document;
        long newTop;
        if (_state.LineIndex is { SparseEntryCount: > 0 })
        {
            long targetLine = (long)e.NewValue + 1;
            newTop = FindOffsetOfLine(targetLine);
        }
        else
        {
            long fileLen = _state.FileLength;
            long totalLines = Math.Max(1, _state.EstimatedTotalLines);
            long newOffset = (long)(e.NewValue / Math.Max(1, totalLines) * fileLen);
            newOffset = Math.Clamp(newOffset, _state.BomLength, doc.Length);
            newTop = FindLineStart(newOffset);
        }

        newTop = Math.Clamp(newTop, _state.BomLength, doc.Length);
        _state.TextTopOffset = newTop;
        _state.TextCursorOffset = Math.Max(newTop, _state.BomLength);
        _state.TextSelectionAnchor = -1;
        _desiredColumn = -1;
        _userScrollFrames = 2;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    private void UpdateScrollBar()
    {
        if (_state.Document is null || ScrollBar is null) return;
        _updatingScroll = true;
        try
        {
            if (_state.LineIndex?.TotalLineCount > 0)
                _state.EstimatedTotalLines = _state.LineIndex.TotalLineCount;

            long totalLines = Math.Max(1, _state.EstimatedTotalLines);
            double maxTop = Math.Max(0, totalLines - _state.VisibleRows);
            long currentLine = ComputeLineNumber(_state.TextTopOffset);
            double scrollPos = Math.Clamp(currentLine - 1, 0, (long)maxTop);

            ScrollBar.Maximum = maxTop;
            ScrollBar.ViewportSize = Math.Max(1, _state.VisibleRows);
            if (_userScrollFrames > 0)
                _userScrollFrames--;
            else
                ScrollBar.Value = scrollPos;
        }
        finally
        {
            _updatingScroll = false;
        }
    }

    private void QueueScrollBarUpdate()
    {
        if (_scrollUpdateQueued) return;
        _scrollUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollUpdateQueued = false;
            UpdateScrollBar();
        }, DispatcherPriority.Background);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_state.Document is null) return;

        Rect bounds = Bounds;
        ViewTheme theme = _theme;

        // Paint control background
        context.FillRectangle(theme.Background, bounds);

        double charWidth = MeasureCharWidth();
        double lineHeight = FontSize + LinePadding;

        int visibleRows = Math.Max(1, (int)(bounds.Height / lineHeight));
        _state.VisibleRows = visibleRows;

        // Gutter width (line numbers)
        long totalLines = Math.Max(1, _state.EstimatedTotalLines);
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(totalLines + 1)));
        double gutterWidth = (gutterDigits + 2) * charWidth;

        // Draw gutter background
        context.FillRectangle(theme.GutterBackground, new Rect(0, 0, gutterWidth - charWidth, bounds.Height));

        // Draw gutter separator
        context.DrawLine(theme.GutterPen, new Point(gutterWidth - charWidth, 0),
            new Point(gutterWidth - charWidth, bounds.Height));

        double textAreaWidth = bounds.Width - gutterWidth;
        int textAreaCols = Math.Max(1, (int)(textAreaWidth / charWidth));
        _lastTextAreaCols = textAreaCols;
        int columnsAvailable = _state.WordWrap ? textAreaCols : int.MaxValue;

        long topOffset = _state.TextTopOffset;
        int readSize = Math.Max((visibleRows + 4) * 256, 16384);
        int bytesRead;
        int lineCount;
        while (true)
        {
            readSize = (int)Math.Min(readSize, _state.FileLength - topOffset);
            if (readSize <= 0) return;

            EnsureBuffer(readSize);
            bytesRead = _state.Document.Read(topOffset, _readBuffer.AsSpan(0, readSize));
            if (bytesRead == 0) return;

            EnsureVisualLines(visibleRows + 64);
            lineCount = _wrapEngine.ComputeVisualLines(
                _readBuffer.AsSpan(0, bytesRead), topOffset, columnsAvailable, _state.WordWrap, _visualLines, _state.Decoder);

            if (lineCount >= visibleRows ||
                topOffset + bytesRead >= _state.FileLength ||
                readSize >= MaxReadSize)
                break;

            readSize = Math.Min(readSize * 2, MaxReadSize);
        }

        ReadOnlySpan<byte> data = _readBuffer.AsSpan(0, bytesRead);
        _lastRenderedLineCount = lineCount;
        lineCount = Math.Min(lineCount, visibleRows);

        // Brushes
        IBrush textBrush = theme.TextPrimary;
        IBrush gutterTextBrush = theme.TextSecondary;
        IBrush selectionBrush = theme.SelectionHighlight;
        IBrush cursorBrush = theme.CursorBar;
        IBrush matchBrush = theme.MatchHighlight;
        IBrush activeMatchBrush = theme.ActiveMatchHighlight;
        List<SearchResult> matches = _state.SearchResults;
        int activeMatchIdx = _state.CurrentMatchIndex;

        // Selection range
        long selStart = _state.TextSelStart;
        long selEnd = _state.TextSelEnd;

        long currentLineNumber = ComputeLineNumber(topOffset) - 1;

        for (int row = 0; row < lineCount; row++)
        {
            VisualLine vl = _visualLines[row];
            double y = row * lineHeight;
            long lineAbsOffset = vl.DocOffset;

            // Detect hard line start: first row, or preceded by a newline
            bool isHardLine;
            if (row == 0)
            {
                isHardLine = topOffset == _state.BomLength || IsNewlineBefore(topOffset);
            }
            else
            {
                long prevEnd = _visualLines[row - 1].DocOffset + _visualLines[row - 1].ByteLength;
                isHardLine = vl.DocOffset != prevEnd || IsNewlineBefore(vl.DocOffset);
            }

            if (isHardLine)
                currentLineNumber++;

            // Gutter: show line number on hard lines, wrap indicator on continuations
            if (isHardLine)
            {
                string lineNumStr = currentLineNumber.ToString();
                double lineNumX = gutterWidth - (lineNumStr.Length + 1) * charWidth;

                FormattedText lineNumText = new(lineNumStr,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, FontSize, gutterTextBrush);
                context.DrawText(lineNumText, new Point(lineNumX, y));
            }
            else
            {
                // Wrap continuation: show ↪ indicator
                string wrapIndicator = "↪";
                double wrapX = gutterWidth - 2 * charWidth;
                FormattedText wrapText = new(wrapIndicator,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, FontSize, theme.TextMuted);
                context.DrawText(wrapText, new Point(wrapX, y));
            }

            // Render text content
            int localOffset = (int)(vl.DocOffset - topOffset);
            if (localOffset < 0 || localOffset + vl.ByteLength > data.Length) continue;
            ReadOnlySpan<byte> lineData = data.Slice(localOffset, vl.ByteLength);
            double textX = gutterWidth;

            // Decode and render characters
            int charCol = 0;
            int byteIdx = 0;
            while (byteIdx < lineData.Length)
            {
                (System.Text.Rune rune, int runeBytes) = _state.Decoder.DecodeRune(lineData, byteIdx);
                if (runeBytes <= 0) { byteIdx++; continue; }

                int codePoint = rune.Value;
                long absOffset = lineAbsOffset + byteIdx;

                // Match highlight
                bool isMatch = false;
                bool isActiveMatch = false;
                for (int m = 0; m < matches.Count; m++)
                {
                    long mStart = matches[m].Offset;
                    long mEnd = mStart + matches[m].Length - 1;
                    if (absOffset >= mStart && absOffset <= mEnd)
                    {
                        isMatch = true;
                        isActiveMatch = m == activeMatchIdx;
                        break;
                    }
                    if (mStart > absOffset) break;
                }
                if (isMatch)
                {
                    context.FillRectangle(isActiveMatch ? activeMatchBrush : matchBrush,
                        new Rect(textX + charCol * charWidth, y, charWidth, lineHeight));
                }

                // Selection highlight
                if (selStart >= 0 && absOffset >= selStart && absOffset <= selEnd)
                {
                    context.FillRectangle(selectionBrush,
                        new Rect(textX + charCol * charWidth, y, charWidth, lineHeight));
                }

                // Cursor
                if (absOffset == _state.TextCursorOffset)
                {
                    context.FillRectangle(cursorBrush,
                        new Rect(textX + charCol * charWidth, y, 2, lineHeight));
                }

                // Character
                char ch;
                if (codePoint == '\t')
                {
                    int tabStop = _state.TabWidth - (charCol % _state.TabWidth);
                    charCol += tabStop;
                    byteIdx += runeBytes;
                    continue;
                }
                else if (codePoint == '\n' || codePoint == '\r')
                {
                    byteIdx += runeBytes;
                    continue;
                }
                else if (codePoint >= 0x20 && codePoint < 0x10000)
                {
                    ch = (char)codePoint;
                }
                else
                {
                    ch = '.';
                }

                FormattedText charText = new(ch.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, FontSize, textBrush);
                context.DrawText(charText, new Point(textX + charCol * charWidth, y));

                charCol++;
                byteIdx += runeBytes;
            }
        }

        QueueScrollBarUpdate();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double MeasureCharWidth()
    {
        FormattedText measurement = new("0", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, MonoTypeface, FontSize, Brushes.White);
        return measurement.Width;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_state.Document is null) { base.OnKeyDown(e); return; }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        long oldCursor = _state.TextCursorOffset;
        if (_state.WordWrap && _lastRenderedLineCount > 0)
        {
            switch (e.Key)
            {
                case Key.Up:
                    MoveVerticalVisual(-1);
                    if (shift)
                    {
                        if (_state.TextSelectionAnchor < 0)
                            _state.TextSelectionAnchor = oldCursor;
                    }
                    else
                    {
                        _state.TextSelectionAnchor = -1;
                    }
                    e.Handled = true;
                    OnStateChanged();
                    return;
                case Key.Down:
                    MoveVerticalVisual(1);
                    if (shift)
                    {
                        if (_state.TextSelectionAnchor < 0)
                            _state.TextSelectionAnchor = oldCursor;
                    }
                    else
                    {
                        _state.TextSelectionAnchor = -1;
                    }
                    e.Handled = true;
                    OnStateChanged();
                    return;
                case Key.PageUp:
                    PageVerticalVisual(-1, shift);
                    e.Handled = true;
                    return;
                case Key.PageDown:
                    PageVerticalVisual(1, shift);
                    e.Handled = true;
                    return;
            }
        }

        long newCursor = oldCursor;
        switch (e.Key)
        {
            case Key.Right:
                _desiredColumn = -1;
                newCursor = Math.Min(oldCursor + _state.Decoder.MinCharBytes, _state.FileLength);
                break;
            case Key.Left:
                _desiredColumn = -1;
                newCursor = Math.Max(oldCursor - _state.Decoder.MinCharBytes, _state.BomLength);
                break;
            case Key.Down:
                newCursor = MoveDown(oldCursor);
                break;
            case Key.Up:
                newCursor = MoveUp(oldCursor);
                break;
            case Key.PageDown:
                for (int i = 0; i < _state.VisibleRows; i++)
                    newCursor = MoveDown(newCursor);
                break;
            case Key.PageUp:
                for (int i = 0; i < _state.VisibleRows; i++)
                    newCursor = MoveUp(newCursor);
                break;
            case Key.Home:
                _desiredColumn = -1;
                newCursor = ctrl ? _state.BomLength : FindLineStart(oldCursor);
                break;
            case Key.End:
                _desiredColumn = -1;
                newCursor = ctrl ? _state.FileLength : FindLineEnd(oldCursor);
                break;
            case Key.Back:
                _desiredColumn = -1;
                if (oldCursor > _state.BomLength)
                {
                    long deleteAt = Math.Max(oldCursor - _state.Decoder.MinCharBytes, _state.BomLength);
                    long deleteLen = oldCursor - deleteAt;
                    _state.Document.Delete(deleteAt, deleteLen);
                    newCursor = deleteAt;
                }
                break;
            case Key.Delete:
                _desiredColumn = -1;
                if (oldCursor < _state.FileLength)
                {
                    _state.Document.Delete(oldCursor, _state.Decoder.MinCharBytes);
                }
                break;
            case Key.Enter:
                _desiredColumn = -1;
                _state.Document.Insert(oldCursor, _state.Decoder.EncodeString("\n"));
                newCursor = oldCursor + _state.Decoder.MinCharBytes;
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        if (shift)
        {
            if (_state.TextSelectionAnchor < 0)
                _state.TextSelectionAnchor = oldCursor;
        }
        else
        {
            _state.TextSelectionAnchor = -1;
        }

        _state.TextCursorOffset = newCursor;
        e.Handled = true;
        OnStateChanged();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_state.Document is null || string.IsNullOrEmpty(e.Text)) return;

        _desiredColumn = -1;
        byte[] encoded = _state.Decoder.EncodeString(e.Text);
        _state.Document.Insert(_state.TextCursorOffset, encoded);
        _state.TextCursorOffset += encoded.Length;
        OnStateChanged();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_state.Document is null) return;

        Point pos = e.GetPosition(this);
        double charWidth = MeasureCharWidth();
        double lineHeight = FontSize + LinePadding;

        // Compute gutter width
        long totalLines = Math.Max(1, _state.EstimatedTotalLines);
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(totalLines + 1)));
        double gutterWidth = (gutterDigits + 2) * charWidth;

        if (pos.X < gutterWidth) return; // clicked in gutter

        int row = (int)(pos.Y / lineHeight);
        int col = (int)((pos.X - gutterWidth) / charWidth);

        // Read data and compute visual lines (same as Render)
        long topOffset = _state.TextTopOffset;
        int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - topOffset);
        if (readLen <= 0) return;
        _state.Document.Read(topOffset, _readBuffer.AsSpan(0, readLen));
        ReadOnlySpan<byte> data = _readBuffer.AsSpan(0, readLen);

        double textAreaWidth = Bounds.Width - gutterWidth;
        int columnsAvailable = _state.WordWrap ? Math.Max(1, (int)(textAreaWidth / charWidth)) : int.MaxValue;
        int lineCount = _wrapEngine.ComputeVisualLines(data, topOffset, columnsAvailable, _state.WordWrap, _visualLines.AsSpan(), _state.Decoder);

        if (row >= 0 && row < lineCount)
        {
            VisualLine vl = _visualLines[row];
            // Walk bytes to find the column
            int localOffset = (int)(vl.DocOffset - topOffset);
            if (localOffset < 0 || localOffset + vl.ByteLength > data.Length) return;
            ReadOnlySpan<byte> lineData = data.Slice(localOffset, vl.ByteLength);
            int charCol = 0;
            int byteIdx = 0;
            long targetOffset = vl.DocOffset; // default to line start
            while (byteIdx < lineData.Length && charCol < col)
            {
                (System.Text.Rune rune, int runeBytes) = _state.Decoder.DecodeRune(lineData, byteIdx);
                if (runeBytes <= 0) { byteIdx++; continue; }
                targetOffset = vl.DocOffset + byteIdx;
                charCol++;
                byteIdx += runeBytes;
            }
            if (charCol >= col && byteIdx < lineData.Length)
                targetOffset = vl.DocOffset + byteIdx;

            _state.TextCursorOffset = targetOffset;
            _state.TextSelectionAnchor = -1;
            _desiredColumn = -1;
            OnStateChanged();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_state.Document is null) return;

        int rows = e.Delta.Y > 0 ? -3 : 3;
        ScrollByRows(rows);
        InvalidateVisual();
    }

    /// <summary>Navigates to a specific line number (1-based).</summary>
    internal void GotoLine(long lineNumber)
    {
        if (_state.Document is null || lineNumber < 1) return;
        _desiredColumn = -1;
        long offset = FindOffsetOfLine(lineNumber);
        _state.TextCursorOffset = Math.Max(offset, _state.BomLength);
        _state.TextSelectionAnchor = -1;
        OnStateChanged();
    }

    internal void GotoOffset(long offset)
    {
        if (_state.Document is null) return;
        _desiredColumn = -1;
        _state.TextCursorOffset = Math.Clamp(offset, _state.BomLength, _state.Document.Length);
        _state.TextSelectionAnchor = -1;
        OnStateChanged();
    }

    private void EnsureCursorVisible()
    {
        if (_state.TextCursorOffset < 0 || _state.Document is null) return;

        int textAreaCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : GetColumnsAvailable();
        int vpHeight = _state.VisibleRows;
        if (vpHeight <= 0) vpHeight = 24;

        if (_state.TextCursorOffset < _state.TextTopOffset)
        {
            if (_state.WordWrap)
            {
                _state.TextTopOffset = ComputeViewportTopForCursor(_state.TextCursorOffset, textAreaCols, 0);
            }
            else
            {
                _state.TextTopOffset = FindLineStart(_state.TextCursorOffset);
            }
            return;
        }

        int maxCols = _state.WordWrap ? textAreaCols : int.MaxValue;
        int readSize = Math.Max((vpHeight + 4) * 256, 16384);
        readSize = (int)Math.Min(readSize, _state.Document.Length - _state.TextTopOffset);
        if (readSize <= 0) return;

        int expandedRead = Math.Min(readSize * 4, MaxReadSize);
        expandedRead = (int)Math.Min(expandedRead, _state.Document.Length - _state.TextTopOffset);
        EnsureBuffer(expandedRead);
        int bytesRead = _state.Document.Read(_state.TextTopOffset, _readBuffer.AsSpan(0, expandedRead));
        if (bytesRead == 0) return;

        EnsureVisualLines(vpHeight + 64);
        int lineCount = _wrapEngine.ComputeVisualLines(
            _readBuffer.AsSpan(0, bytesRead), _state.TextTopOffset, maxCols, _state.WordWrap, _visualLines, _state.Decoder);

        bool cursorVisible = false;
        int rowsToCheck = Math.Min(vpHeight, lineCount);
        for (int i = 0; i < rowsToCheck; i++)
        {
            long vlStart = _visualLines[i].DocOffset;
            long vlEnd = vlStart + _visualLines[i].ByteLength;
            if (_state.TextCursorOffset >= vlStart && _state.TextCursorOffset <= vlEnd)
            {
                cursorVisible = true;
                break;
            }
        }

        if (!cursorVisible)
        {
            if (_state.WordWrap)
            {
                _state.TextTopOffset = ComputeViewportTopForCursor(_state.TextCursorOffset, textAreaCols, vpHeight / 3);
            }
            else
            {
                long newTop = FindLineStart(_state.TextCursorOffset);
                int minChar = _state.Decoder.MinCharBytes;
                for (int i = 0; i < vpHeight / 2 && newTop > _state.BomLength; i++)
                {
                    long prev = FindLineStart(Math.Max(_state.BomLength, newTop - minChar));
                    if (prev >= newTop) break;
                    newTop = prev;
                }
                _state.TextTopOffset = newTop;
            }
        }
    }

    private void ScrollByRows(int rowDelta)
    {
        if (_state.WordWrap)
        {
            int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
            long offset = _state.TextTopOffset;
            if (rowDelta > 0)
            {
                for (int i = 0; i < rowDelta && offset < _state.FileLength; i++)
                {
                    if (FindNextVisualLine(offset, maxCols, out VisualLine nextVl))
                    {
                        long next = nextVl.DocOffset + nextVl.ByteLength;
                        if (next <= offset) break;
                        offset = next;
                    }
                    else break;
                }
            }
            else
            {
                int steps = -rowDelta;
                for (int i = 0; i < steps && offset > _state.BomLength; i++)
                {
                    if (FindPreviousVisualLine(offset, maxCols, out VisualLine prevVl))
                        offset = prevVl.DocOffset;
                    else break;
                }
            }

            _state.TextTopOffset = Math.Clamp(offset, _state.BomLength, Math.Max(_state.BomLength, _state.FileLength));
            return;
        }

        long off = _state.TextTopOffset;
        if (rowDelta > 0)
        {
            for (int i = 0; i < rowDelta && off < _state.FileLength; i++)
            {
                long lineEnd = FindLineEnd(off);
                int newlineLen = NewlineLengthAt(lineEnd);
                off = Math.Min(lineEnd + Math.Max(newlineLen, _state.Decoder.MinCharBytes), _state.FileLength);
            }
        }
        else
        {
            int steps = -rowDelta;
            int minChar = _state.Decoder.MinCharBytes;
            for (int i = 0; i < steps && off > _state.BomLength; i++)
            {
                long prevLineEnd = Math.Max(_state.BomLength, off - minChar);
                off = FindLineStart(prevLineEnd);
            }
        }
        _state.TextTopOffset = Math.Clamp(off, _state.BomLength, Math.Max(_state.BomLength, _state.FileLength));
    }

    private long MoveDown(long offset)
    {
        long lineEnd = FindLineEnd(offset);
        if (lineEnd >= _state.FileLength) return offset;
        int newlineLen = NewlineLengthAt(lineEnd);
        return Math.Min(lineEnd + Math.Max(newlineLen, _state.Decoder.MinCharBytes), _state.FileLength);
    }

    private long MoveUp(long offset)
    {
        int minChar = _state.Decoder.MinCharBytes;
        long lineStart = FindLineStart(offset);
        if (lineStart <= _state.BomLength) return _state.BomLength;
        return FindLineStart(Math.Max(_state.BomLength, lineStart - minChar));
    }

    /// <summary>Returns the number of character columns available in the text area.</summary>
    private int GetColumnsAvailable()
    {
        double charWidth = MeasureCharWidth();
        long totalLines = Math.Max(1, _state.EstimatedTotalLines);
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(totalLines + 1)));
        double gutterWidth = (gutterDigits + 2) * charWidth;
        double textAreaWidth = Bounds.Width - gutterWidth;
        return Math.Max(1, (int)(textAreaWidth / charWidth));
    }

    private void PageVerticalVisual(int direction, bool extend)
    {
        if (_state.Document is null) return;

        if (!extend) _state.TextSelectionAnchor = -1;
        else if (_state.TextSelectionAnchor < 0)
            _state.TextSelectionAnchor = _state.TextCursorOffset;

        int vpHeight = _state.VisibleRows;
        if (vpHeight <= 0) vpHeight = 24;
        int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;

        int cursorRow = -1;
        for (int i = 0; i < _lastRenderedLineCount; i++)
        {
            long vlStart = _visualLines[i].DocOffset;
            long vlEnd = vlStart + _visualLines[i].ByteLength;
            if (_state.TextCursorOffset >= vlStart && _state.TextCursorOffset < vlEnd)
            {
                cursorRow = i;
                break;
            }

            if (_state.TextCursorOffset == vlEnd)
            {
                bool nextStartsHere = (i + 1 < _lastRenderedLineCount) && _visualLines[i + 1].DocOffset == vlEnd;
                if (!nextStartsHere)
                {
                    cursorRow = i;
                    break;
                }
            }
        }
        if (cursorRow < 0) cursorRow = 0;

        int displayCol;
        if (_desiredColumn >= 0)
        {
            displayCol = _desiredColumn;
        }
        else
        {
            displayCol = 0;
            if (cursorRow < _lastRenderedLineCount)
            {
                VisualLine curVl = _visualLines[cursorRow];
                int curLen = (int)Math.Min(curVl.ByteLength, int.MaxValue);
                EnsureBuffer(curLen);
                _state.Document.Read(curVl.DocOffset, _readBuffer.AsSpan(0, curLen));
                displayCol = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, curLen),
                    _state.TextCursorOffset - curVl.DocOffset, _state.Decoder);
            }
            _desiredColumn = displayCol;
        }

        long newTop = _state.TextTopOffset;
        if (direction > 0)
        {
            for (int i = 0; i < vpHeight && newTop < _state.Document.Length; i++)
            {
                if (FindNextVisualLine(newTop, maxCols, out VisualLine nextVl))
                {
                    long next = nextVl.DocOffset + nextVl.ByteLength;
                    if (next <= newTop) break;
                    newTop = next;
                }
                else break;
            }
            newTop = Math.Min(newTop, _state.Document.Length);
        }
        else
        {
            for (int i = 0; i < vpHeight && newTop > _state.BomLength; i++)
            {
                if (FindPreviousVisualLine(newTop, maxCols, out VisualLine prevVl))
                    newTop = prevVl.DocOffset;
                else break;
            }
        }

        _state.TextTopOffset = newTop;

        int readSize = Math.Max((vpHeight + 4) * 256, 16384);
        readSize = (int)Math.Min(readSize, _state.Document.Length - _state.TextTopOffset);
        if (readSize > 0)
        {
            EnsureBuffer(readSize);
            int bytesRead = _state.Document.Read(_state.TextTopOffset, _readBuffer.AsSpan(0, readSize));
            if (bytesRead > 0)
            {
                EnsureVisualLines(vpHeight + 64);
                int lineCount = _wrapEngine.ComputeVisualLines(
                    _readBuffer.AsSpan(0, bytesRead), _state.TextTopOffset, maxCols, true, _visualLines, _state.Decoder);
                _lastRenderedLineCount = lineCount;

                int targetRow = Math.Min(cursorRow, lineCount - 1);
                if (targetRow < 0) targetRow = 0;
                if (targetRow < lineCount)
                    PlaceCursorOnVisualLine(_visualLines[targetRow], displayCol);
            }
        }

        OnStateChanged();
    }

    private void MoveVerticalVisual(int direction)
    {
        if (_state.Document is null) return;

        int cursorRow = -1;
        for (int i = 0; i < _lastRenderedLineCount; i++)
        {
            long vlStart = _visualLines[i].DocOffset;
            long vlEnd = vlStart + _visualLines[i].ByteLength;
            if (_state.TextCursorOffset >= vlStart && _state.TextCursorOffset < vlEnd)
            {
                cursorRow = i;
                break;
            }
            if (_state.TextCursorOffset == vlEnd)
            {
                bool nextLineStartsHere = (i + 1 < _lastRenderedLineCount) && _visualLines[i + 1].DocOffset == vlEnd;
                if (!nextLineStartsHere)
                {
                    cursorRow = i;
                    break;
                }
            }
        }

        int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
        if (cursorRow < 0)
        {
            if (FindVisualLineContaining(_state.TextCursorOffset, maxCols, out VisualLine curVlFb))
            {
                int col;
                if (_desiredColumn >= 0)
                {
                    col = _desiredColumn;
                }
                else
                {
                    int lineLen = (int)Math.Min(curVlFb.ByteLength, int.MaxValue);
                    EnsureBuffer(lineLen);
                    _state.Document.Read(curVlFb.DocOffset, _readBuffer.AsSpan(0, lineLen));
                    col = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, lineLen),
                        _state.TextCursorOffset - curVlFb.DocOffset, _state.Decoder);
                    _desiredColumn = col;
                }

                if (direction < 0)
                {
                    if (curVlFb.DocOffset <= _state.BomLength) return;
                    if (FindPreviousVisualLine(curVlFb.DocOffset, maxCols, out VisualLine prevVl))
                        PlaceCursorOnVisualLine(prevVl, col);
                }
                else
                {
                    long afterCur = curVlFb.DocOffset + curVlFb.ByteLength;
                    if (afterCur < _state.Document.Length && FindNextVisualLine(afterCur, maxCols, out VisualLine nextVl))
                        PlaceCursorOnVisualLine(nextVl, col);
                }
                return;
            }

            if (direction < 0)
            {
                _state.TextCursorOffset = MoveUp(_state.TextCursorOffset);
            }
            else
            {
                _state.TextCursorOffset = MoveDown(_state.TextCursorOffset);
            }
            return;
        }

        VisualLine curVl = _visualLines[cursorRow];
        int displayCol;
        if (_desiredColumn >= 0)
        {
            displayCol = _desiredColumn;
        }
        else
        {
            int lineLen = (int)Math.Min(curVl.ByteLength, int.MaxValue);
            EnsureBuffer(lineLen);
            _state.Document.Read(curVl.DocOffset, _readBuffer.AsSpan(0, lineLen));
            long byteInLine = _state.TextCursorOffset - curVl.DocOffset;
            displayCol = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, lineLen), byteInLine, _state.Decoder);
            _desiredColumn = displayCol;
        }

        int targetRow = cursorRow + direction;
        if (targetRow < 0)
        {
            long firstVlStart = _visualLines[0].DocOffset;
            if (firstVlStart <= _state.BomLength) return;
            if (FindPreviousVisualLine(firstVlStart, maxCols, out VisualLine prevVl))
                PlaceCursorOnVisualLine(prevVl, displayCol);
            return;
        }

        if (targetRow >= _lastRenderedLineCount)
        {
            VisualLine lastVl = _visualLines[_lastRenderedLineCount - 1];
            long afterLast = lastVl.DocOffset + lastVl.ByteLength;
            if (afterLast >= _state.Document.Length) return;
            if (FindNextVisualLine(afterLast, maxCols, out VisualLine nextVl))
                PlaceCursorOnVisualLine(nextVl, displayCol);
            return;
        }

        PlaceCursorOnVisualLine(_visualLines[targetRow], displayCol);
    }

    private int ByteOffsetToDisplayColumn(ReadOnlySpan<byte> lineBytes, long byteOffsetInLine, ITextDecoder decoder)
    {
        int col = 0;
        int pos = 0;
        while (pos < lineBytes.Length && pos < byteOffsetInLine)
        {
            (System.Text.Rune rune, int len) = decoder.DecodeRune(lineBytes, pos);
            if (len <= 0) { pos++; continue; }

            int cp = rune.Value;
            if (cp == '\n' || cp == '\r') break;
            if (cp == 0xFEFF) { pos += len; continue; }
            col += Utf8Utils.RuneColumnWidth(rune, _state.TabWidth);
            pos += len;
        }
        return col;
    }

    private int DisplayColumnToByteOffset(ReadOnlySpan<byte> lineBytes, int targetColumn, ITextDecoder decoder)
    {
        int col = 0;
        int pos = 0;
        while (pos < lineBytes.Length)
        {
            (System.Text.Rune rune, int len) = decoder.DecodeRune(lineBytes, pos);
            if (len <= 0) { pos++; continue; }

            int cp = rune.Value;
            if (cp == '\n' || cp == '\r') break;
            if (cp == 0xFEFF) { pos += len; continue; }
            if (col >= targetColumn) return pos;
            col += Utf8Utils.RuneColumnWidth(rune, _state.TabWidth);
            pos += len;
        }
        return pos;
    }

    private void PlaceCursorOnVisualLine(VisualLine targetVl, int displayCol)
    {
        if (_state.Document is null) return;

        int targetLength = (int)Math.Min(targetVl.ByteLength, int.MaxValue);
        EnsureBuffer(targetLength);
        _state.Document.Read(targetVl.DocOffset, _readBuffer.AsSpan(0, targetLength));
        int targetByteOffset = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, targetLength), displayCol, _state.Decoder);
        _state.TextCursorOffset = Math.Clamp(targetVl.DocOffset + targetByteOffset,
            targetVl.DocOffset, Math.Min(targetVl.DocOffset + targetVl.ByteLength, _state.Document.Length));
    }

    /// <summary>
    /// Finds the first visual line starting at <paramref name="fromOffset"/>
    /// when word wrap is active. Returns its extent so the caller knows where
    /// the next visual line starts.
    /// </summary>
    private bool FindNextVisualLine(long fromOffset, int maxCols, out VisualLine result)
    {
        result = default;
        if (_state.Document is null || fromOffset >= _state.FileLength) return false;

        int readLen = (int)Math.Min((long)maxCols * 8 + 64, _state.FileLength - fromOffset);
        if (readLen <= 0) return false;

        EnsureBuffer(readLen);
        int bytesRead = _state.Document.Read(fromOffset, _readBuffer.AsSpan(0, readLen));
        if (bytesRead == 0) return false;

        Span<VisualLine> output = stackalloc VisualLine[2];
        int count = _wrapEngine.ComputeVisualLines(
            _readBuffer.AsSpan(0, bytesRead), fromOffset, maxCols, true, output, _state.Decoder);
        if (count == 0) return false;

        result = output[0];
        return true;
    }

    private bool FindPreviousVisualLine(long vlStartOffset, int maxCols, out VisualLine result)
    {
        result = default;
        if (_state.Document is null || vlStartOffset <= _state.BomLength) return false;

        long hardLineStart = FindLineStart(vlStartOffset);
        if (vlStartOffset == hardLineStart)
        {
            long prevPos = Math.Max(_state.BomLength, vlStartOffset - _state.Decoder.MinCharBytes);
            hardLineStart = FindLineStart(prevPos);
        }
        if (hardLineStart < _state.BomLength) hardLineStart = _state.BomLength;

        bool found = false;
        long scanPos = hardLineStart;
        while (scanPos < vlStartOffset)
        {
            int readLen = (int)Math.Min(
                Math.Max((long)maxCols * 256, vlStartOffset - scanPos + (long)maxCols * 8),
                _state.FileLength - scanPos);
            if (readLen <= 0) break;

            EnsureBuffer(readLen);
            int bytesRead = _state.Document.Read(scanPos, _readBuffer.AsSpan(0, readLen));
            if (bytesRead == 0) break;

            EnsureNavVisualLines(256);
            int count = _wrapEngine.ComputeVisualLines(
                _readBuffer.AsSpan(0, bytesRead), scanPos, maxCols, true, _navVisualLines, _state.Decoder);
            if (count == 0) break;

            for (int i = 0; i < count; i++)
            {
                if (_navVisualLines[i].DocOffset >= vlStartOffset) return found;
                result = _navVisualLines[i];
                found = true;
            }

            VisualLine lastVl = _navVisualLines[count - 1];
            long newScan = lastVl.DocOffset + lastVl.ByteLength;
            if (newScan <= scanPos) break;
            scanPos = newScan;
        }

        return found;
    }

    private bool FindVisualLineContaining(long offset, int maxCols, out VisualLine result)
    {
        result = default;
        if (_state.Document is null || offset < _state.BomLength || offset > _state.Document.Length) return false;

        long hardLineStart = FindLineStart(offset);
        if (hardLineStart < _state.BomLength) hardLineStart = _state.BomLength;

        long scanPos = hardLineStart;
        while (scanPos <= offset && scanPos < _state.Document.Length)
        {
            int readLen = (int)Math.Min(
                Math.Max((long)maxCols * 256, offset - scanPos + (long)maxCols * 8),
                _state.Document.Length - scanPos);
            if (readLen <= 0) break;

            EnsureBuffer(readLen);
            int bytesRead = _state.Document.Read(scanPos, _readBuffer.AsSpan(0, readLen));
            if (bytesRead == 0) break;

            EnsureNavVisualLines(256);
            int count = _wrapEngine.ComputeVisualLines(
                _readBuffer.AsSpan(0, bytesRead), scanPos, maxCols, true, _navVisualLines, _state.Decoder);
            if (count == 0) break;

            for (int i = 0; i < count; i++)
            {
                long vlStart = _navVisualLines[i].DocOffset;
                long vlEnd = vlStart + _navVisualLines[i].ByteLength;
                if (offset >= vlStart && offset < vlEnd)
                {
                    result = _navVisualLines[i];
                    return true;
                }
                if (offset == vlEnd)
                {
                    bool nextStartsHere = (i + 1 < count) && _navVisualLines[i + 1].DocOffset == vlEnd;
                    bool bufferExhausted = (i + 1 >= count) && (count >= _navVisualLines.Length);
                    if (!nextStartsHere && !bufferExhausted)
                    {
                        result = _navVisualLines[i];
                        return true;
                    }
                }
            }

            VisualLine lastVl = _navVisualLines[count - 1];
            long newScan = lastVl.DocOffset + lastVl.ByteLength;
            if (newScan <= scanPos) break;
            scanPos = newScan;
        }

        return false;
    }

    private long ComputeViewportTopForCursor(long cursorOffset, int maxCols, int contextBefore)
    {
        if (_state.Document is null) return cursorOffset;

        long hardLineStart = FindLineStart(cursorOffset);
        if (hardLineStart < _state.BomLength) hardLineStart = _state.BomLength;

        long scanPos = hardLineStart;
        int bufferSize = Math.Max(contextBefore + 1, 1);
        long[] recentStarts = new long[bufferSize];
        int recentWritten = 0;

        while (scanPos <= cursorOffset && scanPos < _state.Document.Length)
        {
            int readLen = (int)Math.Min(
                Math.Max((long)maxCols * 256, cursorOffset - scanPos + (long)maxCols * 8),
                _state.Document.Length - scanPos);
            if (readLen <= 0) break;

            EnsureBuffer(readLen);
            int bytesRead = _state.Document.Read(scanPos, _readBuffer.AsSpan(0, readLen));
            if (bytesRead == 0) break;

            EnsureNavVisualLines(256);
            int count = _wrapEngine.ComputeVisualLines(
                _readBuffer.AsSpan(0, bytesRead), scanPos, maxCols, true, _navVisualLines, _state.Decoder);
            if (count == 0) break;

            for (int i = 0; i < count; i++)
            {
                long vlStart = _navVisualLines[i].DocOffset;
                long vlEnd = vlStart + _navVisualLines[i].ByteLength;

                recentStarts[recentWritten % bufferSize] = vlStart;
                recentWritten++;

                if (cursorOffset >= vlStart && cursorOffset <= vlEnd)
                {
                    int backLines = Math.Min(contextBefore, recentWritten - 1);
                    int idx = ((recentWritten - 1 - backLines) % bufferSize + bufferSize) % bufferSize;
                    return recentStarts[idx];
                }
            }

            VisualLine lastVl = _navVisualLines[count - 1];
            long newScan = lastVl.DocOffset + lastVl.ByteLength;
            if (newScan <= scanPos) break;
            scanPos = newScan;
        }

        return cursorOffset;
    }

    /// <summary>Grows the read buffer if needed, up to <see cref="MaxReadSize"/>.</summary>
    private void EnsureBuffer(int minSize)
    {
        if (_readBuffer.Length >= minSize) return;
        int newSize = _readBuffer.Length;
        while (newSize < minSize && newSize < MaxReadSize)
            newSize *= 2;
        newSize = Math.Min(newSize, MaxReadSize);
        if (newSize > _readBuffer.Length)
            _readBuffer = new byte[newSize];
    }

    private void EnsureVisualLines(int count)
    {
        if (_visualLines.Length < count)
            _visualLines = new VisualLine[count];
    }

    private void EnsureNavVisualLines(int count)
    {
        if (_navVisualLines.Length < count)
            _navVisualLines = new VisualLine[count];
    }

    private void EnsureScanBuffer(int minSize)
    {
        if (_scanBuffer.Length >= minSize) return;
        int newSize = _scanBuffer.Length;
        while (newSize < minSize && newSize < MaxReadSize)
            newSize *= 2;
        newSize = Math.Min(newSize, MaxReadSize);
        if (newSize > _scanBuffer.Length)
            _scanBuffer = new byte[newSize];
    }

    private long FindLineStart(long offset)
    {
        if (_state.Document is null || offset <= _state.BomLength) return _state.BomLength;

        int minChar = _state.Decoder.MinCharBytes;
        int chunkSize = 4096;
        long search = offset;
        while (search > _state.BomLength)
        {
            int chunkLen = (int)Math.Min(chunkSize, search - _state.BomLength);
            long chunkStart = search - chunkLen;
            EnsureScanBuffer(chunkLen);
            Span<byte> buf = _scanBuffer.AsSpan(0, chunkLen);
            _state.Document.Read(chunkStart, buf);

            for (int i = chunkLen - minChar; i >= 0; i -= minChar)
            {
                if (IsLF(buf, i, minChar))
                    return chunkStart + i + minChar;
            }

            search = chunkStart;
            if (chunkSize < MaxReadSize)
                chunkSize = Math.Min(chunkSize * 2, MaxReadSize);
        }

        return _state.BomLength;
    }

    private long FindLineEnd(long offset)
    {
        if (_state.Document is null) return offset;

        int minChar = _state.Decoder.MinCharBytes;
        int chunkSize = 8192;
        int crPrefix = minChar;
        long search = offset;
        bool firstChunk = true;

        while (search < _state.Document.Length)
        {
            long readStart = firstChunk ? search : Math.Max(offset, search - crPrefix);
            int overlap = (int)(search - readStart);
            int chunkLen = (int)Math.Min(chunkSize + overlap, _state.Document.Length - readStart);
            if (chunkLen <= 0) break;

            EnsureScanBuffer(chunkLen);
            Span<byte> buf = _scanBuffer.AsSpan(0, chunkLen);
            _state.Document.Read(readStart, buf);

            int scanStart = firstChunk ? 0 : overlap;
            if (minChar > 1)
                scanStart = (scanStart + minChar - 1) / minChar * minChar;

            for (int i = scanStart; i + minChar <= chunkLen; i += minChar)
            {
                if (IsLF(buf, i, minChar))
                {
                    if (minChar == 1 && i > 0 && buf[i - 1] == 0x0D)
                        return readStart + i - 1;
                    if (minChar == 2 && i >= 2 && buf[i - 2] == 0x0D && buf[i - 1] == 0x00)
                        return readStart + i - 2;
                    return readStart + i;
                }
            }

            search = readStart + chunkLen;
            firstChunk = false;
            if (chunkSize < MaxReadSize)
                chunkSize = Math.Min(chunkSize * 2, MaxReadSize);
        }

        return _state.Document.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLF(ReadOnlySpan<byte> buffer, int index, int minChar)
    {
        if (minChar == 1)
            return buffer[index] == 0x0A;
        return index + 1 < buffer.Length && buffer[index] == 0x0A && buffer[index + 1] == 0x00;
    }

    private int NewlineLengthAt(long offset)
    {
        if (_state.Document is null || offset < _state.BomLength || offset >= _state.Document.Length) return 0;

        int minChar = _state.Decoder.MinCharBytes;
        Span<byte> buf = stackalloc byte[4];
        int read = _state.Document.Read(offset, buf[..(int)Math.Min(minChar * 2, _state.Document.Length - offset)]);
        if (read < minChar) return 0;

        if (minChar == 1)
        {
            if (buf[0] == 0x0D && read >= 2 && buf[1] == 0x0A) return 2;
            if (buf[0] == 0x0A) return 1;
            if (buf[0] == 0x0D) return 1;
            return 0;
        }

        if (read >= 4 && buf[0] == 0x0D && buf[1] == 0x00 && buf[2] == 0x0A && buf[3] == 0x00) return 4;
        if (read >= 2 && buf[0] == 0x0A && buf[1] == 0x00) return 2;
        if (read >= 2 && buf[0] == 0x0D && buf[1] == 0x00) return 2;
        return 0;
    }

    private long ComputeLineNumber(long offset)
    {
        if (_state.Document is null) return 1;

        if (_state.LineIndex is { SparseEntryCount: > 0 } index)
        {
            long baseLine = index.EstimateLineForOffset(offset, _state.Document.Length);
            int sparseFactor = index.SparseFactor;
            int sparseIdx = (int)(baseLine / sparseFactor) - 1;
            if (sparseIdx >= index.SparseEntryCount)
                sparseIdx = index.SparseEntryCount - 1;

            long scanFrom;
            long lineNum;
            if (sparseIdx >= 0)
            {
                scanFrom = index.GetSparseOffset(sparseIdx) + _state.Decoder.MinCharBytes;
                lineNum = (long)(sparseIdx + 1) * sparseFactor + 1;
            }
            else
            {
                scanFrom = _state.BomLength;
                lineNum = 1;
            }

            if (offset > scanFrom)
            {
                long gap = offset - scanFrom;
                if (gap <= 4 * 1024 * 1024)
                    lineNum += CountNewlines(scanFrom, offset);
            }

            _cachedTopOffset = offset;
            _cachedTopLineNumber = lineNum;
            return lineNum;
        }

        long from;
        long to;
        long lineNumber;
        if (offset >= _cachedTopOffset)
        {
            from = _cachedTopOffset;
            to = offset;
            lineNumber = _cachedTopLineNumber;
        }
        else
        {
            from = _state.BomLength;
            to = offset;
            lineNumber = 1;
        }

        if (to - from > 10 * 1024 * 1024 && _state.Document.Length > 0)
            lineNumber = Math.Max(1, (long)((double)offset / _state.Document.Length * Math.Max(1, _state.EstimatedTotalLines)));
        else
            lineNumber += CountNewlines(from, to);

        _cachedTopOffset = offset;
        _cachedTopLineNumber = lineNumber;
        return lineNumber;
    }

    private long CountNewlines(long from, long to)
    {
        if (_state.Document is null || to <= from) return 0;

        int minChar = _state.Decoder.MinCharBytes;
        long count = 0;
        long pos = from;
        EnsureScanBuffer(65536);

        while (pos < to)
        {
            int readLen = (int)Math.Min(_scanBuffer.Length, to - pos);
            Span<byte> buf = _scanBuffer.AsSpan(0, readLen);
            int read = _state.Document.Read(pos, buf);
            if (read == 0) break;

            if (minChar == 2)
            {
                int alignedLen = read & ~1;
                for (int i = 0; i + 1 < alignedLen; i += 2)
                {
                    if (buf[i] == 0x0A && buf[i + 1] == 0x00)
                        count++;
                }
            }
            else
            {
                for (int i = 0; i < read; i++)
                {
                    if (buf[i] == 0x0A)
                        count++;
                }
            }

            pos += read;
        }

        return count;
    }

    private long FindOffsetOfLine(long targetLine)
    {
        if (_state.Document is null || targetLine <= 1) return _state.BomLength;

        int minChar = _state.Decoder.MinCharBytes;
        long newlinesNeeded = targetLine - 1;
        long startOffset = _state.BomLength;
        long newlinesCounted = 0;

        if (_state.LineIndex is { SparseEntryCount: > 0 } index)
        {
            int sparseFactor = index.SparseFactor;
            int sparseIdx = (int)(newlinesNeeded / sparseFactor) - 1;
            if (sparseIdx >= index.SparseEntryCount)
                sparseIdx = index.SparseEntryCount - 1;
            if (sparseIdx >= 0)
            {
                startOffset = index.GetSparseOffset(sparseIdx) + minChar;
                newlinesCounted = (long)(sparseIdx + 1) * sparseFactor;
            }
        }

        long remaining = newlinesNeeded - newlinesCounted;
        if (remaining <= 0)
            return Math.Min(startOffset, _state.Document.Length);

        long pos = startOffset;
        EnsureScanBuffer(65536);
        long found = 0;
        while (pos < _state.Document.Length && found < remaining)
        {
            int readLen = (int)Math.Min(_scanBuffer.Length, _state.Document.Length - pos);
            Span<byte> buf = _scanBuffer.AsSpan(0, readLen);
            int read = _state.Document.Read(pos, buf);
            if (read == 0) break;

            if (minChar == 2)
            {
                int alignedLen = read & ~1;
                for (int i = 0; i + 1 < alignedLen; i += 2)
                {
                    if (buf[i] == 0x0A && buf[i + 1] == 0x00)
                    {
                        found++;
                        if (found >= remaining)
                            return pos + i + 2;
                    }
                }
            }
            else
            {
                for (int i = 0; i < read; i++)
                {
                    if (buf[i] == 0x0A)
                    {
                        found++;
                        if (found >= remaining)
                            return pos + i + 1;
                    }
                }
            }
            pos += read;
        }

        return Math.Min(pos, _state.Document.Length);
    }

    /// <summary>Checks if the byte before the given document offset is a newline.</summary>
    private bool IsNewlineBefore(long docOffset)
    {
        if (_state.Document is null || docOffset <= _state.BomLength) return true;

        int minChar = _state.Decoder.MinCharBytes;
        int readLen = (int)Math.Min(minChar, docOffset);
        Span<byte> buffer = stackalloc byte[2];
        _state.Document.Read(docOffset - readLen, buffer[..readLen]);
        return _state.Decoder.IsNewline(buffer[..readLen], 0, out _);
    }

    private void OnStateChanged()
    {
        EnsureCursorVisible();
        InvalidateVisual();
        StateChanged?.Invoke();
    }
}
