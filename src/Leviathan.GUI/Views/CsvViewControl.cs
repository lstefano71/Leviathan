using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Leviathan.Core.Csv;

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

    public CsvViewControl(AppState state)
    {
        _state = state;
        Focusable = true;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_state.Document is null || _state.CsvRowIndex is null) return;

        Rect bounds = Bounds;
        double charWidth = MeasureCharWidth();
        double lineHeight = FontSize + LinePadding;
        int[] colWidths = _state.CsvColumnWidths ?? [];
        int colCount = _state.CsvColumnCount;
        if (colCount == 0 || colWidths.Length == 0) return;

        int visibleRows = Math.Max(1, (int)((bounds.Height - lineHeight) / lineHeight)); // -1 for header
        CsvDialect dialect = _state.CsvDialect;

        // Brushes
        IBrush textBrush = Brushes.White;
        IBrush headerBrush = new SolidColorBrush(Color.FromRgb(200, 200, 255));
        IBrush headerBg = new SolidColorBrush(Color.FromArgb(40, 100, 100, 200));
        IBrush gridPen = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        IBrush cursorBrush = new SolidColorBrush(Color.FromArgb(80, 51, 153, 255));
        IBrush selectionBrush = new SolidColorBrush(Color.FromArgb(50, 51, 153, 255));
        IPen gridLinePen = new Pen(gridPen, 1);

        int hScroll = _state.CsvHorizontalScroll;

        // Draw header row
        if (dialect.HasHeader && _state.CsvHeaderNames.Length > 0)
        {
            context.FillRectangle(headerBg, new Rect(0, 0, bounds.Width, lineHeight));

            double hx = 0;
            for (int c = hScroll; c < colCount && hx < bounds.Width; c++)
            {
                double cellWidth = (colWidths[c] + 2) * charWidth + CellPaddingX;
                string headerText = c < _state.CsvHeaderNames.Length ? _state.CsvHeaderNames[c] : $"Col {c + 1}";
                if (headerText.Length > colWidths[c])
                    headerText = headerText[..colWidths[c]];

                FormattedText ft = new(headerText, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, FontSize, headerBrush);
                context.DrawText(ft, new Point(hx + CellPaddingX / 2, 0));

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

            // Cursor/selection highlight
            if (dataRow == _state.CsvCursorRow)
            {
                context.FillRectangle(cursorBrush, new Rect(0, y, bounds.Width, lineHeight));
            }
            else if (selStart >= 0 && dataRow >= selStart && dataRow <= selEnd)
            {
                context.FillRectangle(selectionBrush, new Rect(0, y, bounds.Width, lineHeight));
            }

            // Read row data from document — use sparse index estimation
            long adjustedRow = dataRow + (dialect.HasHeader ? 1 : 0);
            int sparseIdx = (int)(adjustedRow / _state.CsvRowIndex.SparseFactor);
            long rowOffset = sparseIdx < _state.CsvRowIndex.SparseEntryCount
                ? _state.CsvRowIndex.GetSparseOffset(sparseIdx)
                : -1;
            if (rowOffset < 0) continue;

            // Walk forward from sparse offset to exact row
            long targetWithinBlock = adjustedRow - (long)sparseIdx * _state.CsvRowIndex.SparseFactor;
            if (targetWithinBlock > 0)
            {
                int scanLen = (int)Math.Min(65536, _state.FileLength - rowOffset);
                if (scanLen > 0)
                {
                    _state.Document.Read(rowOffset, _readBuffer.AsSpan(0, scanLen));
                    int pos = 0;
                    long rowsSkipped = 0;
                    bool inQ = false;
                    byte q = dialect.Quote;
                    while (pos < scanLen && rowsSkipped < targetWithinBlock)
                    {
                        byte b = _readBuffer[pos];
                        if (inQ) { if (b == q) { if (pos + 1 < scanLen && _readBuffer[pos + 1] == q) { pos += 2; continue; } inQ = false; } pos++; continue; }
                        if (b == q && q != 0) { inQ = true; pos++; continue; }
                        if (b == (byte)'\n') { rowsSkipped++; pos++; continue; }
                        if (b == (byte)'\r') { rowsSkipped++; pos++; if (pos < scanLen && _readBuffer[pos] == (byte)'\n') pos++; continue; }
                        pos++;
                    }
                    rowOffset += pos;
                }
            }

            int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - rowOffset);
            if (readLen <= 0) continue;
            _state.Document.Read(rowOffset, _readBuffer.AsSpan(0, readLen));

            // Find end of row
            int rowLen = FindRowEnd(_readBuffer.AsSpan(0, readLen), dialect);
            ReadOnlySpan<byte> rowData = _readBuffer.AsSpan(0, rowLen);

            // Parse fields
            int fieldCount = CsvFieldParser.ParseRecord(rowData, dialect, fields);

            // Render cells
            double cellX = 0;
            for (int c = hScroll; c < colCount && cellX < bounds.Width; c++)
            {
                double cellWidth = (colWidths[c] + 2) * charWidth + CellPaddingX;

                if (c < fieldCount)
                {
                    int written = CsvFieldParser.UnescapeField(rowData, fields[c], dialect, unescaped);
                    string cellText = System.Text.Encoding.UTF8.GetString(unescaped[..written]);
                    if (cellText.Length > colWidths[c])
                        cellText = cellText[..colWidths[c]];

                    IBrush cellBrush = textBrush;
                    if (dataRow == _state.CsvCursorRow && c == _state.CsvCursorCol)
                    {
                        context.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 200, 50)),
                            new Rect(cellX, y, cellWidth, lineHeight));
                    }

                    FormattedText ft = new(cellText, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, MonoTypeface, FontSize, cellBrush);
                    context.DrawText(ft, new Point(cellX + CellPaddingX / 2, y));
                }

                cellX += cellWidth;
            }
        }
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
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
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
}
