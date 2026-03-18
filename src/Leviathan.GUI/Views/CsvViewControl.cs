using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using Leviathan.Core.Csv;
using Leviathan.Core.Search;
using Leviathan.GUI.Helpers;

using System.Runtime.CompilerServices;
using System.Text;

namespace Leviathan.GUI.Views;

/// <summary>
/// High-performance CSV grid view control. Renders a tabular grid with sticky header,
/// cell cursor, and scrolling via Avalonia DrawingContext.
/// </summary>
internal sealed class CsvViewControl : Control
{
    private const double LinePadding = 2;
    private const double CellPaddingX = 8;

    private readonly AppState _state;
    private readonly byte[] _readBuffer = new byte[65536];
    private ColorTheme _theme;

    internal Action? StateChanged;
    internal Action? OnRecordDetail;

    /// <summary>Vertical scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? VerticalScrollBar { get; set; }
    /// <summary>Horizontal scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? HorizontalScrollBar { get; set; }
    private bool _updatingScroll;
    private bool _scrollUpdateQueued;
    private bool _updatingHorizontalScroll;
    private bool _horizontalScrollUpdateQueued;
    private bool _hasHorizontalOverflow;
    private int _horizontalViewportColumns = 1;
    private int _horizontalMaxStartColumn;

    // Cached measurement (3A)
    private double _cachedCsvCharWidth;
    private double _cachedCsvBaseline;
    private double _cachedCsvMeasurementHeight;
    private Typeface _cachedCsvTypeface;
    private double _cachedCsvFontSize;

    // Cached header FormattedText (3B)
    private FormattedText[]? _cachedHeaderTexts;
    private string[]? _cachedHeaderNames;
    private int[]? _cachedHeaderColWidths;
    private IBrush? _cachedHeaderBrush;

    // Cached row-number FormattedText (3E)
    private readonly Dictionary<long, FormattedText> _rowNumberTextCache = new();
    private Typeface _rowNumCacheTypeface;
    private double _rowNumCacheFontSize;
    private IBrush? _rowNumCacheBrush;

    // Pre-computed column pixel positions and widths (3C)
    private double[] _colXPositions = [];
    private double[] _colPixelWidths = [];
    private int _colXHScroll = -1;
    private int _colXCount;
    private double _colXGutterWidth;
    private double _colXCharWidth;

    // Reusable char buffer for FormatCellPreview (3F)
    private char[] _cellPreviewBuffer = new char[256];

    public CsvViewControl(AppState state)
    {
        _state = state;
        _theme = state.ActiveTheme;
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => {
            _theme = _state.ActiveTheme;
            InvalidateVisual();
        };
        BuildContextMenu();
    }

