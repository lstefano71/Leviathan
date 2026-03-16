using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Leviathan.Core.Csv;
using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Views;

/// <summary>
/// High-performance CSV grid view control. Renders a tabular grid with sticky header,
/// cell cursor, and scrolling via Avalonia DrawingContext.
/// </summary>
internal sealed class CsvViewControl : Control
{
    private static readonly Typeface MonoTypeface = new("Consolas, Courier New, monospace");
    private const double FontSize = 14;
    private const double LinePadding = 2;
    private const double CellPaddingX = 8;

    private readonly AppState _state;
    private readonly byte[] _readBuffer = new byte[65536];
    private ViewTheme _theme = ViewTheme.Resolve();

    internal Action? StateChanged;
    internal Action? OnRecordDetail;

    /// <summary>Scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? ScrollBar { get; set; }
    private bool _updatingScroll;
    private bool _scrollUpdateQueued;

    public CsvViewControl(AppState state)
    {
        _state = state;
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
        if (_updatingScroll || _state.CsvRowIndex is null || ScrollBar is null) return;
        long totalRows = _state.CsvRowIndex.TotalRowCount;
        long maxTop = Math.Max(0, totalRows - _state.VisibleRows);
        long newTop = (long)(e.NewValue / Math.Max(1, ScrollBar.Maximum) * maxTop);
        _state.CsvTopRowIndex = Math.Clamp(newTop, 0, maxTop);
        InvalidateVisual();
    }

    private void UpdateScrollBar()
    {
        if (_state.CsvRowIndex is null || ScrollBar is null) return;
        _updatingScroll = true;
        try
        {
            long totalRows = _state.CsvRowIndex.TotalRowCount;
            long maxTop = Math.Max(0, totalRows - _state.VisibleRows);
            ScrollBar.Maximum = 10000;
            ScrollBar.Value = maxTop > 0 ? (double)_state.CsvTopRowIndex / maxTop * 10000 : 0;
            ScrollBar.ViewportSize = totalRows > 0 ? (double)_state.VisibleRows / totalRows * 10000 : 10000;
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

        if (_state.Document is null || _state.CsvRowIndex is null) return;

        Rect bounds = Bounds;
        ViewTheme theme = _theme;

        // Paint control background
        context.FillRectangle(theme.Background, bounds);

        FormattedText measurement = CreateFormattedText("0", Brushes.White);
        double charWidth = measurement.Width;
        double lineHeight = FontSize + LinePadding;
        double textBaseline = Math.Max(0, (lineHeight - measurement.Height) / 2) + measurement.Baseline;

        // Row number gutter
        long totalDataRows = _state.CsvRowIndex.TotalRowCount;
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(Math.Max(2, totalDataRows + 1))));
        double gutterWidth = (gutterDigits + 2) * charWidth;

        // Draw gutter background
        context.FillRectangle(theme.GutterBackground, new Rect(0, 0, gutterWidth - charWidth, bounds.Height));
        context.DrawLine(theme.GutterPen, new Point(gutterWidth - charWidth, 0),
            new Point(gutterWidth - charWidth, bounds.Height));

        int[] colWidths = _state.CsvColumnWidths ?? [];
        int colCount = _state.CsvColumnCount;
        if (colCount == 0 || colWidths.Length == 0) return;

        int visibleRows = Math.Max(1, (int)((bounds.Height - lineHeight) / lineHeight)); // -1 for header
        _state.VisibleRows = visibleRows;
        CsvDialect dialect = _state.CsvDialect;

        // Brushes
        IBrush textBrush = theme.TextPrimary;
        IBrush headerBrush = theme.HeaderText;
        IBrush headerBg = theme.HeaderBackground;
        IBrush cursorBrush = theme.SelectionHighlight;
        IBrush selectionBrush = theme.SelectionHighlight;
        IPen gridLinePen = theme.GridLinePen;

        int hScroll = _state.CsvHorizontalScroll;

        // Draw header row
        if (dialect.HasHeader && _state.CsvHeaderNames.Length > 0)
        {
            context.FillRectangle(headerBg, new Rect(0, 0, bounds.Width, lineHeight));

            double hx = gutterWidth;
            for (int c = hScroll; c < colCount && hx < bounds.Width; c++)
            {
                double cellWidth = (colWidths[c] + 2) * charWidth + CellPaddingX;
                string headerText = c < _state.CsvHeaderNames.Length ? _state.CsvHeaderNames[c] : $"Col {c + 1}";
                if (headerText.Length > colWidths[c])
                    headerText = headerText[..colWidths[c]];

                FormattedText ft = CreateFormattedText(headerText, headerBrush);
                context.DrawText(ft, new Point(hx + CellPaddingX / 2, GetTextOriginY(0, textBaseline, ft)));

                hx += cellWidth;
                context.DrawLine(gridLinePen, new Point(hx, 0), new Point(hx, bounds.Height));
            }

            // Header separator
            context.DrawLine(gridLinePen, new Point(0, lineHeight), new Point(bounds.Width, lineHeight));
        }

