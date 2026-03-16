using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
    private readonly byte[] _readBuffer = new byte[131072]; // 128 KB
    private readonly VisualLine[] _visualLines = new VisualLine[2048];
    private readonly LineWrapEngine _wrapEngine;
    private ViewTheme _theme = ViewTheme.Resolve();

    /// <summary>Scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? ScrollBar { get; set; }
    private bool _updatingScroll;

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
        long fileLen = _state.FileLength;
        long newOffset = (long)(e.NewValue / Math.Max(1, ScrollBar.Maximum) * fileLen);
        newOffset = Math.Clamp(newOffset, 0, Math.Max(0, fileLen - 1));
        _state.TextTopOffset = FindLineStart(newOffset);
        InvalidateVisual();
    }

    private void UpdateScrollBar()
    {
        if (_state.Document is null || ScrollBar is null) return;
        _updatingScroll = true;
        long fileLen = _state.FileLength;
        ScrollBar.Maximum = 10000;
        ScrollBar.Value = fileLen > 0 ? (double)_state.TextTopOffset / fileLen * 10000 : 0;
        long windowSize = _readBuffer.Length;
        ScrollBar.ViewportSize = fileLen > 0 ? (double)windowSize / fileLen * 10000 : 10000;
        _updatingScroll = false;
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

        // Read visible data
        long topOffset = _state.TextTopOffset;
        int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - topOffset);
        if (readLen <= 0) return;

        _state.Document.Read(topOffset, _readBuffer.AsSpan(0, readLen));
        ReadOnlySpan<byte> data = _readBuffer.AsSpan(0, readLen);

        // Compute visual lines
        double textAreaWidth = bounds.Width - gutterWidth;
        int columnsAvailable = _state.WordWrap ? Math.Max(1, (int)(textAreaWidth / charWidth)) : int.MaxValue;

        int lineCount = _wrapEngine.ComputeVisualLines(
            data, topOffset, columnsAvailable, _state.WordWrap, _visualLines.AsSpan(), _state.Decoder);
        lineCount = Math.Min(lineCount, visibleRows);

        // Brushes
        IBrush textBrush = theme.TextPrimary;
        IBrush gutterTextBrush = theme.TextSecondary;
        IBrush selectionBrush = theme.SelectionHighlight;
        IBrush cursorBrush = theme.CursorBar;

        // Selection range
        long selStart = _state.TextSelStart;
        long selEnd = _state.TextSelEnd;

        long currentLineNumber = EstimateLineNumber(topOffset);

        for (int row = 0; row < lineCount; row++)
        {
            VisualLine vl = _visualLines[row];
            double y = row * lineHeight;
            long lineAbsOffset = vl.DocOffset;

            // Detect hard line start: first row, or preceded by a newline
            bool isHardLine;
            if (row == 0)
            {
                isHardLine = topOffset == 0 || IsNewlineBefore(topOffset);
            }
            else
            {
                // Check if the byte just before this visual line is a newline
                long prevEnd = _visualLines[row - 1].DocOffset + _visualLines[row - 1].ByteLength;
                isHardLine = vl.DocOffset != prevEnd || IsNewlineAt(data, (int)(prevEnd - topOffset - 1));
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

        UpdateScrollBar();
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
        long newCursor = oldCursor;

        switch (e.Key)
        {
            case Key.Right:
                newCursor = Math.Min(oldCursor + _state.Decoder.MinCharBytes, _state.FileLength);
                break;
            case Key.Left:
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
                newCursor = ctrl ? _state.BomLength : FindLineStart(oldCursor);
                break;
            case Key.End:
                newCursor = ctrl ? _state.FileLength : FindLineEnd(oldCursor);
                break;
            case Key.Back:
                if (oldCursor > _state.BomLength)
                {
                    long deleteAt = Math.Max(oldCursor - _state.Decoder.MinCharBytes, _state.BomLength);
                    long deleteLen = oldCursor - deleteAt;
                    _state.Document.Delete(deleteAt, deleteLen);
                    newCursor = deleteAt;
                }
                break;
            case Key.Delete:
                if (oldCursor < _state.FileLength)
                {
                    _state.Document.Delete(oldCursor, _state.Decoder.MinCharBytes);
                }
                break;
            case Key.Enter:
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
        EnsureCursorVisible();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_state.Document is null || string.IsNullOrEmpty(e.Text)) return;

        byte[] encoded = _state.Decoder.EncodeString(e.Text);
        _state.Document.Insert(_state.TextCursorOffset, encoded);
        _state.TextCursorOffset += encoded.Length;
        EnsureCursorVisible();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
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
        if (_state.Document is null || _state.LineIndex is null) return;

        // Use sparse index: estimate the offset for the target line
        int sparseIdx = (int)(lineNumber / _state.LineIndex.SparseFactor);
        long offset = sparseIdx < _state.LineIndex.SparseEntryCount
            ? _state.LineIndex.GetSparseOffset(sparseIdx)
            : _state.BomLength;
        _state.TextCursorOffset = Math.Max(offset, _state.BomLength);
        _state.TextSelectionAnchor = -1;
        EnsureCursorVisible();
        InvalidateVisual();
    }

    private void EnsureCursorVisible()
    {
        long cursor = _state.TextCursorOffset;
        long topOffset = _state.TextTopOffset;
        long windowSize = _readBuffer.Length / 2;

        if (cursor < topOffset)
        {
            _state.TextTopOffset = FindLineStart(cursor);
        }
        else if (cursor > topOffset + windowSize)
        {
            _state.TextTopOffset = FindLineStart(Math.Max(0, cursor - windowSize / 2));
        }
    }

    private void ScrollByRows(int rowDelta)
    {
        long bytesPerRow = 80;
        long newTop = _state.TextTopOffset + rowDelta * bytesPerRow;
        newTop = Math.Clamp(newTop, 0, Math.Max(0, _state.FileLength - 1));
        _state.TextTopOffset = FindLineStart(newTop);
    }

    private long MoveDown(long offset)
    {
        long end = FindLineEnd(offset);
        if (end < _state.FileLength)
            return Math.Min(end + _state.Decoder.MinCharBytes, _state.FileLength);
        return offset;
    }

    private long MoveUp(long offset)
    {
        long lineStart = FindLineStart(offset);
        if (lineStart <= _state.BomLength) return _state.BomLength;
        return FindLineStart(lineStart - _state.Decoder.MinCharBytes);
    }

    private long FindLineStart(long offset)
    {
        if (_state.Document is null || offset <= _state.BomLength) return _state.BomLength;

        int scanSize = (int)Math.Min(4096, offset - _state.BomLength);
        long readFrom = offset - scanSize;
        Span<byte> buf = stackalloc byte[scanSize];
        _state.Document.Read(readFrom, buf);

        for (int i = scanSize - 1; i >= 0; i--)
        {
            if (buf[i] == (byte)'\n')
                return readFrom + i + 1;
        }

        return readFrom;
    }

    private long FindLineEnd(long offset)
    {
        if (_state.Document is null) return offset;

        int scanSize = (int)Math.Min(4096, _state.FileLength - offset);
        if (scanSize <= 0) return offset;

        Span<byte> buf = stackalloc byte[scanSize];
        _state.Document.Read(offset, buf);

        for (int i = 0; i < scanSize; i++)
        {
            if (buf[i] == (byte)'\n')
                return offset + i;
        }

        return offset + scanSize;
    }

    private long EstimateLineNumber(long offset)
    {
        if (_state.LineIndex is { } index)
            return index.EstimateLineForOffset(offset, _state.FileLength);

        return offset / 80;
    }

    /// <summary>Checks if the byte before the given document offset is a newline.</summary>
    private bool IsNewlineBefore(long docOffset)
    {
        if (_state.Document is null || docOffset <= 0) return true;
        Span<byte> b = stackalloc byte[1];
        _state.Document.Read(docOffset - 1, b);
        return b[0] == (byte)'\n' || b[0] == (byte)'\r';
    }

    /// <summary>Checks if the byte at the given index in the data span is a newline.</summary>
    private static bool IsNewlineAt(ReadOnlySpan<byte> data, int index)
    {
        if (index < 0 || index >= data.Length) return true;
        return data[index] == (byte)'\n' || data[index] == (byte)'\r';
    }
}