    private void BuildContextMenu()
    {
        ContextMenu menu = new();

        MenuItem copyCellValue = new() { Header = "Copy Cell Value" };
        copyCellValue.Click += async (_, _) => await CopyCellValueAsync();

        MenuItem copyRowAsCsv = new() { Header = "Copy Row as CSV" };
        copyRowAsCsv.Click += async (_, _) => await CopyRowAsCsvAsync();

        MenuItem copySelectionAsCsv = new() { Header = "Copy Selection as CSV" };
        copySelectionAsCsv.Click += async (_, _) => await CopySelectionAsCsvAsync();

        menu.Items.Add(copyCellValue);
        menu.Items.Add(copyRowAsCsv);
        menu.Items.Add(copySelectionAsCsv);
        menu.Items.Add(new Separator());

        MenuItem hideCol = new() { Header = "Hide Column" };
        hideCol.Click += (_, _) => {
            int col = _state.CsvCursorCol;
            if (col >= 0 && col < _state.CsvColumnCount) {
                _state.CsvHiddenColumns.Add(col);
                InvalidateVisual();
                StateChanged?.Invoke();
            }
        };

        MenuItem showAll = new() { Header = "Show All Columns" };
        showAll.Click += (_, _) => {
            _state.CsvHiddenColumns.Clear();
            InvalidateVisual();
            StateChanged?.Invoke();
        };

        MenuItem colVis = new() { Header = "Column Visibility..." };
        colVis.Click += async (_, _) => {
            if (Avalonia.VisualTree.VisualExtensions.GetVisualRoot(this) is not Window owner) return;
            Widgets.ColumnVisibilityDialog dlg = new(_state);
            await dlg.ShowDialog(owner);
            if (dlg.Changed) {
                InvalidateVisual();
                StateChanged?.Invoke();
            }
        };

        menu.Items.Add(hideCol);
        menu.Items.Add(showAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(colVis);
        ContextMenu = menu;

        menu.Opening += (_, _) => {
            int col = _state.CsvCursorCol;
            string[] headers = _state.CsvHeaderNames;
            string colName = col >= 0 && col < headers.Length ? headers[col] : $"Column {col + 1}";
            hideCol.Header = $"Hide Column \"{colName}\"";

            bool hasRow = _state.CsvRowIndex is not null && _state.CsvCursorRow >= 0;
            copyCellValue.IsEnabled = hasRow;
            copyRowAsCsv.IsEnabled = hasRow;

            long selAnchor = _state.CsvSelectionAnchorRow;
            long cursorRow = _state.CsvCursorRow;
            bool hasMultiRowSelection = selAnchor >= 0 && selAnchor != cursorRow;
            copySelectionAsCsv.IsEnabled = hasMultiRowSelection;
        };
    }

    /// <summary>
    /// Copies the value of the current cell to the clipboard.
    /// </summary>
    internal async Task CopyCellValueAsync()
    {
        string? value = ReadCellValue(_state.CsvCursorRow, _state.CsvCursorCol);
        if (value is null) return;

        TopLevel? root = TopLevel.GetTopLevel(this);
        if (root?.Clipboard is null) return;
        await root.Clipboard.SetTextAsync(value);
    }

    /// <summary>
    /// Copies the current row as a CSV line to the clipboard.
    /// </summary>
    private async Task CopyRowAsCsvAsync()
    {
        string? row = ReadRowAsCsv(_state.CsvCursorRow);
        if (row is null) return;

        TopLevel? root = TopLevel.GetTopLevel(this);
        if (root?.Clipboard is null) return;
        await root.Clipboard.SetTextAsync(row);
    }

    /// <summary>
    /// Copies all selected rows as CSV text to the clipboard.
    /// </summary>
    private async Task CopySelectionAsCsvAsync()
    {
        long selAnchor = _state.CsvSelectionAnchorRow;
        long cursorRow = _state.CsvCursorRow;
        long startRow = selAnchor >= 0 ? Math.Min(selAnchor, cursorRow) : cursorRow;
        long endRow = selAnchor >= 0 ? Math.Max(selAnchor, cursorRow) : cursorRow;

        StringBuilder sb = new();
        for (long r = startRow; r <= endRow; r++) {
            string? row = ReadRowAsCsv(r);
            if (row is not null)
                sb.AppendLine(row);
        }

        if (sb.Length == 0) return;

        TopLevel? root = TopLevel.GetTopLevel(this);
        if (root?.Clipboard is null) return;
        await root.Clipboard.SetTextAsync(sb.ToString());
    }

    /// <summary>
    /// Reads the text value of a specific cell.
    /// </summary>
    internal string? ReadCellValue(long dataRow, int col)
    {
        if (_state.Document is null || _state.CsvRowIndex is null)
            return null;

        long rowOffset = GetRowByteOffset(dataRow);
        if (rowOffset < 0 || rowOffset >= _state.FileLength)
            return null;

        int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - rowOffset);
        if (readLen <= 0) return null;
        _state.Document.Read(rowOffset, _readBuffer.AsSpan(0, readLen));

        int rowLen = FindRowEnd(_readBuffer.AsSpan(0, readLen), _state.CsvDialect);
        ReadOnlySpan<byte> rowData = _readBuffer.AsSpan(0, rowLen);

        Span<CsvField> fields = stackalloc CsvField[256];
        int fieldCount = CsvFieldParser.ParseRecord(rowData, _state.CsvDialect, fields);

        if (col < 0 || col >= fieldCount)
            return null;

        Span<byte> unescaped = stackalloc byte[4096];
        int written = CsvFieldParser.UnescapeField(rowData, fields[col], _state.CsvDialect, unescaped);
        return Encoding.UTF8.GetString(unescaped[..written]);
    }

    /// <summary>
    /// Reads an entire row as raw CSV text.
    /// </summary>
    private string? ReadRowAsCsv(long dataRow)
    {
        if (_state.Document is null || _state.CsvRowIndex is null)
            return null;

        long rowOffset = GetRowByteOffset(dataRow);
        if (rowOffset < 0 || rowOffset >= _state.FileLength)
            return null;

        int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - rowOffset);
        if (readLen <= 0) return null;
        _state.Document.Read(rowOffset, _readBuffer.AsSpan(0, readLen));

