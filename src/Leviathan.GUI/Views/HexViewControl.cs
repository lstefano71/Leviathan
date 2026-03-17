using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using Leviathan.Core.Search;
using Leviathan.GUI.Helpers;

using System.Runtime.CompilerServices;

namespace Leviathan.GUI.Views;

/// <summary>
/// High-performance hex editor control. Renders [Address] [Hex bytes] | [ASCII]
/// via Avalonia DrawingContext — no XAML, pure custom rendering.
/// </summary>
internal sealed class HexViewControl : Control
{
    private const double LinePadding = 2;

    private readonly AppState _state;
    private readonly byte[] _readBuffer = new byte[65536];
    private ColorTheme _theme;

    internal Action? StateChanged;

    /// <summary>Scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? ScrollBar { get; set; }
    private bool _updatingScroll;
    private bool _scrollUpdateQueued;

    /// <summary>Lookup table for zero-alloc byte-to-hex conversion.</summary>
    private static ReadOnlySpan<byte> HexChars => "0123456789ABCDEF"u8;

    public HexViewControl(AppState state)
    {
        _state = state;
        _theme = state.ActiveTheme;
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => {
            _theme = _state.ActiveTheme;
            InvalidateVisual();
        };
    }

    /// <summary>
    /// Applies the current theme and font from AppState, then repaints.
    /// Called by MainWindow when the user switches themes or fonts.
    /// </summary>
    internal void ApplyThemeAndFont()
    {
        _theme = _state.ActiveTheme;
        InvalidateVisual();
    }