        // Draw data rows
        double dataY = dialect.HasHeader ? lineHeight : 0;
        long topRow = _state.CsvTopRowIndex;
        long totalRows = _state.CsvRowIndex.TotalRowCount;

        // Selection range
        long selAnchor = _state.CsvSelectionAnchorRow;
        long selStart = selAnchor < 0 ? -1 : Math.Min(_state.CsvCursorRow, selAnchor);
        long selEnd = selAnchor < 0 ? -1 : Math.Max(_state.CsvCursorRow, selAnchor);

        // Pre-allocate outside the loop to avoid CA2014 stackalloc-in-loop
        Span<CsvField> fields = stackalloc CsvField[256];
        Span<byte> unescaped = stackalloc byte[1024];

        for (int rowIdx = 0; rowIdx < visibleRows; rowIdx++)
        {
            long dataRow = topRow + rowIdx;
            if (dataRow >= totalRows) break;

            double y = dataY + rowIdx * lineHeight;

            // Row number in gutter
            string rowNumStr = (dataRow + 1).ToString();
            double rowNumX = gutterWidth - (rowNumStr.Length + 1) * charWidth;
            FormattedText rowNumFt = CreateFormattedText(rowNumStr, theme.TextSecondary);
            context.DrawText(rowNumFt, new Point(rowNumX, GetTextOriginY(y, textBaseline, rowNumFt)));

            // Cursor/selection highlight
            if (dataRow == _state.CsvCursorRow)
            {
                context.FillRectangle(cursorBrush, new Rect(0, y, bounds.Width, lineHeight));
            }
            else if (selStart >= 0 && dataRow >= selStart && dataRow <= selEnd)
            {
                context.FillRectangle(selectionBrush, new Rect(0, y, bounds.Width, lineHeight));
            }

            // Navigate to the row using the correct sparse index lookup
            long rowOffset = GetRowByteOffset(dataRow);
            if (rowOffset < 0 || rowOffset >= _state.FileLength) continue;

            int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - rowOffset);
            if (readLen <= 0) continue;
            _state.Document.Read(rowOffset, _readBuffer.AsSpan(0, readLen));

            // Find end of row
            int rowLen = FindRowEnd(_readBuffer.AsSpan(0, readLen), dialect);
            ReadOnlySpan<byte> rowData = _readBuffer.AsSpan(0, rowLen);

            // Parse fields
            int fieldCount = CsvFieldParser.ParseRecord(rowData, dialect, fields);