        int rowLen = FindRowEnd(_readBuffer.AsSpan(0, readLen), _state.CsvDialect);
        return Encoding.UTF8.GetString(_readBuffer, 0, rowLen);
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
        if (_updatingScroll || _state.CsvRowIndex is null || VerticalScrollBar is null) return;
        long totalRows = _state.CsvRowIndex.TotalRowCount;
        long maxTop = Math.Max(0, totalRows - _state.VisibleRows);
        long newTop = (long)(e.NewValue / Math.Max(1, VerticalScrollBar.Maximum) * maxTop);
        _state.CsvTopRowIndex = Math.Clamp(newTop, 0, maxTop);
        InvalidateVisual();
    }

    private void UpdateScrollBar()
    {
        if (_state.CsvRowIndex is null || VerticalScrollBar is null) return;
        _updatingScroll = true;
        try {
            long totalRows = _state.CsvRowIndex.TotalRowCount;
            long maxTop = Math.Max(0, totalRows - _state.VisibleRows);
            VerticalScrollBar.Maximum = 10000;
            VerticalScrollBar.Value = maxTop > 0 ? (double)_state.CsvTopRowIndex / maxTop * 10000 : 0;
            VerticalScrollBar.ViewportSize = totalRows > 0 ? (double)_state.VisibleRows / totalRows * 10000 : 10000;
        } finally {
            _updatingScroll = false;
        }
    }

    internal void OnHorizontalScrollBarValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingHorizontalScroll || HorizontalScrollBar is null) return;
        int clamped = Math.Clamp((int)Math.Round(e.NewValue), 0, Math.Max(0, _state.CsvColumnCount - 1));
        if (clamped == _state.CsvHorizontalScroll) return;
        _state.CsvHorizontalScroll = clamped;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    private void UpdateHorizontalScrollBar()
    {
        if (HorizontalScrollBar is null) return;

        bool show = _hasHorizontalOverflow && _horizontalMaxStartColumn > 0;

        _updatingHorizontalScroll = true;
        try {
            HorizontalScrollBar.IsVisible = show;
            if (!show) {
                _state.CsvHorizontalScroll = 0;
                HorizontalScrollBar.Minimum = 0;
                HorizontalScrollBar.Maximum = 0;
                HorizontalScrollBar.Value = 0;
                HorizontalScrollBar.ViewportSize = 1;
                return;
            }

            _state.CsvHorizontalScroll = Math.Clamp(_state.CsvHorizontalScroll, 0, _horizontalMaxStartColumn);
            HorizontalScrollBar.Minimum = 0;
            HorizontalScrollBar.Maximum = _horizontalMaxStartColumn;
            HorizontalScrollBar.ViewportSize = Math.Max(1, Math.Min(_horizontalViewportColumns, _horizontalMaxStartColumn + 1));
            HorizontalScrollBar.SmallChange = 1;
            HorizontalScrollBar.LargeChange = Math.Max(1, _horizontalViewportColumns - 1);
            HorizontalScrollBar.Value = _state.CsvHorizontalScroll;
        } finally {
            _updatingHorizontalScroll = false;
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

    private void QueueHorizontalScrollBarUpdate()
    {
        if (_horizontalScrollUpdateQueued) return;
        _horizontalScrollUpdateQueued = true;
        Dispatcher.UIThread.Post(() => {
            _horizontalScrollUpdateQueued = false;
            UpdateHorizontalScrollBar();
        }, DispatcherPriority.Background);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_state.Document is null || _state.CsvRowIndex is null) return;

        Rect bounds = Bounds;
        ColorTheme theme = _theme;

        // Paint control background
        context.FillRectangle(theme.Background, bounds);

        // Cached measurement (3A)
        Typeface typeface = _state.ContentTypeface;
        double fontSize = _state.ContentFontSize;
        double charWidth;
        double textBaseline;
        double measureHeight;
        if (_cachedCsvCharWidth > 0 && _cachedCsvTypeface == typeface && _cachedCsvFontSize == fontSize) {
            charWidth = _cachedCsvCharWidth;
            textBaseline = _cachedCsvBaseline;
            measureHeight = _cachedCsvMeasurementHeight;
        } else {
            FormattedText measurement = CreateFormattedText("0", Brushes.White);
            charWidth = measurement.Width;
            measureHeight = measurement.Height;
            textBaseline = measurement.Baseline;
            _cachedCsvCharWidth = charWidth;
            _cachedCsvMeasurementHeight = measureHeight;
            _cachedCsvBaseline = textBaseline;
            _cachedCsvTypeface = typeface;
            _cachedCsvFontSize = fontSize;
        }
        double lineHeight = fontSize + LinePadding;
        textBaseline = Math.Max(0, (lineHeight - measureHeight) / 2) + textBaseline;

        // Row number gutter
        bool gutterVisible = _state.GutterVisible;
        long totalDataRows = _state.CsvRowIndex.TotalRowCount;
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(Math.Max(2, totalDataRows + 1))));
        double gutterWidth = gutterVisible ? (gutterDigits + 3) * charWidth : 0;

        // Draw gutter background
        if (gutterVisible) {
            context.FillRectangle(theme.GutterBackground, new Rect(0, 0, gutterWidth - charWidth, bounds.Height));
            context.DrawLine(theme.GutterPen, new Point(gutterWidth - charWidth, 0),
                new Point(gutterWidth - charWidth, bounds.Height));
        }

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
        IBrush matchBrush = theme.MatchHighlight;
        IBrush activeMatchBrush = theme.ActiveMatchHighlight;
        IBrush rowStripeBrush = theme.RowStripe;
        IBrush colStripeBrush = theme.ColumnStripe;
        IPen gridLinePen = theme.GridLinePen;
        List<SearchResult> matches = _state.SearchResults;
        int activeMatchIdx = _state.CurrentMatchIndex;
        HashSet<int> hiddenCols = _state.CsvHiddenColumns;
        UpdateHorizontalScrollMetrics(colWidths, colCount, bounds.Width - gutterWidth, charWidth, hiddenCols);

        int hScroll = _state.CsvHorizontalScroll;

        // Pre-compute column X positions and pixel widths (3C)
        EnsureColumnPositions(colWidths, colCount, hScroll, gutterWidth, charWidth, hiddenCols);

        // Ensure row-number cache is valid (3E)
        if (_rowNumCacheTypeface != typeface || _rowNumCacheFontSize != fontSize || _rowNumCacheBrush != theme.TextSecondary) {
            _rowNumberTextCache.Clear();
            _rowNumCacheTypeface = typeface;
            _rowNumCacheFontSize = fontSize;
            _rowNumCacheBrush = theme.TextSecondary;
        }

        // Draw column stripes as full-height rectangles once (3D)
        {
            int visIdx = 0;
            for (int c = hScroll; c < colCount; c++) {
                if (hiddenCols.Contains(c)) continue;
                if (visIdx >= _colPixelWidths.Length) break;
                double cx = _colXPositions[visIdx];
                if (cx >= bounds.Width) break;
                double cw = _colPixelWidths[visIdx];
                if (c % 2 == 0)
                    context.FillRectangle(colStripeBrush, new Rect(cx, 0, cw, bounds.Height));
                // Grid line
                context.DrawLine(gridLinePen, new Point(cx + cw, 0), new Point(cx + cw, bounds.Height));
                visIdx++;
            }
        }

        // Draw header row
        if (dialect.HasHeader && _state.CsvHeaderNames.Length > 0) {
            context.FillRectangle(headerBg, new Rect(0, 0, bounds.Width, lineHeight));

            // Ensure header text cache (3B)
            EnsureHeaderTextCache(colWidths, colCount, hScroll, headerBrush, hiddenCols);

            int visIdx = 0;
            for (int c = hScroll; c < colCount; c++) {
                if (hiddenCols.Contains(c)) continue;
                if (visIdx >= _colXPositions.Length) break;
                double hx = _colXPositions[visIdx];
                if (hx >= bounds.Width) break;

                if (_cachedHeaderTexts is not null && visIdx < _cachedHeaderTexts.Length) {
                    FormattedText ft = _cachedHeaderTexts[visIdx];
                    context.DrawText(ft, new Point(hx + CellPaddingX / 2, GetTextOriginY(0, textBaseline, ft)));
                }
                visIdx++;
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

        for (int rowIdx = 0; rowIdx < visibleRows; rowIdx++) {
            long dataRow = topRow + rowIdx;
            if (dataRow >= totalRows) break;

            double y = dataY + rowIdx * lineHeight;

            // Alternating row stripe (drawn first, behind everything)
            if (rowIdx % 2 == 0)
                context.FillRectangle(rowStripeBrush, new Rect(gutterWidth, y, bounds.Width - gutterWidth, lineHeight));

            // Row number in gutter (cached — 3E)
            if (gutterVisible) {
                FormattedText rowNumFt = GetOrCreateRowNumberText(dataRow + 1, theme.TextSecondary);
                string rowNumStr = (dataRow + 1).ToString();
                double rowNumX = gutterWidth - (rowNumStr.Length + 1) * charWidth;
                context.DrawText(rowNumFt, new Point(rowNumX, GetTextOriginY(y, textBaseline, rowNumFt)));
            }

            // Cursor/selection highlight
            if (dataRow == _state.CsvCursorRow) {
                context.FillRectangle(cursorBrush, new Rect(0, y, bounds.Width, lineHeight));
            } else if (selStart >= 0 && dataRow >= selStart && dataRow <= selEnd) {
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

            // Find first match overlapping this row for highlight rendering
            int rowMatchCursor = SearchHighlightHelper.BinarySearchFirstMatch(matches, rowOffset);

            // Render cells using pre-computed positions
            int visIdx = 0;
            for (int c = hScroll; c < colCount; c++) {
                if (hiddenCols.Contains(c)) continue;
                if (visIdx >= _colXPositions.Length) break;
                double cellX = _colXPositions[visIdx];
                if (cellX >= bounds.Width) break;
                double cellWidth = _colPixelWidths[visIdx];

                if (c < fieldCount) {
                    // Search match highlight for this cell
                    long cellStart = rowOffset + fields[c].Offset;
                    long cellEnd = cellStart + fields[c].Length - 1;
                    bool isMatch = false;
                    bool isActiveMatch = false;

                    int mc = rowMatchCursor;
                    while (mc < matches.Count) {
                        long mStart = matches[mc].Offset;
                        long mEnd = mStart + matches[mc].Length - 1;
                        if (mStart > cellEnd) break;
                        if (mEnd >= cellStart) {
                            isMatch = true;
                            isActiveMatch = mc == activeMatchIdx;
                            break;
                        }
                        mc++;
                    }

                    if (isMatch) {
                        context.FillRectangle(isActiveMatch ? activeMatchBrush : matchBrush,
                            new Rect(cellX, y, cellWidth, lineHeight));
                    }

                    int written = CsvFieldParser.UnescapeField(rowData, fields[c], dialect, unescaped);
                    string cellText = FormatCellPreview(Encoding.UTF8.GetString(unescaped[..written]), colWidths[c]);

                    if (dataRow == _state.CsvCursorRow && c == _state.CsvCursorCol) {
                        context.FillRectangle(theme.CursorHighlight,
                            new Rect(cellX, y, cellWidth, lineHeight));
                    }

                    FormattedText ft = CreateFormattedText(cellText, textBrush);
                    context.DrawText(ft, new Point(cellX + CellPaddingX / 2, GetTextOriginY(y, textBaseline, ft)));
                }

                visIdx++;
            }
        }

        // Evict stale row-number cache entries
        if (_rowNumberTextCache.Count > Math.Max(256, visibleRows * 8))
            _rowNumberTextCache.Clear();

        QueueScrollBarUpdate();
        QueueHorizontalScrollBarUpdate();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double MeasureCharWidth()
    {
        return CreateFormattedText("0", Brushes.White).Width;
    }

    private FormattedText CreateFormattedText(string text, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, brush);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double GetTextOriginY(double rowY, double baseline, FormattedText text) =>
        rowY + baseline - text.Baseline;

    /// <summary>Pre-computes column X positions and pixel widths for visible columns.</summary>
    private void EnsureColumnPositions(int[] colWidths, int colCount, int hScroll, double gutterWidth, double charWidth, HashSet<int> hiddenCols)
    {
        if (_colXHScroll == hScroll && _colXCount == colCount && _colXGutterWidth == gutterWidth && _colXCharWidth == charWidth)
            return;

        _colXHScroll = hScroll;
        _colXCount = colCount;
        _colXGutterWidth = gutterWidth;
        _colXCharWidth = charWidth;

        int maxVisible = colCount - hScroll;
        if (_colXPositions.Length < maxVisible) {
            _colXPositions = new double[maxVisible];
            _colPixelWidths = new double[maxVisible];
        }

        double x = gutterWidth;
        int idx = 0;
        for (int c = hScroll; c < colCount; c++) {
            if (hiddenCols.Contains(c)) continue;
            double cw = (colWidths[c] + 2) * charWidth + CellPaddingX;
            _colXPositions[idx] = x;
            _colPixelWidths[idx] = cw;
            x += cw;
            idx++;
        }
    }

    /// <summary>Caches FormattedText for header cells; invalidated on header name, width, or brush change.</summary>
    private void EnsureHeaderTextCache(int[] colWidths, int colCount, int hScroll, IBrush headerBrush, HashSet<int> hiddenCols)
    {
        bool needsRebuild = _cachedHeaderTexts is null || _cachedHeaderBrush != headerBrush
            || _cachedHeaderNames != _state.CsvHeaderNames || _cachedHeaderColWidths != colWidths;

        if (!needsRebuild) return;

        _cachedHeaderBrush = headerBrush;
        _cachedHeaderNames = _state.CsvHeaderNames;
        _cachedHeaderColWidths = colWidths;

        int maxVisible = colCount - hScroll;
        _cachedHeaderTexts = new FormattedText[maxVisible];

        int idx = 0;
        for (int c = hScroll; c < colCount; c++) {
            if (hiddenCols.Contains(c)) continue;
            if (idx >= maxVisible) break;

            string headerText = c < _state.CsvHeaderNames.Length ? _state.CsvHeaderNames[c] : $"Col {c + 1}";
            if (headerText.Length > colWidths[c])
                headerText = headerText[..colWidths[c]];

            _cachedHeaderTexts[idx] = CreateFormattedText(headerText, headerBrush);
            idx++;
        }
    }

    /// <summary>Returns a cached FormattedText for the given row number.</summary>
    private FormattedText GetOrCreateRowNumberText(long rowNumber, IBrush brush)
    {
        if (_rowNumberTextCache.TryGetValue(rowNumber, out FormattedText? cached))
            return cached;

        FormattedText created = CreateFormattedText(rowNumber.ToString(), brush);
        _rowNumberTextCache[rowNumber] = created;
        return created;
    }

    private void UpdateHorizontalScrollMetrics(
        int[] colWidths,
        int colCount,
        double viewportWidth,
        double charWidth,
        HashSet<int> hiddenCols)
    {
        double effectiveViewport = Math.Max(1, viewportWidth);
        double totalVisibleWidth = 0;
        int visibleColCount = 0;
        for (int c = 0; c < colCount; c++) {
            if (hiddenCols.Contains(c))
                continue;

            totalVisibleWidth += (colWidths[c] + 2) * charWidth + CellPaddingX;
            visibleColCount++;
        }

        _hasHorizontalOverflow = visibleColCount > 0 && totalVisibleWidth > effectiveViewport + 0.5;
        if (!_hasHorizontalOverflow) {
            _horizontalViewportColumns = Math.Max(1, visibleColCount);
            _horizontalMaxStartColumn = 0;
            return;
        }

        _horizontalMaxStartColumn = ComputeMaxHorizontalStartColumn(colWidths, colCount, hiddenCols, effectiveViewport, charWidth);
        _horizontalViewportColumns = Math.Max(1, EstimateVisibleColumnsFrom(_state.CsvHorizontalScroll, colWidths, colCount, hiddenCols, effectiveViewport, charWidth));
    }

    private static int ComputeMaxHorizontalStartColumn(
        int[] colWidths,
        int colCount,
        HashSet<int> hiddenCols,
        double viewportWidth,
        double charWidth)
    {
        int maxStart = 0;
        for (int start = 0; start < colCount; start++) {
            if (hiddenCols.Contains(start))
                continue;

            int visibleCount = EstimateVisibleColumnsFrom(start, colWidths, colCount, hiddenCols, viewportWidth, charWidth);
            if (visibleCount > 0)
                maxStart = start;
        }

        return maxStart;
    }

    private static int EstimateVisibleColumnsFrom(
        int start,
        int[] colWidths,
        int colCount,
        HashSet<int> hiddenCols,
        double viewportWidth,
        double charWidth)
    {
        double used = 0;
        int count = 0;
        for (int c = Math.Max(0, start); c < colCount; c++) {
            if (hiddenCols.Contains(c))
                continue;

            double cellWidth = (colWidths[c] + 2) * charWidth + CellPaddingX;
            if (count > 0 && used + cellWidth > viewportWidth)
                break;

            used += cellWidth;
            count++;

            if (cellWidth > viewportWidth)
                break;
        }

        return count;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_state.Document is null || _state.CsvRowIndex is null) { base.OnKeyDown(e); return; }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        long totalRows = _state.CsvRowIndex.TotalRowCount;
        int colCount = _state.CsvColumnCount;
        long oldRow = _state.CsvCursorRow;

        switch (e.Key) {
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
        if (shift) {
            if (_state.CsvSelectionAnchorRow < 0)
                _state.CsvSelectionAnchorRow = oldRow;
        } else {
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
        double lineHeight = _state.ContentFontSize + LinePadding;
        int[] colWidths = _state.CsvColumnWidths ?? [];
        if (colWidths.Length == 0) return;

        // Account for gutter
        long totalRows = _state.CsvRowIndex.TotalRowCount;
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(Math.Max(2, totalRows + 1))));
        double gutterWidth = _state.GutterVisible ? (gutterDigits + 3) * charWidth : 0;

        bool hasHeader = _state.CsvDialect.HasHeader;
        double dataY = hasHeader ? lineHeight : 0;

        int row = (int)((pos.Y - dataY) / lineHeight);
        long dataRow = _state.CsvTopRowIndex + row;
        if (dataRow < 0 || dataRow >= totalRows) return;

        // Determine column from X position (skip hidden columns to match render layout)
        double cellX = gutterWidth;
        int hScroll = _state.CsvHorizontalScroll;
        HashSet<int> hiddenCols = _state.CsvHiddenColumns;
        int clickedCol = -1;
        for (int c = hScroll; c < _state.CsvColumnCount; c++) {
            if (hiddenCols.Contains(c)) continue;
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

    /// <summary>Navigates to the CSV row and column containing the given byte offset.</summary>
    internal void GotoOffset(long byteOffset)
    {
        if (_state.CsvRowIndex is null || _state.Document is null || _state.CsvRowIndex.TotalRowCount <= 0)
            return;

        // Step 1: Find the precise row using sparse index + linear scan
        long targetRow = FindPreciseRowForOffset(byteOffset);
        targetRow = Math.Clamp(targetRow, 0, _state.CsvRowIndex.TotalRowCount - 1);
        _state.CsvCursorRow = targetRow;

        // Step 2: Determine which column the offset falls into
        long rowOffset = GetRowByteOffset(targetRow);
        if (rowOffset >= 0 && rowOffset < _state.FileLength) {
            int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - rowOffset);
            if (readLen > 0) {
                _state.Document.Read(rowOffset, _readBuffer.AsSpan(0, readLen));
                int rowLen = FindRowEnd(_readBuffer.AsSpan(0, readLen), _state.CsvDialect);
                Span<CsvField> fields = stackalloc CsvField[256];
                int fieldCount = CsvFieldParser.ParseRecord(_readBuffer.AsSpan(0, rowLen), _state.CsvDialect, fields);
                long localOffset = byteOffset - rowOffset;

                for (int c = 0; c < fieldCount; c++) {
                    if (localOffset >= fields[c].Offset && localOffset < fields[c].Offset + fields[c].Length) {
                        _state.CsvCursorCol = c;
                        break;
                    }
                }
            }
        }

        _state.CsvSelectionAnchorRow = -1;
        EnsureCursorVisible();
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    /// <summary>Sets the top row from a byte anchor, used for linked-tab viewport sync.</summary>
    internal void SyncTopOffset(long byteOffset)
    {
        if (_state.CsvRowIndex is null || _state.Document is null || _state.CsvRowIndex.TotalRowCount <= 0)
            return;

        long totalRows = _state.CsvRowIndex.TotalRowCount;
        long targetRow = Math.Clamp(FindPreciseRowForOffset(byteOffset), 0, totalRows - 1);
        long maxTop = Math.Max(0, totalRows - _state.VisibleRows);
        _state.CsvTopRowIndex = Math.Clamp(targetRow, 0, maxTop);
        _state.CsvCursorRow = targetRow;
        _state.CsvSelectionAnchorRow = -1;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Finds the precise 0-based data row for a byte offset by using the sparse index
    /// to get close, then linear-scanning forward to the exact row.
    /// </summary>
    private long FindPreciseRowForOffset(long byteOffset)
    {
        CsvRowIndex? index = _state.CsvRowIndex;
        if (index is null || _state.Document is null) return 0;

        CsvDialect dialect = _state.CsvDialect;
        long totalRows = index.TotalRowCount;

        // Binary search sparse entries to find the closest entry <= byteOffset
        int lo = 0, hi = index.SparseEntryCount - 1;
        while (lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            if (index.GetSparseOffset(mid) <= byteOffset)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        // hi is now the largest sparse index whose offset <= byteOffset (or -1 if none)
        long baseRow;
        long scanOffset;
        if (hi >= 0) {
            baseRow = (long)(hi + 1) * index.SparseFactor;
            scanOffset = index.GetSparseOffset(hi);
        } else {
            baseRow = 0;
            scanOffset = 0;
        }

        // Account for header row offset
        if (dialect.HasHeader && baseRow == 0) {
            scanOffset = index.FirstDataRowOffset > 0 ? index.FirstDataRowOffset : 0;
        }

        // Linear scan forward counting rows until we pass byteOffset
        long currentRow = baseRow;
        bool inQuoted = false;
        byte quote = dialect.Quote;

        while (scanOffset < byteOffset && currentRow < totalRows) {
            int toRead = (int)Math.Min(_readBuffer.Length, _state.FileLength - scanOffset);
            if (toRead <= 0) break;
            _state.Document.Read(scanOffset, _readBuffer.AsSpan(0, toRead));

            for (int i = 0; i < toRead; i++) {
                long absPos = scanOffset + i;
                if (absPos >= byteOffset) return Math.Max(0, currentRow - (dialect.HasHeader ? 1 : 0));

                byte b = _readBuffer[i];
                if (inQuoted) {
                    if (b == quote) {
                        if (i + 1 < toRead && _readBuffer[i + 1] == quote) { i++; continue; }
                        inQuoted = false;
                    }
                    continue;
                }
                if (b == quote && quote != 0) { inQuoted = true; continue; }
                if (b == (byte)'\n') {
                    currentRow++;
                } else if (b == (byte)'\r') {
                    if (i + 1 < toRead && _readBuffer[i + 1] == (byte)'\n') i++;
                    currentRow++;
                }
            }

            scanOffset += toRead;
        }

        // Convert from absolute row to data row (subtract header if present)
        long dataRow = dialect.HasHeader ? currentRow - 1 : currentRow;
        return Math.Clamp(dataRow, 0, totalRows - 1);
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

        // Fast path: if value fits and has no control chars, return as-is
        if (value.Length <= maxWidth) {
            bool hasSpecial = false;
            for (int i = 0; i < value.Length; i++) {
                char c = value[i];
                if (c == '\r' || c == '\n' || char.IsControl(c)) {
                    hasSpecial = true;
                    break;
                }
            }
            if (!hasSpecial) return value;
        }

        return FormatCellPreviewSlow(value, maxWidth);
    }

    private static string FormatCellPreviewSlow(string value, int maxWidth)
    {
        StringBuilder preview = new(Math.Min(value.Length, maxWidth + 1));
        for (int i = 0; i < value.Length && preview.Length < maxWidth + 1; i++) {
            char current = value[i];
            if (current == '\r') {
                if (i + 1 < value.Length && value[i + 1] == '\n')
                    i++;

                preview.Append('\u23CE');
                continue;
            }

            if (current == '\n') {
                preview.Append('\u23CE');
                continue;
            }

            if (char.IsControl(current)) {
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

        for (int i = 0; i < data.Length; i++) {
            byte b = data[i];
            if (inQuoted) {
                if (b == quote) {
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

        if (effectiveSparseIdx > 0) {
            offset = index.GetSparseOffset(effectiveSparseIdx - 1);
            rowsScanned = (long)effectiveSparseIdx * index.SparseFactor;
        } else if (actualRow == 1 && index.FirstDataRowOffset > 0) {
            return index.FirstDataRowOffset;
        } else {
            offset = 0;
            rowsScanned = 0;
        }

        // Linear scan from the sparse entry to the target row
        long remaining = actualRow - rowsScanned;
        if (remaining <= 0) return offset;

        long fileLen = _state.FileLength;
        bool inQuoted = false;
        byte quote = dialect.Quote;

        while (remaining > 0 && offset < fileLen) {
            int toRead = (int)Math.Min(_readBuffer.Length, fileLen - offset);
            if (toRead <= 0) break;
            _state.Document.Read(offset, _readBuffer.AsSpan(0, toRead));

            for (int i = 0; i < toRead && remaining > 0; i++) {
                byte b = _readBuffer[i];

                if (inQuoted) {
                    if (b == quote) {
                        if (i + 1 < toRead && _readBuffer[i + 1] == quote) { i++; continue; }
                        inQuoted = false;
                    }
                    continue;
                }

                if (b == quote && quote != 0) { inQuoted = true; continue; }

                if (b == (byte)'\n') {
                    remaining--;
                    if (remaining == 0) return offset + i + 1;
                } else if (b == (byte)'\r') {
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
