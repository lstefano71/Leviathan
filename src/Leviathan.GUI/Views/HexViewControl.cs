using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.GUI.Helpers;

using System.Runtime.CompilerServices;
using System.Text;

namespace Leviathan.GUI.Views;

/// <summary>
/// High-performance hex editor control. Renders [Address] [Hex bytes] | [ASCII]
/// via Avalonia DrawingContext — no XAML, pure custom rendering.
/// </summary>
internal sealed class HexViewControl : Control
{
    private const double LinePadding = 2;

    /// <summary>Distinctive marker color for bookmark indicators in the gutter.</summary>
    private static readonly IBrush BookmarkMarkerBrush = new SolidColorBrush(Color.FromArgb(220, 255, 120, 50));

    private readonly AppState _state;
    private readonly byte[] _readBuffer = new byte[65536];
    private ColorTheme _theme;

    internal Action? StateChanged;

    /// <summary>Scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? ScrollBar { get; set; }
    private bool _updatingScroll;
    private bool _scrollUpdateQueued;

    private Typeface? _cacheTypeface;
    private double _cacheFontSize;
    private IBrush? _cacheTextBrush;
    private IBrush? _cacheAsciiBrush;
    private IBrush? _cacheHeaderBrush;
    private IBrush? _cacheAddressBrush;
    private FormattedText[]? _hexByteTextCache;
    private FormattedText[]? _asciiByteTextCache;
    private FormattedText[]? _headerHexTextCache;
    private FormattedText[]? _headerAsciiTextCache;
    private readonly Dictionary<long, FormattedText> _addressTextCache = [];
    private bool _addressCacheDecimal;
    private int _addressCacheDigits;
    private double _cachedCharWidth;

    // Cached "Offset" header label — invalidated on font/brush/format change
    private FormattedText? _cachedOffsetLabel;
    private bool _cachedOffsetLabelDecimal;
    private IBrush? _cachedOffsetLabelBrush;

    // Pre-computed hex/ascii X position arrays
    private double[] _hexXPositions = [];
    private double[] _asciiXPositions = [];
    private int _xPositionsBytesPerRow;
    private double _xPositionsAddressWidth;
    private double _xPositionsAsciiX;
    private double _xPositionsCharWidth;

    // Reusable char buffers for row-level text batching
    private char[] _hexRowBuffer = new char[256];
    private char[] _asciiRowBuffer = new char[64];

    // Row-level FormattedText cache — keyed by row content hash (Phase 2)
    private readonly Dictionary<long, (int contentHash, FormattedText hexText, FormattedText asciiText)> _rowTextCache = new();
    private IBrush? _rowTextCacheTextBrush;
    private IBrush? _rowTextCacheAsciiBrush;
    private Typeface? _rowTextCacheTypeface;
    private double _rowTextCacheFontSize;

    // Cached header row FormattedText (hex + ASCII header lines)
    private FormattedText? _cachedHeaderHexLine;
    private FormattedText? _cachedHeaderAsciiLine;
    private int _cachedHeaderBytesPerRow;
    private double _cachedHeaderAddressWidth;

    /// <summary>Pre-computed ASCII lookup table: byte → display char ('.' for non-printable).</summary>
    private static readonly char[] AsciiLookup = InitAsciiLookup();

    private static char[] InitAsciiLookup()
    {
        char[] table = new char[256];
        for (int i = 0; i < 256; i++)
            table[i] = i >= 0x20 && i < 0x7F ? (char)i : '.';
        return table;
    }
    private bool _isPointerSelectionActive;
    private bool _pointerSelectionExtended;
    private bool _pointerSelectionStartedWithShift;
    private long _pointerSelectionStartOffset = -1;

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
        BuildContextMenu();
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