            // Render cells
            double cellX = gutterWidth;
            for (int c = hScroll; c < colCount && cellX < bounds.Width; c++)
            {
                double cellWidth = (colWidths[c] + 2) * charWidth + CellPaddingX;

                if (c < fieldCount)
                {
                    int written = CsvFieldParser.UnescapeField(rowData, fields[c], dialect, unescaped);
                    string cellText = FormatCellPreview(Encoding.UTF8.GetString(unescaped[..written]), colWidths[c]);

                    IBrush cellBrush = textBrush;
                    if (dataRow == _state.CsvCursorRow && c == _state.CsvCursorCol)
                    {
                        context.FillRectangle(theme.CursorHighlight,
                            new Rect(cellX, y, cellWidth, lineHeight));
                    }

                    FormattedText ft = CreateFormattedText(cellText, cellBrush);
                    context.DrawText(ft, new Point(cellX + CellPaddingX / 2, GetTextOriginY(y, textBaseline, ft)));
                }

                cellX += cellWidth;
            }
        }

        QueueScrollBarUpdate();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double MeasureCharWidth()
    {
        return CreateFormattedText("0", Brushes.White).Width;
    }

    private static FormattedText CreateFormattedText(string text, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, MonoTypeface, FontSize, brush);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetTextOriginY(double rowY, double baseline, FormattedText text) =>
        rowY + baseline - text.Baseline;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_state.Document is null || _state.CsvRowIndex is null) { base.OnKeyDown(e); return; }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        long totalRows = _state.CsvRowIndex.TotalRowCount;
        int colCount = _state.CsvColumnCount;
        long oldRow = _state.CsvCursorRow;

        switch (e.Key)
        {
            case Key.Down:
                _state.CsvCursorRow = Math.Min(oldRow + 1, totalRows - 1);
                break;
            case Key.Up:
                _state.CsvCursorRow = Math.Max(oldRow - 1, 0);
                break;
            case Key.Right:
                _state.CsvCursorCol = Math.Min(_state.CsvCursorCol + 1, colCount - 1);
                break;
            case Key.Left:
                _state.CsvCursorCol = Math.Max(_state.CsvCursorCol - 1, 0);
                break;
            case Key.PageDown:
                _state.CsvCursorRow = Math.Min(oldRow + _state.VisibleRows, totalRows - 1);
                break;
            case Key.PageUp:
                _state.CsvCursorRow = Math.Max(oldRow - _state.VisibleRows, 0);
                break;
            case Key.Home:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    _state.CsvCursorRow = 0;
                else
                    _state.CsvCursorCol = 0;
                break;
            case Key.End:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    _state.CsvCursorRow = totalRows - 1;
                else
                    _state.CsvCursorCol = colCount - 1;
                break;
            case Key.F2:
                OnRecordDetail?.Invoke();
                e.Handled = true;
                return;
            case Key.Tab:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _state.CsvCursorCol = Math.Max(_state.CsvCursorCol - 1, 0);
                else
                    _state.CsvCursorCol = Math.Min(_state.CsvCursorCol + 1, colCount - 1);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        // Selection
        if (shift)
        {
            if (_state.CsvSelectionAnchorRow < 0)
                _state.CsvSelectionAnchorRow = oldRow;
        }
        else
        {
            _state.CsvSelectionAnchorRow = -1;
        }

        EnsureCursorVisible();
        e.Handled = true;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_state.Document is null || _state.CsvRowIndex is null) return;

        Point pos = e.GetPosition(this);
        double charWidth = MeasureCharWidth();
        double lineHeight = FontSize + LinePadding;
        int[] colWidths = _state.CsvColumnWidths ?? [];
        if (colWidths.Length == 0) return;

        // Account for gutter
        long totalRows = _state.CsvRowIndex.TotalRowCount;
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(Math.Max(2, totalRows + 1))));
        double gutterWidth = (gutterDigits + 2) * charWidth;

        bool hasHeader = _state.CsvDialect.HasHeader;
        double dataY = hasHeader ? lineHeight : 0;

        int row = (int)((pos.Y - dataY) / lineHeight);
        long dataRow = _state.CsvTopRowIndex + row;
        if (dataRow < 0 || dataRow >= totalRows) return;

        // Determine column from X position
        double cellX = gutterWidth;
        int hScroll = _state.CsvHorizontalScroll;
        int clickedCol = -1;
        for (int c = hScroll; c < _state.CsvColumnCount; c++)
        {
            double cellWidth = (colWidths[c] + 2) * charWidth + CellPaddingX;
            if (pos.X >= cellX && pos.X < cellX + cellWidth) { clickedCol = c; break; }
            cellX += cellWidth;
        }

        _state.CsvCursorRow = dataRow;
        if (clickedCol >= 0) _state.CsvCursorCol = clickedCol;
        _state.CsvSelectionAnchorRow = -1;
        EnsureCursorVisible();
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_state.CsvRowIndex is null) return;

        int rows = e.Delta.Y > 0 ? -3 : 3;
        long totalRows = _state.CsvRowIndex.TotalRowCount;
        _state.CsvTopRowIndex = Math.Clamp(_state.CsvTopRowIndex + rows, 0, Math.Max(0, totalRows - _state.VisibleRows));
        InvalidateVisual();
    }

    /// <summary>Navigates to the CSV row containing the given byte offset.</summary>
    internal void GotoOffset(long byteOffset)
    {
        if (_state.CsvRowIndex is null || _state.Document is null || _state.CsvRowIndex.TotalRowCount <= 0)
            return;

        long estimatedRow = _state.CsvRowIndex.EstimateRowForOffset(byteOffset, _state.Document.Length);
        // EstimateRowForOffset returns a 0-based data row count; clamp to valid range
        long targetRow = Math.Clamp(estimatedRow, 0, _state.CsvRowIndex.TotalRowCount - 1);
        _state.CsvCursorRow = targetRow;
        _state.CsvSelectionAnchorRow = -1;
        EnsureCursorVisible();
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    internal void GotoRow(long rowNumber)
    {
        if (_state.CsvRowIndex is null || rowNumber < 1 || _state.CsvRowIndex.TotalRowCount <= 0)
            return;

        long targetRow = Math.Clamp(rowNumber - 1, 0, _state.CsvRowIndex.TotalRowCount - 1);
        _state.CsvCursorRow = targetRow;
        _state.CsvSelectionAnchorRow = -1;
        EnsureCursorVisible();
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    private void EnsureCursorVisible()
    {
        long cursor = _state.CsvCursorRow;
        long top = _state.CsvTopRowIndex;
        int visible = _state.VisibleRows;

        if (cursor < top)
            _state.CsvTopRowIndex = cursor;
        else if (cursor >= top + visible)
            _state.CsvTopRowIndex = cursor - visible + 1;

        // Horizontal
        int cursorCol = _state.CsvCursorCol;
        if (cursorCol < _state.CsvHorizontalScroll)
            _state.CsvHorizontalScroll = cursorCol;
        // approximate visible columns
        else if (cursorCol >= _state.CsvHorizontalScroll + 8)
            _state.CsvHorizontalScroll = cursorCol - 7;
    }

    private static string FormatCellPreview(string value, int maxWidth)
    {
        if (string.IsNullOrEmpty(value) || maxWidth <= 0)
            return string.Empty;

        StringBuilder preview = new(Math.Min(value.Length, maxWidth + 1));
        for (int i = 0; i < value.Length && preview.Length < maxWidth + 1; i++)
        {
            char current = value[i];
            if (current == '\r')
            {
                if (i + 1 < value.Length && value[i + 1] == '\n')
                    i++;

                preview.Append('\u23CE');
                continue;
            }

            if (current == '\n')
            {
                preview.Append('\u23CE');
                continue;
            }

            if (char.IsControl(current))
            {
                preview.Append('\u00B7');
                continue;
            }

            preview.Append(current);
        }

        if (preview.Length <= maxWidth)
            return preview.ToString();

        preview.Length = Math.Max(0, maxWidth - 1);
        preview.Append('\u2026');
        return preview.ToString();
    }

    private static int FindRowEnd(ReadOnlySpan<byte> data, CsvDialect dialect)
    {
        bool inQuoted = false;
        byte quote = dialect.Quote;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            if (inQuoted)
            {
                if (b == quote)
                {
                    if (i + 1 < data.Length && data[i + 1] == quote) { i++; continue; }
                    inQuoted = false;
                }
                continue;
            }
            if (b == quote && quote != 0) { inQuoted = true; continue; }
            if (b == (byte)'\n' || b == (byte)'\r') return i;
        }

        return data.Length;
    }

    /// <summary>
    /// Returns the byte offset of the start of <paramref name="dataRowIndex"/> (0-based data row).
    /// Mirrors the proven TUI2 logic: correct sparse index mapping with clamping,
    /// fallback to offset 0, and quoted-field state preserved across buffer boundaries.
    /// </summary>
    internal long GetRowByteOffset(long dataRowIndex)
    {
        if (_state.Document is null) return -1;

        CsvRowIndex? index = _state.CsvRowIndex;
        if (index is null) return -1;

        CsvDialect dialect = _state.CsvDialect;
        long actualRow = dialect.HasHeader ? dataRowIndex + 1 : dataRowIndex;

        if (actualRow == 0) return 0;
        if (actualRow > index.TotalRowCount) return -1;

        // Use sparse index to get close, clamping to last available entry
        int sparseIdx = (int)((actualRow - 1) / index.SparseFactor);
        int effectiveSparseIdx = Math.Min(sparseIdx, index.SparseEntryCount);
        long offset;
        long rowsScanned;

        if (effectiveSparseIdx > 0)
        {
            offset = index.GetSparseOffset(effectiveSparseIdx - 1);
            rowsScanned = (long)effectiveSparseIdx * index.SparseFactor;
        }
        else if (actualRow == 1 && index.FirstDataRowOffset > 0)
        {
            return index.FirstDataRowOffset;
        }
        else
        {
            offset = 0;
            rowsScanned = 0;
        }

        // Linear scan from the sparse entry to the target row
        long remaining = actualRow - rowsScanned;
        if (remaining <= 0) return offset;

        long fileLen = _state.FileLength;
        bool inQuoted = false;
        byte quote = dialect.Quote;

        while (remaining > 0 && offset < fileLen)
        {
            int toRead = (int)Math.Min(_readBuffer.Length, fileLen - offset);
            if (toRead <= 0) break;
            _state.Document.Read(offset, _readBuffer.AsSpan(0, toRead));

            for (int i = 0; i < toRead && remaining > 0; i++)
            {
                byte b = _readBuffer[i];

                if (inQuoted)
                {
                    if (b == quote)
                    {
                        if (i + 1 < toRead && _readBuffer[i + 1] == quote) { i++; continue; }
                        inQuoted = false;
                    }
                    continue;
                }

                if (b == quote && quote != 0) { inQuoted = true; continue; }

                if (b == (byte)'\n')
                {
                    remaining--;
                    if (remaining == 0) return offset + i + 1;
                }
                else if (b == (byte)'\r')
                {
                    if (i + 1 < toRead && _readBuffer[i + 1] == (byte)'\n') i++;
                    remaining--;
                    if (remaining == 0) return offset + i + 1;
                }
            }

            offset += toRead;
        }

        return offset;
    }
}