    internal void OnScrollBarValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingScroll || _state.Document is null || ScrollBar is null) return;
        long totalRows = (_state.FileLength + _state.BytesPerRow - 1) / _state.BytesPerRow;
        long row = (long)(e.NewValue / Math.Max(1, ScrollBar.Maximum) * Math.Max(0, totalRows - _state.VisibleRows));
        long newBase = row * _state.BytesPerRow;
        newBase = Math.Clamp(newBase, 0, Math.Max(0, _state.FileLength - (long)_state.VisibleRows * _state.BytesPerRow));
        _state.HexBaseOffset = newBase;
        InvalidateVisual();
    }

    private void UpdateScrollBar()
    {
        if (_state.Document is null || ScrollBar is null) return;
        _updatingScroll = true;
        try {
            long totalRows = (_state.FileLength + _state.BytesPerRow - 1) / _state.BytesPerRow;
            long currentRow = _state.HexBaseOffset / Math.Max(1, _state.BytesPerRow);
            long maxRow = Math.Max(0, totalRows - _state.VisibleRows);
            ScrollBar.Maximum = 10000;
            ScrollBar.Value = maxRow > 0 ? (double)currentRow / maxRow * 10000 : 0;
            ScrollBar.ViewportSize = totalRows > 0 ? (double)_state.VisibleRows / totalRows * 10000 : 10000;
        } finally {
            _updatingScroll = false;
        }
    }

    private void QueueScrollBarUpdate()
    {
        if (_scrollUpdateQueued) return;
        _scrollUpdateQueued = true;
        Dispatcher.UIThread.Post(() => {
            _scrollUpdateQueued = false;
            UpdateScrollBar();
        }, DispatcherPriority.Background);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_state.Document is null) return;

        Rect bounds = Bounds;
        ColorTheme theme = _theme;

        // Paint control background
        context.FillRectangle(theme.Background, bounds);

        double charWidth = MeasureCharWidth();

        // Auto-fit bytes per row when setting is 0 (Auto)
        if (_state.BytesPerRowSetting == 0) {
            int autoFit = _state.ComputeBytesPerRow(bounds.Width, charWidth);
            if (autoFit != _state.BytesPerRow)
                _state.BytesPerRow = autoFit;
        }

        double lineHeight = _state.ContentFontSize + LinePadding;
        double headerHeight = lineHeight;

        int bytesPerRow = _state.BytesPerRow;
        int visibleRows = Math.Max(1, (int)((bounds.Height - headerHeight) / lineHeight));
        _state.VisibleRows = visibleRows;

        // Determine address column width (grows for large files)
        bool decimalOffset = _state.HexOffsetDecimal;
        bool gutterVisible = _state.GutterVisible;
        int addressDigits;
        if (decimalOffset)
            addressDigits = Math.Max(8, (int)Math.Ceiling(Math.Log10(Math.Max(2, _state.FileLength + 1))));
        else
            addressDigits = _state.FileLength > 0xFFFF_FFFFL ? 16 : 8;
        double addressWidth = gutterVisible ? (addressDigits + 3) * charWidth : 0;

        // Draw gutter background + separator (matching Text/CSV views)
        if (gutterVisible) {
            double gutterSepX = addressWidth - charWidth;
            context.FillRectangle(theme.GutterBackground, new Rect(0, 0, gutterSepX, bounds.Height));
            context.DrawLine(theme.GutterPen, new Point(gutterSepX, 0), new Point(gutterSepX, bounds.Height));
        }

        // Hex column: 3 chars per byte (XX + space), extra space every 8 bytes
        int groupCount = (bytesPerRow + 7) / 8;
        double hexWidth = (bytesPerRow * 3 + groupCount) * charWidth;

        // Separator
        double separatorX = addressWidth + hexWidth;

        // ASCII column
        double asciiX = separatorX + 3 * charWidth;

        // ── Fixed column header ──────────────────────────────────────
        IBrush headerBgBrush = theme.HeaderBackground;
        IBrush headerTextBrush = theme.HeaderText;

        context.FillRectangle(headerBgBrush, new Rect(0, 0, bounds.Width, headerHeight));

        // "Offset" label in address column
        if (gutterVisible) {
            string offsetLabelText = decimalOffset ? "Offset (dec)" : "Offset";
            FormattedText offsetLabel = new(offsetLabelText, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerTextBrush);
            context.DrawText(offsetLabel, new Point(charWidth, 0));
        }

        // Hex column offsets: 00 01 02 … 0F (matching byte positions)
        for (int col = 0; col < bytesPerRow; col++) {
            int groupSep = col / 8;
            double hexX = addressWidth + (col * 3 + groupSep) * charWidth;

            char hi = (char)HexChars[col >> 4];
            char lo = (char)HexChars[col & 0xF];
            string label = new([hi, lo]);

            FormattedText headerHex = new(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerTextBrush);
            context.DrawText(headerHex, new Point(hexX, 0));
        }

        // ASCII column offsets: 0123456789ABCDEF (single char per column)
        for (int col = 0; col < bytesPerRow; col++) {
            double ax = asciiX + col * charWidth;
            char label = (char)HexChars[col & 0xF];

            FormattedText headerAscii = new(label.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerTextBrush);
            context.DrawText(headerAscii, new Point(ax, 0));
        }

        // Header / data separator line
        IPen separatorPen = theme.GridLinePen;
        context.DrawLine(separatorPen, new Point(0, headerHeight), new Point(bounds.Width, headerHeight));

        // Vertical separator between hex and ASCII columns
        context.DrawLine(separatorPen, new Point(separatorX + charWidth, 0),
            new Point(separatorX + charWidth, bounds.Height));

        // ── Data rows (offset by header height) ──────────────────────
        long startOffset = _state.HexBaseOffset;
        int totalBytes = visibleRows * bytesPerRow;
        int maxRead = (int)Math.Min(totalBytes, _state.FileLength - startOffset);
        if (maxRead <= 0) {
            QueueScrollBarUpdate();
            return;
        }

        int readLen = Math.Min(maxRead, _readBuffer.Length);
        _state.Document.Read(startOffset, _readBuffer.AsSpan(0, readLen));

        // Selection range
        long selStart = _state.HexSelStart;
        long selEnd = _state.HexSelEnd;

        IBrush textBrush = theme.TextPrimary;
        IBrush addressBrush = theme.TextSecondary;
        IBrush asciiBrush = theme.TextMuted;
        IBrush selectionBrush = theme.SelectionHighlight;
        IBrush cursorBrush = theme.CursorHighlight;
        IBrush matchBrush = theme.MatchHighlight;
        IBrush activeMatchBrush = theme.ActiveMatchHighlight;
        List<SearchResult> matches = _state.SearchResults;
        int activeMatchIdx = _state.CurrentMatchIndex;

        // Binary-search for the first match visible in or after the viewport
        int matchCursor = SearchHighlightHelper.BinarySearchFirstMatch(matches, startOffset);

        for (int row = 0; row < visibleRows; row++) {
            long rowOffset = startOffset + (long)row * bytesPerRow;
            if (rowOffset >= _state.FileLength) break;

            double y = headerHeight + row * lineHeight;
            int rowStart = row * bytesPerRow;
            int rowBytes = Math.Min(bytesPerRow, readLen - rowStart);
            if (rowBytes <= 0) break;

            // Address column
            if (gutterVisible) {
                string address = decimalOffset
                    ? rowOffset.ToString().PadLeft(addressDigits)
                    : (addressDigits == 16 ? rowOffset.ToString("X16") : rowOffset.ToString("X8"));
                FormattedText addressText = new(address, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, addressBrush);
                context.DrawText(addressText, new Point(charWidth, y));
            }

            // Hex bytes
            for (int col = 0; col < rowBytes; col++) {
                byte b = _readBuffer[rowStart + col];
                long byteOffset = rowOffset + col;
                int groupSep = col / 8;
                double hexX = addressWidth + (col * 3 + groupSep) * charWidth;

                // Match highlight (sliding cursor — O(1) amortized per byte)
                bool isActiveMatch = false;
                bool isMatch = false;
                while (matchCursor < matches.Count) {
                    long mStart = matches[matchCursor].Offset;
                    long mEnd = mStart + matches[matchCursor].Length - 1;
                    if (byteOffset > mEnd) {
                        matchCursor++;
                        continue;
                    }
                    if (byteOffset >= mStart) {
                        isMatch = true;
                        isActiveMatch = matchCursor == activeMatchIdx;
                    }
                    break;
                }
                if (isMatch) {
                    context.FillRectangle(isActiveMatch ? activeMatchBrush : matchBrush,
                        new Rect(hexX, y, charWidth * 2, lineHeight));
                }

                // Selection/cursor highlight
                if (byteOffset == _state.HexCursorOffset) {
                    context.FillRectangle(cursorBrush,
                        new Rect(hexX, y, charWidth * 2, lineHeight));
                } else if (selStart >= 0 && byteOffset >= selStart && byteOffset <= selEnd) {
                    context.FillRectangle(selectionBrush,
                        new Rect(hexX, y, charWidth * 2, lineHeight));
                }

                char hi = (char)HexChars[b >> 4];
                char lo = (char)HexChars[b & 0xF];
                string hexPair = new([hi, lo]);

                FormattedText hexText = new(hexPair, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, textBrush);
                context.DrawText(hexText, new Point(hexX, y));
            }

            // ASCII column
            for (int col = 0; col < rowBytes; col++) {
                byte b = _readBuffer[rowStart + col];
                long byteOffset = rowOffset + col;
                double ax = asciiX + col * charWidth;

                // Selection/cursor highlight in ASCII
                if (byteOffset == _state.HexCursorOffset) {
                    context.FillRectangle(cursorBrush,
                        new Rect(ax, y, charWidth, lineHeight));
                } else if (selStart >= 0 && byteOffset >= selStart && byteOffset <= selEnd) {
                    context.FillRectangle(selectionBrush,
                        new Rect(ax, y, charWidth, lineHeight));
                }

                char ch = b >= 0x20 && b < 0x7F ? (char)b : '.';
                FormattedText asciiText = new(ch.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, asciiBrush);
                context.DrawText(asciiText, new Point(ax, y));
            }
        }

        QueueScrollBarUpdate();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double MeasureCharWidth()
    {
        FormattedText measurement = new("0", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, Brushes.White);
        return measurement.Width;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_state.Document is null) { base.OnKeyDown(e); return; }

        long fileLen = _state.FileLength;
        int bytesPerRow = _state.BytesPerRow;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        long oldCursor = _state.HexCursorOffset;
        long newCursor = oldCursor;

        switch (e.Key) {
            case Key.Right:
                newCursor = Math.Min(oldCursor + 1, fileLen - 1);
                _state.NibbleLow = false;
                break;
            case Key.Left:
                newCursor = Math.Max(oldCursor - 1, 0);
                _state.NibbleLow = false;
                break;
            case Key.Down:
                newCursor = Math.Min(oldCursor + bytesPerRow, fileLen - 1);
                break;
            case Key.Up:
                newCursor = Math.Max(oldCursor - bytesPerRow, 0);
                break;
            case Key.PageDown:
                newCursor = Math.Min(oldCursor + (long)_state.VisibleRows * bytesPerRow, fileLen - 1);
                break;
            case Key.PageUp:
                newCursor = Math.Max(oldCursor - (long)_state.VisibleRows * bytesPerRow, 0);
                break;
            case Key.Home:
                newCursor = ctrl ? 0 : oldCursor - (oldCursor % bytesPerRow);
                break;
            case Key.End:
                newCursor = ctrl ? fileLen - 1 : Math.Min(oldCursor - (oldCursor % bytesPerRow) + bytesPerRow - 1, fileLen - 1);
                break;
            default:
                // Hex digit editing
                int hexDigit = GetHexDigit(e.Key);
                if (hexDigit >= 0 && _state.HexCursorOffset >= 0) {
                    if (_state.IsReadOnly) {
                        e.Handled = true;
                        return;
                    }
                    InsertHexNibble(hexDigit);
                    e.Handled = true;
                    InvalidateVisual();
                    return;
                }
                base.OnKeyDown(e);
                return;
        }

        // Selection handling
        if (shift) {
            if (_state.HexSelectionAnchor < 0)
                _state.HexSelectionAnchor = oldCursor;
        } else {
            _state.HexSelectionAnchor = -1;
        }

        _state.HexCursorOffset = newCursor;
        EnsureCursorVisible();
        e.Handled = true;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (_state.Document is null) return;

        Point pos = e.GetPosition(this);
        long offset = HitTest(pos);
        if (offset >= 0) {
            _state.HexCursorOffset = offset;
            _state.HexSelectionAnchor = -1;
            _state.NibbleLow = false;
            InvalidateVisual();
            StateChanged?.Invoke();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_state.Document is null) return;

        int rows = e.Delta.Y > 0 ? -3 : 3;
        long newBase = _state.HexBaseOffset + (long)rows * _state.BytesPerRow;
        newBase = Math.Max(0, newBase);
        newBase = Math.Min(newBase, Math.Max(0, _state.FileLength - (long)_state.VisibleRows * _state.BytesPerRow));
        // Align to row boundary
        newBase -= newBase % _state.BytesPerRow;
        _state.HexBaseOffset = newBase;
        InvalidateVisual();
    }

    /// <summary>Navigates to a specific byte offset.</summary>
    internal void GotoOffset(long offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _state.FileLength - 1));
        _state.HexCursorOffset = offset;
        _state.HexSelectionAnchor = -1;
        _state.NibbleLow = false;
        EnsureCursorVisible();
        InvalidateVisual();
    }

    private void EnsureCursorVisible()
    {
        long cursor = _state.HexCursorOffset;
        int bytesPerRow = _state.BytesPerRow;
        long baseOffset = _state.HexBaseOffset;
        int visibleRows = _state.VisibleRows;

        if (cursor < baseOffset) {
            _state.HexBaseOffset = (cursor / bytesPerRow) * bytesPerRow;
        } else if (cursor >= baseOffset + (long)visibleRows * bytesPerRow) {
            _state.HexBaseOffset = ((cursor / bytesPerRow) - visibleRows + 1) * bytesPerRow;
        }
    }

    private long HitTest(Point point)
    {
        double charWidth = MeasureCharWidth();
        double lineHeight = _state.ContentFontSize + LinePadding;
        double headerHeight = lineHeight;
        int bytesPerRow = _state.BytesPerRow;

        // Click in the header row — ignore
        if (point.Y < headerHeight) return -1;

        int row = (int)((point.Y - headerHeight) / lineHeight);
        if (row < 0 || row >= _state.VisibleRows) return -1;

        int addressDigits;
        if (_state.HexOffsetDecimal)
            addressDigits = Math.Max(8, (int)Math.Ceiling(Math.Log10(Math.Max(2, _state.FileLength + 1))));
        else
            addressDigits = _state.FileLength > 0xFFFF_FFFFL ? 16 : 8;
        double addressWidth = _state.GutterVisible ? (addressDigits + 3) * charWidth : 0;

        double hexX = point.X - addressWidth;
        if (hexX >= 0) {
            int groupCount = (bytesPerRow + 7) / 8;
            double totalHexWidth = (bytesPerRow * 3 + groupCount) * charWidth;

            if (hexX < totalHexWidth) {
                // Hit in hex area — approximate column
                int approxCol = (int)(hexX / (3 * charWidth));
                approxCol = Math.Clamp(approxCol, 0, bytesPerRow - 1);
                long offset = _state.HexBaseOffset + (long)row * bytesPerRow + approxCol;
                return Math.Min(offset, _state.FileLength - 1);
            }
        }

        return _state.HexBaseOffset + (long)row * bytesPerRow;
    }

    private void InsertHexNibble(int digit)
    {
        if (_state.Document is null || _state.HexCursorOffset < 0) return;

        long offset = _state.HexCursorOffset;
        Span<byte> current = stackalloc byte[1];
        if (offset < _state.FileLength)
            _state.Document.Read(offset, current);
        else
            current[0] = 0;

        byte value;
        if (!_state.NibbleLow) {
            value = (byte)((digit << 4) | (current[0] & 0x0F));
            _state.NibbleLow = true;
        } else {
            value = (byte)((current[0] & 0xF0) | digit);
            _state.NibbleLow = false;
        }

        if (offset < _state.FileLength) {
            _state.Document.Delete(offset, 1);
            _state.Document.Insert(offset, [value]);
        } else {
            _state.Document.Insert(offset, [value]);
        }

        _state.InvalidateSearchResults();

        if (!_state.NibbleLow) {
            _state.HexCursorOffset = Math.Min(offset + 1, _state.Document.Length - 1);
        }
    }

    private static int GetHexDigit(Key key) => key switch {
        Key.D0 or Key.NumPad0 => 0,
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D8 or Key.NumPad8 => 8,
        Key.D9 or Key.NumPad9 => 9,
        Key.A => 10,
        Key.B => 11,
        Key.C => 12,
        Key.D => 13,
        Key.E => 14,
        Key.F => 15,
        _ => -1
    };
}