    private void BuildContextMenu()
    {
        MenuItem cut = new() { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
        cut.Click += (_, _) => CutRequested?.Invoke();

        MenuItem copy = new() { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += async (_, _) => await CopySelectionAsHexAsync();

        MenuItem copyAsText = new() { Header = "Copy as Text" };
        copyAsText.Click += async (_, _) => await CopySelectionAsTextAsync();

        MenuItem paste = new() { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
        paste.Click += (_, _) => PasteRequested?.Invoke();

        MenuItem selectAll = new() { Header = "Select All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAll.Click += (_, _) => SelectAllRequested?.Invoke();

        MenuItem addBookmark = new() { Header = "Add Bookmark" };
        addBookmark.Click += (_, _) => {
            long offset = _state.HexCursorOffset;
            if (offset >= 0) {
                _state.Bookmarks.Toggle(offset);
                InvalidateVisual();
                StateChanged?.Invoke();
            }
        };

        ContextMenu menu = new();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(copyAsText);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(addBookmark);
        menu.Opening += (_, _) => {
            bool hasSelection = _state.HexSelStart >= 0 && _state.HexSelEnd >= _state.HexSelStart;
            cut.IsEnabled = hasSelection && !_state.IsReadOnly;
            copy.IsEnabled = hasSelection;
            copyAsText.IsEnabled = hasSelection;
            paste.IsEnabled = !_state.IsReadOnly;

            long offset = _state.HexCursorOffset;
            bool hasBookmark = offset >= 0 && _state.Bookmarks.Contains(offset);
            addBookmark.Header = hasBookmark ? "Remove Bookmark" : "Add Bookmark";
        };
        ContextMenu = menu;
    }

    /// <summary>Fired when the user requests Cut from the context menu.</summary>
    internal Action? CutRequested;
    /// <summary>Fired when the user requests Paste from the context menu.</summary>
    internal Action? PasteRequested;
    /// <summary>Fired when the user requests Select All from the context menu.</summary>
    internal Action? SelectAllRequested;

    private async Task CopySelectionAsHexAsync()
    {
        if (!TryReadSelectionBytes(262144, out byte[] bytes))
            return;

        StringBuilder sb = new(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++) {
            if (i > 0)
                sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }

        TopLevel? root = TopLevel.GetTopLevel(this);
        if (root?.Clipboard is null)
            return;
        await root.Clipboard.SetTextAsync(sb.ToString());
    }

    private async Task CopySelectionAsTextAsync()
    {
        if (!TryReadSelectionBytes(262144, out byte[] bytes))
            return;

        Encoding encoding = _state.Decoder.Encoding switch {
            TextEncoding.Utf16Le => Encoding.Unicode,
            TextEncoding.Windows1252 => Encoding.Latin1,
            _ => Encoding.UTF8
        };

        TopLevel? root = TopLevel.GetTopLevel(this);
        if (root?.Clipboard is null)
            return;
        await root.Clipboard.SetTextAsync(encoding.GetString(bytes));
    }

    private bool TryReadSelectionBytes(int maxBytes, out byte[] bytes)
    {
        bytes = [];
        if (_state.Document is null)
            return false;

        long selectionStart = _state.HexSelStart;
        long selectionEnd = _state.HexSelEnd;
        if (selectionStart < 0 || selectionEnd < selectionStart)
            return false;

        int length = (int)Math.Min(selectionEnd - selectionStart + 1, maxBytes);
        if (length <= 0)
            return false;

        bytes = new byte[length];
        _state.Document.Read(selectionStart, bytes);
        return true;
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

        IBrush textBrush = theme.TextPrimary;
        IBrush addressBrush = theme.TextSecondary;
        IBrush asciiBrush = theme.TextMuted;
        IBrush selectionBrush = theme.SelectionHighlight;
        IBrush cursorBrush = theme.CursorHighlight;
        IBrush matchBrush = theme.MatchHighlight;
        IBrush activeMatchBrush = theme.ActiveMatchHighlight;

        // ── Fixed column header ──────────────────────────────────────
        IBrush headerBgBrush = theme.HeaderBackground;
        IBrush headerTextBrush = theme.HeaderText;

        EnsureTextCaches(textBrush, asciiBrush, headerTextBrush, addressBrush);
        double charWidth = MeasureCharWidth(headerTextBrush);

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

        context.FillRectangle(headerBgBrush, new Rect(0, 0, bounds.Width, headerHeight));

        // "Offset" label in address column (cached)
        if (gutterVisible) {
            FormattedText offsetLabel = GetOffsetLabel(decimalOffset, headerTextBrush);
            context.DrawText(offsetLabel, new Point(charWidth, 0));
        }

        // Hex + ASCII column headers (cached as full-line FormattedText)
        EnsureHeaderLines(bytesPerRow, addressWidth, asciiX, charWidth, headerTextBrush);
        context.DrawText(_cachedHeaderHexLine!, new Point(addressWidth, 0));
        context.DrawText(_cachedHeaderAsciiLine!, new Point(asciiX, 0));

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
        long hexCursorOffset = _state.HexCursorOffset;

        // Hoisted typeface/fontSize for row text construction
        Typeface typeface = _state.ContentTypeface;
        double fontSize = _state.ContentFontSize;

        List<SearchResult> matches = _state.SearchResults;
        int activeMatchIdx = _state.CurrentMatchIndex;

        // Binary-search for the first match visible in or after the viewport
        int matchCursor = SearchHighlightHelper.BinarySearchFirstMatch(matches, startOffset);

        // Hoist bookmarks outside the row loop (2C)
        IReadOnlyList<Leviathan.Core.DataModel.Bookmark> bookmarks = _state.Bookmarks.GetAll();

        // Pre-compute hex/ascii X position arrays (2E)
        EnsureXPositions(bytesPerRow, addressWidth, asciiX, charWidth);

        // Ensure row buffers are large enough
        int hexRowChars = bytesPerRow * 3 + (bytesPerRow + 7) / 8;
        if (_hexRowBuffer.Length < hexRowChars)
            _hexRowBuffer = new char[hexRowChars];
        if (_asciiRowBuffer.Length < bytesPerRow)
            _asciiRowBuffer = new char[bytesPerRow];

        // Invalidate row text cache on brush/font change
        if (_rowTextCacheTextBrush != textBrush || _rowTextCacheAsciiBrush != asciiBrush
            || _rowTextCacheTypeface != typeface || _rowTextCacheFontSize != fontSize) {
            _rowTextCache.Clear();
            _rowTextCacheTextBrush = textBrush;
            _rowTextCacheAsciiBrush = asciiBrush;
            _rowTextCacheTypeface = typeface;
            _rowTextCacheFontSize = fontSize;
        }

        for (int row = 0; row < visibleRows; row++) {
            long rowOffset = startOffset + (long)row * bytesPerRow;
            if (rowOffset >= _state.FileLength) break;

            double y = headerHeight + row * lineHeight;
            int rowStart = row * bytesPerRow;
            int rowBytes = Math.Min(bytesPerRow, readLen - rowStart);
            if (rowBytes <= 0) break;

            // Address column
            if (gutterVisible) {
                FormattedText addressText = GetAddressText(rowOffset, decimalOffset, addressDigits, addressBrush);
                context.DrawText(addressText, new Point(charWidth, y));

                // Bookmark marker: small circle in the gutter
                long rowEnd = rowOffset + bytesPerRow - 1;
                for (int bi = 0; bi < bookmarks.Count; bi++) {
                    long bmOff = bookmarks[bi].Offset;
                    if (bmOff > rowEnd) break;
                    if (bmOff >= rowOffset) {
                        double markerY = y + lineHeight / 2;
                        context.DrawEllipse(BookmarkMarkerBrush, null,
                            new Point(charWidth * 0.4, markerY), charWidth * 0.3, charWidth * 0.3);
                        break;
                    }
                }
            }

            // ── Hex + ASCII: background highlights (batched per run) ──
            int hexSelRunStart = -1;
            int asciiSelRunStart = -1;
            int hexMatchRunStart = -1;
            IBrush? hexMatchRunBrush = null;

            for (int col = 0; col < rowBytes; col++) {
                long byteOffset = rowOffset + col;
                double hexX = _hexXPositions[col];
                double ax = _asciiXPositions[col];

                // Match highlight (sliding cursor — O(1) amortized per byte)
                bool isMatch = false;
                bool isActiveMatch = false;
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

                // Match rects — merge adjacent (2B)
                if (isMatch) {
                    IBrush mBrush = isActiveMatch ? activeMatchBrush : matchBrush;
                    if (hexMatchRunStart < 0) {
                        hexMatchRunStart = col;
                        hexMatchRunBrush = mBrush;
                    } else if (hexMatchRunBrush != mBrush) {
                        FlushHexMatchRun(context, hexMatchRunBrush!, y, lineHeight, charWidth, hexMatchRunStart, col);
                        hexMatchRunStart = col;
                        hexMatchRunBrush = mBrush;
                    }
                } else if (hexMatchRunStart >= 0) {
                    FlushHexMatchRun(context, hexMatchRunBrush!, y, lineHeight, charWidth, hexMatchRunStart, col);
                    hexMatchRunStart = -1;
                    hexMatchRunBrush = null;
                }

                // Cursor highlight (always per-cell — a single byte)
                if (byteOffset == hexCursorOffset) {
                    context.FillRectangle(cursorBrush, new Rect(hexX, y, charWidth * 2, lineHeight));
                    context.FillRectangle(cursorBrush, new Rect(ax, y, charWidth, lineHeight));
                } else if (selStart >= 0 && byteOffset >= selStart && byteOffset <= selEnd) {
                    // Selection rects — merge adjacent (2B)
                    if (hexSelRunStart < 0) hexSelRunStart = col;
                    if (asciiSelRunStart < 0) asciiSelRunStart = col;
                } else {
                    // Flush selection runs
                    if (hexSelRunStart >= 0) {
                        double sx = _hexXPositions[hexSelRunStart];
                        double ex = hexX; // current col start = end of selection
                        context.FillRectangle(selectionBrush, new Rect(sx, y, ex - sx + charWidth * 2, lineHeight));
                        hexSelRunStart = -1;
                    }
                    if (asciiSelRunStart >= 0) {
                        double sx = _asciiXPositions[asciiSelRunStart];
                        context.FillRectangle(selectionBrush, new Rect(sx, y, ax - sx + charWidth, lineHeight));
                        asciiSelRunStart = -1;
                    }
                }
            }

            // Flush any pending runs at end of row
            if (hexMatchRunStart >= 0)
                FlushHexMatchRun(context, hexMatchRunBrush!, y, lineHeight, charWidth, hexMatchRunStart, rowBytes);
            if (hexSelRunStart >= 0) {
                double sx = _hexXPositions[hexSelRunStart];
                double ex = _hexXPositions[rowBytes - 1];
                context.FillRectangle(selectionBrush, new Rect(sx, y, ex - sx + charWidth * 2, lineHeight));
            }
            if (asciiSelRunStart >= 0) {
                double sx = _asciiXPositions[asciiSelRunStart];
                double ex = _asciiXPositions[rowBytes - 1];
                context.FillRectangle(selectionBrush, new Rect(sx, y, ex - sx + charWidth, lineHeight));
            }

            // ── Row-level text rendering (cached across frames) ──
            // Content hash: cheap hash of the row bytes for cache key
            int contentHash = ComputeRowContentHash(_readBuffer, rowStart, rowBytes);

            if (_rowTextCache.TryGetValue(rowOffset, out var cached) && cached.contentHash == contentHash) {
                // Cache hit — reuse existing FormattedText objects
                context.DrawText(cached.hexText, new Point(addressWidth, y));
                context.DrawText(cached.asciiText, new Point(asciiX, y));
            } else {
                // Cache miss — build strings and create FormattedText
                int hexPos = 0;
                for (int col = 0; col < rowBytes; col++) {
                    byte b = _readBuffer[rowStart + col];
                    _hexRowBuffer[hexPos++] = (char)HexChars[b >> 4];
                    _hexRowBuffer[hexPos++] = (char)HexChars[b & 0xF];
                    _hexRowBuffer[hexPos++] = ' ';
                    if ((col & 7) == 7 && col < rowBytes - 1)
                        _hexRowBuffer[hexPos++] = ' '; // group separator
                }

                FormattedText hexRowText = new(new string(_hexRowBuffer, 0, hexPos),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, fontSize, textBrush);
                context.DrawText(hexRowText, new Point(addressWidth, y));

                // Build ASCII row string using lookup table
                for (int col = 0; col < rowBytes; col++)
                    _asciiRowBuffer[col] = AsciiLookup[_readBuffer[rowStart + col]];

                FormattedText asciiRowText = new(new string(_asciiRowBuffer, 0, rowBytes),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, fontSize, asciiBrush);
                context.DrawText(asciiRowText, new Point(asciiX, y));

                _rowTextCache[rowOffset] = (contentHash, hexRowText, asciiRowText);
            }
        }

        // Evict row text cache when it grows too large
        if (_rowTextCache.Count > Math.Max(256, visibleRows * 4))
            _rowTextCache.Clear();

        QueueScrollBarUpdate();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double MeasureCharWidth(IBrush brush)
    {
        if (_cachedCharWidth > 0)
            return _cachedCharWidth;

        FormattedText measurement = new("0", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, brush);
        _cachedCharWidth = measurement.Width;
        return _cachedCharWidth;
    }

    private void EnsureTextCaches(IBrush textBrush, IBrush asciiBrush, IBrush headerBrush, IBrush addressBrush)
    {
        if (_hexByteTextCache is not null
            && _asciiByteTextCache is not null
            && _headerHexTextCache is not null
            && _headerAsciiTextCache is not null
            && Equals(_cacheTypeface, _state.ContentTypeface)
            && _cacheFontSize.Equals(_state.ContentFontSize)
            && ReferenceEquals(_cacheTextBrush, textBrush)
            && ReferenceEquals(_cacheAsciiBrush, asciiBrush)
            && ReferenceEquals(_cacheHeaderBrush, headerBrush)
            && ReferenceEquals(_cacheAddressBrush, addressBrush)) {
            return;
        }

        _cacheTypeface = _state.ContentTypeface;
        _cacheFontSize = _state.ContentFontSize;
        _cacheTextBrush = textBrush;
        _cacheAsciiBrush = asciiBrush;
        _cacheHeaderBrush = headerBrush;
        _cacheAddressBrush = addressBrush;
        _cachedCharWidth = 0;
        _addressTextCache.Clear();
        _rowTextCache.Clear();
        _cachedHeaderHexLine = null;
        _cachedHeaderAsciiLine = null;

        _hexByteTextCache = new FormattedText[256];
        _asciiByteTextCache = new FormattedText[256];
        for (int i = 0; i < 256; i++) {
            char hi = (char)HexChars[i >> 4];
            char lo = (char)HexChars[i & 0xF];
            string hexPair = new([hi, lo]);
            _hexByteTextCache[i] = new FormattedText(hexPair, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, textBrush);

            char ch = i >= 0x20 && i < 0x7F ? (char)i : '.';
            _asciiByteTextCache[i] = new FormattedText(ch.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, asciiBrush);
        }

        _headerHexTextCache = new FormattedText[256];
        for (int i = 0; i < 256; i++) {
            char hi = (char)HexChars[i >> 4];
            char lo = (char)HexChars[i & 0xF];
            string label = new([hi, lo]);
            _headerHexTextCache[i] = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerBrush);
        }

        _headerAsciiTextCache = new FormattedText[16];
        for (int i = 0; i < 16; i++) {
            char label = (char)HexChars[i];
            _headerAsciiTextCache[i] = new FormattedText(label.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerBrush);
        }
    }

    private FormattedText GetAddressText(long rowOffset, bool decimalOffset, int addressDigits, IBrush addressBrush)
    {
        if (_addressCacheDecimal != decimalOffset || _addressCacheDigits != addressDigits) {
            _addressCacheDecimal = decimalOffset;
            _addressCacheDigits = addressDigits;
            _addressTextCache.Clear();
        }

        if (_addressTextCache.TryGetValue(rowOffset, out FormattedText? cached))
            return cached;

        string address = decimalOffset
            ? rowOffset.ToString().PadLeft(addressDigits)
            : HexFormatter.FormatOffset(rowOffset, addressDigits);

        FormattedText created = new(address, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, addressBrush);

        _addressTextCache[rowOffset] = created;
        if (_addressTextCache.Count > Math.Max(256, _state.VisibleRows * 8))
            _addressTextCache.Clear();

        return created;
    }

    /// <summary>Returns a cached FormattedText for the "Offset" / "Offset (dec)" header label.</summary>
    private FormattedText GetOffsetLabel(bool decimalOffset, IBrush headerTextBrush)
    {
        if (_cachedOffsetLabel is not null && _cachedOffsetLabelDecimal == decimalOffset && _cachedOffsetLabelBrush == headerTextBrush)
            return _cachedOffsetLabel;

        string text = decimalOffset ? "Offset (dec)" : "Offset";
        _cachedOffsetLabel = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerTextBrush);
        _cachedOffsetLabelDecimal = decimalOffset;
        _cachedOffsetLabelBrush = headerTextBrush;
        return _cachedOffsetLabel;
    }

    /// <summary>Pre-computes hex and ASCII X position arrays to avoid per-byte arithmetic.</summary>
    private void EnsureXPositions(int bytesPerRow, double addressWidth, double asciiX, double charWidth)
    {
        if (_xPositionsBytesPerRow == bytesPerRow && _xPositionsAddressWidth == addressWidth
            && _xPositionsAsciiX == asciiX && _xPositionsCharWidth == charWidth)
            return;

        _xPositionsBytesPerRow = bytesPerRow;
        _xPositionsAddressWidth = addressWidth;
        _xPositionsAsciiX = asciiX;
        _xPositionsCharWidth = charWidth;

        if (_hexXPositions.Length < bytesPerRow)
            _hexXPositions = new double[bytesPerRow];
        if (_asciiXPositions.Length < bytesPerRow)
            _asciiXPositions = new double[bytesPerRow];

        for (int col = 0; col < bytesPerRow; col++) {
            int groupSep = col / 8;
            _hexXPositions[col] = addressWidth + (col * 3 + groupSep) * charWidth;
            _asciiXPositions[col] = asciiX + col * charWidth;
        }
    }

    /// <summary>Flushes a contiguous match-highlight run in the hex column.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushHexMatchRun(DrawingContext context, IBrush brush, double y, double lineHeight, double charWidth, int startCol, int endCol)
    {
        double sx = _hexXPositions[startCol];
        double ex = _hexXPositions[endCol - 1];
        context.FillRectangle(brush, new Rect(sx, y, ex - sx + charWidth * 2, lineHeight));
    }

    /// <summary>Builds and caches full-line header FormattedText for hex and ASCII columns.</summary>
    private void EnsureHeaderLines(int bytesPerRow, double addressWidth, double asciiX, double charWidth, IBrush headerTextBrush)
    {
        if (_cachedHeaderHexLine is not null && _cachedHeaderAsciiLine is not null
            && _cachedHeaderBytesPerRow == bytesPerRow && _cachedHeaderAddressWidth == addressWidth)
            return;

        _cachedHeaderBytesPerRow = bytesPerRow;
        _cachedHeaderAddressWidth = addressWidth;

        // Build hex header: "00 01 02 03 04 05 06 07  08 09 ..."
        int hexPos = 0;
        int hexChars = bytesPerRow * 3 + (bytesPerRow + 7) / 8;
        char[] hexBuf = hexChars <= _hexRowBuffer.Length ? _hexRowBuffer : new char[hexChars];
        for (int col = 0; col < bytesPerRow; col++) {
            hexBuf[hexPos++] = (char)HexChars[(col >> 4) & 0xF];
            hexBuf[hexPos++] = (char)HexChars[col & 0xF];
            hexBuf[hexPos++] = ' ';
            if ((col & 7) == 7 && col < bytesPerRow - 1)
                hexBuf[hexPos++] = ' ';
        }
        _cachedHeaderHexLine = new FormattedText(new string(hexBuf, 0, hexPos),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerTextBrush);

        // Build ASCII header: "0123456789ABCDEF0123..."
        char[] asciiBuf = bytesPerRow <= _asciiRowBuffer.Length ? _asciiRowBuffer : new char[bytesPerRow];
        for (int col = 0; col < bytesPerRow; col++)
            asciiBuf[col] = (char)HexChars[col & 0xF];
        _cachedHeaderAsciiLine = new FormattedText(new string(asciiBuf, 0, bytesPerRow),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, headerTextBrush);
    }

    /// <summary>Computes a fast content hash for a row of bytes in the read buffer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeRowContentHash(byte[] buffer, int start, int length)
    {
        // FNV-1a inspired hash — fast and good distribution for byte sequences
        uint hash = 2166136261;
        int end = start + length;
        for (int i = start; i < end; i++) {
            hash ^= buffer[i];
            hash *= 16777619;
        }
        return (int)hash;
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Point pos = e.GetPosition(this);
        long offset = HitTest(pos);
        if (offset >= 0) {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _state.HexSelectionAnchor = ResolvePointerSelectionAnchor(_state.HexSelectionAnchor, _state.HexCursorOffset, offset, shift);
            _state.HexCursorOffset = offset;
            _state.NibbleLow = false;
            _isPointerSelectionActive = true;
            _pointerSelectionExtended = false;
            _pointerSelectionStartedWithShift = shift;
            _pointerSelectionStartOffset = offset;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            StateChanged?.Invoke();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isPointerSelectionActive || _state.Document is null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Pointer.Captured != this)
            return;

        long offset = HitTestForPointerSelection(e.GetPosition(this));
        if (offset < 0 || offset == _state.HexCursorOffset)
            return;

        _state.HexCursorOffset = offset;
        _state.NibbleLow = false;
        _pointerSelectionExtended = _pointerSelectionExtended || offset != _pointerSelectionStartOffset;
        EnsureCursorVisible();
        e.Handled = true;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isPointerSelectionActive)
            return;

        bool pointerSelectionStartedWithShift = _pointerSelectionStartedWithShift;
        bool pointerSelectionExtended = _pointerSelectionExtended;

        if (e.Pointer.Captured == this)
            e.Pointer.Capture(null);

        _state.HexSelectionAnchor = ResolveSelectionAnchorAfterPointerRelease(
            _state.HexSelectionAnchor,
            pointerSelectionStartedWithShift,
            pointerSelectionExtended);

        _isPointerSelectionActive = false;
        _pointerSelectionExtended = false;
        _pointerSelectionStartedWithShift = false;
        _pointerSelectionStartOffset = -1;
        e.Handled = true;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isPointerSelectionActive = false;
        _pointerSelectionExtended = false;
        _pointerSelectionStartedWithShift = false;
        _pointerSelectionStartOffset = -1;
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

    /// <summary>Sets the top row from a byte anchor, used for linked-tab viewport sync.</summary>
    internal void SyncTopOffset(long offset)
    {
        if (_state.Document is null)
            return;

        long clamped = Math.Clamp(offset, 0, Math.Max(0, _state.FileLength - 1));
        int bytesPerRow = Math.Max(1, _state.BytesPerRow);
        long newBase = (clamped / bytesPerRow) * bytesPerRow;
        long maxBase = Math.Max(0, _state.FileLength - (long)_state.VisibleRows * bytesPerRow);
        _state.HexBaseOffset = Math.Clamp(newBase, 0, maxBase);
        _state.HexCursorOffset = clamped;
        _state.HexSelectionAnchor = -1;
        _state.NibbleLow = false;
        InvalidateVisual();
        StateChanged?.Invoke();
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
        double charWidth = MeasureCharWidth(_theme.HeaderText);
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

    private long HitTestForPointerSelection(Point point)
    {
        long offset = HitTest(point);
        if (offset >= 0 || _state.Document is null || _state.FileLength <= 0)
            return offset;

        double lineHeight = _state.ContentFontSize + LinePadding;
        if (point.Y < lineHeight)
            return Math.Clamp(_state.HexBaseOffset, 0, _state.FileLength - 1);

        int row = Math.Max(0, _state.VisibleRows - 1);
        long lastVisibleOffset = _state.HexBaseOffset + (long)row * _state.BytesPerRow;
        return Math.Clamp(lastVisibleOffset, 0, _state.FileLength - 1);
    }

    internal static long ResolvePointerSelectionAnchor(long currentAnchor, long currentCursor, long hitOffset, bool shiftPressed)
    {
        if (!shiftPressed)
            return hitOffset;

        if (currentAnchor >= 0)
            return currentAnchor;

        if (currentCursor >= 0)
            return currentCursor;

        return hitOffset;
    }

    internal static long ResolveSelectionAnchorAfterPointerRelease(long currentAnchor, bool shiftPressed, bool selectionExtended)
        => ShouldClearSelectionAfterPointerRelease(shiftPressed, selectionExtended) ? -1 : currentAnchor;

    internal static bool ShouldClearSelectionAfterPointerRelease(bool shiftPressed, bool selectionExtended)
        => !shiftPressed && !selectionExtended;

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
            _state.Document.BreakCoalescing();
            _state.Document.BeginUndoGroup(offset);
            _state.Document.Delete(offset, 1);
            _state.Document.Insert(offset, [value]);
            _state.Document.EndUndoGroup(offset);
        } else {
            _state.Document.BreakCoalescing();
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
