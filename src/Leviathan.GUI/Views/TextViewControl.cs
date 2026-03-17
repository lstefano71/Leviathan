using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using Leviathan.Core.DataModel;
using Leviathan.Core.Indexing;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.GUI.Helpers;

using System.Runtime.CompilerServices;

namespace Leviathan.GUI.Views;

/// <summary>
/// High-performance text editor control. Uses LineWrapEngine for JIT visual-line
/// computation within the viewport, rendering via Avalonia DrawingContext.
/// </summary>
internal sealed class TextViewControl : Control
{
    private const double LinePadding = 2;

    /// <summary>Distinctive marker color for bookmark indicators in the gutter.</summary>
    private static readonly IBrush BookmarkMarkerBrush = new SolidColorBrush(Color.FromArgb(220, 255, 120, 50));

    private readonly AppState _state;
    private byte[] _readBuffer = new byte[131072]; // 128 KB initial, grows up to 16 MB
    private byte[] _scanBuffer = new byte[4096];
    private VisualLine[] _visualLines = new VisualLine[2048];
    private VisualLine[] _navVisualLines = new VisualLine[256];
    private readonly LineWrapEngine _wrapEngine;
    private const int MaxReadSize = 16 * 1024 * 1024; // 16 MB max buffer
    private ColorTheme _theme;
    private int _lastRenderedLineCount;
    private int _desiredColumn = -1;
    private int _userScrollFrames;
    private int _lastTextAreaCols;
    private long _cachedTopOffset;
    private long _cachedTopLineNumber = 1;
    private bool _alignViewportToEnd;
    private bool _isPointerSelectionActive;
    private bool _pointerSelectionExtended;
    private bool _pointerSelectionStartedWithShift;
    private long _pointerSelectionStartOffset = -1;

    internal Action? StateChanged;

    /// <summary>Vertical scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? VerticalScrollBar { get; set; }
    /// <summary>Horizontal scrollbar exposed for composition in MainWindow.</summary>
    internal Avalonia.Controls.Primitives.ScrollBar? HorizontalScrollBar { get; set; }
    private bool _updatingScroll;
    private bool _scrollUpdateQueued;
    private bool _updatingHorizontalScroll;
    private bool _horizontalScrollUpdateQueued;
    private bool _hasHorizontalOverflow;
    private int _horizontalViewportColumns;
    private int _horizontalContentColumns;

    public TextViewControl(AppState state)
    {
        _state = state;
        _theme = state.ActiveTheme;
        _wrapEngine = new LineWrapEngine(state.TabWidth);
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => {
            _theme = _state.ActiveTheme;
            InvalidateVisual();
        };
        BuildContextMenu();
    }

    /// <summary>Fired when the user requests Cut from the context menu.</summary>
    internal Action? CutRequested;
    /// <summary>Fired when the user requests Copy from the context menu.</summary>
    internal Action? CopyRequested;
    /// <summary>Fired when the user requests Paste from the context menu.</summary>
    internal Action? PasteRequested;
    /// <summary>Fired when the user requests Select All from the context menu.</summary>
    internal Action? SelectAllRequested;

    private void BuildContextMenu()
    {
        MenuItem cut = new() { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
        cut.Click += (_, _) => CutRequested?.Invoke();

        MenuItem copy = new() { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => CopyRequested?.Invoke();

        MenuItem paste = new() { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
        paste.Click += (_, _) => PasteRequested?.Invoke();

        MenuItem selectAll = new() { Header = "Select All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAll.Click += (_, _) => SelectAllRequested?.Invoke();

        MenuItem addBookmark = new() { Header = "Add Bookmark" };
        addBookmark.Click += (_, _) => {
            long offset = _state.TextCursorOffset;
            if (offset >= 0) {
                _state.Bookmarks.Toggle(offset);
                InvalidateVisual();
                StateChanged?.Invoke();
            }
        };

        ContextMenu menu = new();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(addBookmark);
        menu.Opening += (_, _) => {
            bool hasSelection = _state.TextSelStart >= 0 && _state.TextSelEnd >= _state.TextSelStart;
            cut.IsEnabled = hasSelection && !_state.IsReadOnly;
            copy.IsEnabled = hasSelection;
            paste.IsEnabled = !_state.IsReadOnly;

            long offset = _state.TextCursorOffset;
            bool hasBookmark = offset >= 0 && _state.Bookmarks.Contains(offset);
            addBookmark.Header = hasBookmark ? "Remove Bookmark" : "Add Bookmark";
        };
        ContextMenu = menu;
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
        if (_updatingScroll || _state.Document is null || VerticalScrollBar is null) return;
        Core.Document doc = _state.Document;
        long newTop;
        if (_state.LineIndex is { SparseEntryCount: > 0 }) {
            long targetLine = (long)e.NewValue + 1;
            newTop = FindOffsetOfLine(targetLine);
        } else {
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
        if (_state.Document is null || VerticalScrollBar is null) return;
        _updatingScroll = true;
        try {
            if (_state.LineIndex?.TotalLineCount > 0)
                _state.EstimatedTotalLines = _state.LineIndex.TotalLineCount;

            long totalLines = Math.Max(1, _state.EstimatedTotalLines);
            double maxTop = Math.Max(0, totalLines - _state.VisibleRows);
            long currentLine = ComputeLineNumber(_state.TextTopOffset);
            double scrollPos = Math.Clamp(currentLine - 1, 0, (long)maxTop);

            VerticalScrollBar.Maximum = maxTop;
            VerticalScrollBar.ViewportSize = Math.Max(1, _state.VisibleRows);
            if (_userScrollFrames > 0)
                _userScrollFrames--;
            else
                VerticalScrollBar.Value = scrollPos;
        } finally {
            _updatingScroll = false;
        }
    }

    internal void OnHorizontalScrollBarValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingHorizontalScroll || HorizontalScrollBar is null) return;
        _state.TextHorizontalScroll = Math.Max(0, (int)Math.Round(e.NewValue));
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    private void UpdateHorizontalScrollBar()
    {
        if (HorizontalScrollBar is null) return;

        bool show = !_state.WordWrap &&
                    _hasHorizontalOverflow &&
                    _horizontalContentColumns > _horizontalViewportColumns;

        _updatingHorizontalScroll = true;
        try {
            HorizontalScrollBar.IsVisible = show;
            if (!show) {
                _state.TextHorizontalScroll = 0;
                HorizontalScrollBar.Minimum = 0;
                HorizontalScrollBar.Maximum = 0;
                HorizontalScrollBar.Value = 0;
                HorizontalScrollBar.ViewportSize = 1;
                return;
            }

            int maxScroll = Math.Max(0, _horizontalContentColumns - _horizontalViewportColumns);
            _state.TextHorizontalScroll = Math.Clamp(_state.TextHorizontalScroll, 0, maxScroll);

            HorizontalScrollBar.Minimum = 0;
            HorizontalScrollBar.Maximum = maxScroll;
            HorizontalScrollBar.ViewportSize = Math.Max(1, _horizontalViewportColumns);
            HorizontalScrollBar.SmallChange = 1;
            HorizontalScrollBar.LargeChange = Math.Max(1, _horizontalViewportColumns - 1);
            HorizontalScrollBar.Value = _state.TextHorizontalScroll;
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

        if (_state.Document is null) return;

        Rect bounds = Bounds;
        ColorTheme theme = _theme;

        // Paint control background
        context.FillRectangle(theme.Background, bounds);

        double charWidth = MeasureCharWidth();
        double lineHeight = _state.ContentFontSize + LinePadding;

        int visibleRows = Math.Max(1, (int)(bounds.Height / lineHeight));
        _state.VisibleRows = visibleRows;

        // Gutter width (line numbers)
        bool gutterVisible = _state.GutterVisible;
        long totalLines = Math.Max(1, _state.EstimatedTotalLines);
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(totalLines + 1)));
        double gutterWidth = gutterVisible ? (gutterDigits + 3) * charWidth : 0;

        // Draw gutter background + separator
        if (gutterVisible) {
            context.FillRectangle(theme.GutterBackground, new Rect(0, 0, gutterWidth - charWidth, bounds.Height));
            context.DrawLine(theme.GutterPen, new Point(gutterWidth - charWidth, 0),
                new Point(gutterWidth - charWidth, bounds.Height));
        }

        double textAreaWidth = bounds.Width - gutterWidth;
        int textAreaCols = Math.Max(1, (int)(textAreaWidth / charWidth));
        _lastTextAreaCols = textAreaCols;
        int columnsAvailable = _state.WordWrap ? textAreaCols : int.MaxValue;

        long topOffset = _state.TextTopOffset;
        int readSize = Math.Max((visibleRows + 4) * 256, 16384);
        int bytesRead;
        int lineCount;
        while (true) {
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

        // Binary-search for the first match visible in or after the viewport
        int matchCursor = SearchHighlightHelper.BinarySearchFirstMatch(matches, topOffset);

        // Selection range
        long selStart = _state.TextSelStart;
        long selEnd = _state.TextSelEnd;

        long currentLineNumber = ComputeLineNumber(topOffset) - 1;
        bool clipToViewport = !_state.WordWrap;
        int horizontalScroll = clipToViewport ? Math.Max(0, _state.TextHorizontalScroll) : 0;
        bool hasHorizontalOverflow = false;
        int maxRenderedColumns = 0;

        for (int row = 0; row < lineCount; row++) {
            VisualLine vl = _visualLines[row];
            double y = row * lineHeight;
            long lineAbsOffset = vl.DocOffset;

            // Detect hard line start: first row, or preceded by a newline
            bool isHardLine;
            if (row == 0) {
                isHardLine = topOffset == _state.BomLength || IsNewlineBefore(topOffset);
            } else {
                long prevEnd = _visualLines[row - 1].DocOffset + _visualLines[row - 1].ByteLength;
                isHardLine = vl.DocOffset != prevEnd || IsNewlineBefore(vl.DocOffset);
            }

            if (isHardLine)
                currentLineNumber++;

            // Gutter: show line number on hard lines, wrap indicator on continuations
            if (gutterVisible) {
                if (isHardLine) {
                    string lineNumStr = currentLineNumber.ToString();
                    double lineNumX = gutterWidth - (lineNumStr.Length + 1) * charWidth;

                    FormattedText lineNumText = new(lineNumStr,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, gutterTextBrush);
                    context.DrawText(lineNumText, new Point(lineNumX, y));
                } else {
                    // Wrap continuation: show ↪ indicator
                    string wrapIndicator = "↪";
                    FormattedText wrapText = new(wrapIndicator,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, theme.TextMuted);
                    double separatorX = gutterWidth - charWidth;
                    double wrapX = Math.Max(0, separatorX - wrapText.Width - charWidth * 0.5);
                    context.DrawText(wrapText, new Point(wrapX, y));
                }

                // Bookmark marker: small circle in the gutter
                long lineStart = vl.DocOffset;
                long lineEnd = lineStart + vl.ByteLength - 1;
                IReadOnlyList<Leviathan.Core.DataModel.Bookmark> bookmarks = _state.Bookmarks.GetAll();
                for (int bi = 0; bi < bookmarks.Count; bi++) {
                    long bmOff = bookmarks[bi].Offset;
                    if (bmOff > lineEnd) break;
                    if (bmOff >= lineStart) {
                        double markerY = y + lineHeight / 2;
                        context.DrawEllipse(BookmarkMarkerBrush, null,
                            new Point(charWidth * 0.4, markerY), charWidth * 0.3, charWidth * 0.3);
                        break;
                    }
                }
            }

            // Render text content
            int localOffset = (int)(vl.DocOffset - topOffset);
            if (localOffset < 0 || localOffset + vl.ByteLength > data.Length) continue;
            ReadOnlySpan<byte> lineData = data.Slice(localOffset, vl.ByteLength);
            double textX = gutterWidth;

            // Decode and render characters
            int charCol = 0;
            int byteIdx = 0;
            while (byteIdx < lineData.Length) {
                (System.Text.Rune rune, int runeBytes) = _state.Decoder.DecodeRune(lineData, byteIdx);
                if (runeBytes <= 0) { byteIdx++; continue; }

                int codePoint = rune.Value;
                long absOffset = lineAbsOffset + byteIdx;
                if (codePoint == 0xFEFF) {
                    byteIdx += runeBytes;
                    continue;
                }

                // Match highlight (sliding cursor — O(1) amortized per character)
                bool isMatch = false;
                bool isActiveMatch = false;
                while (matchCursor < matches.Count) {
                    long mStart = matches[matchCursor].Offset;
                    long mEnd = mStart + matches[matchCursor].Length - 1;
                    if (absOffset > mEnd) {
                        matchCursor++;
                        continue;
                    }
                    if (absOffset >= mStart) {
                        isMatch = true;
                        isActiveMatch = matchCursor == activeMatchIdx;
                    }
                    break;
                }
                if (isMatch) {
                    int visibleCol = charCol - horizontalScroll;
                    if (!clipToViewport || (visibleCol >= 0 && visibleCol < textAreaCols)) {
                        context.FillRectangle(isActiveMatch ? activeMatchBrush : matchBrush,
                            new Rect(textX + visibleCol * charWidth, y, charWidth, lineHeight));
                    }
                }

                if (codePoint == '\t') {
                    int tabStop = _state.TabWidth - (charCol % _state.TabWidth);
                    charCol += tabStop;
                    if (clipToViewport && charCol > horizontalScroll + textAreaCols)
                        hasHorizontalOverflow = true;
                    byteIdx += runeBytes;
                    continue;
                }

                if (clipToViewport && charCol >= horizontalScroll + textAreaCols) {
                    hasHorizontalOverflow = true;
                    charCol++;
                    byteIdx += runeBytes;
                    continue;
                }

                if (clipToViewport && charCol < horizontalScroll) {
                    charCol++;
                    byteIdx += runeBytes;
                    continue;
                }

                int drawCol = charCol - horizontalScroll;
                if (drawCol < 0) {
                    charCol++;
                    byteIdx += runeBytes;
                    continue;
                }

                if (selStart >= 0 && absOffset >= selStart && absOffset <= selEnd) {
                    context.FillRectangle(selectionBrush,
                        new Rect(textX + drawCol * charWidth, y, charWidth, lineHeight));
                }

                // Cursor
                if (absOffset == _state.TextCursorOffset) {
                    context.FillRectangle(cursorBrush,
                        new Rect(textX + drawCol * charWidth, y, 2, lineHeight));
                }

                // Character
                char ch;
                if (codePoint == '\n' || codePoint == '\r') {
                    byteIdx += runeBytes;
                    continue;
                } else if (codePoint >= 0x20 && codePoint < 0x10000) {
                    ch = (char)codePoint;
                } else {
                    ch = '.';
                }

                FormattedText charText = new(ch.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _state.ContentTypeface, _state.ContentFontSize, textBrush);
                context.DrawText(charText, new Point(textX + drawCol * charWidth, y));

                charCol++;
                byteIdx += runeBytes;
            }

            maxRenderedColumns = Math.Max(maxRenderedColumns, charCol);
        }

        _hasHorizontalOverflow = clipToViewport && Math.Max(maxRenderedColumns, hasHorizontalOverflow ? textAreaCols + 1 : 0) > textAreaCols;
        _horizontalViewportColumns = textAreaCols;
        _horizontalContentColumns = hasHorizontalOverflow
            ? Math.Max(textAreaCols + 1, maxRenderedColumns)
            : maxRenderedColumns;

        QueueScrollBarUpdate();
        QueueHorizontalScrollBarUpdate();
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

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        long oldCursor = _state.TextCursorOffset;
        _alignViewportToEnd = false;
        if (_state.WordWrap && _lastRenderedLineCount > 0) {
            switch (e.Key) {
                case Key.Up:
                    MoveVerticalVisual(-1);
                    if (shift) {
                        if (_state.TextSelectionAnchor < 0)
                            _state.TextSelectionAnchor = oldCursor;
                    } else {
                        _state.TextSelectionAnchor = -1;
                    }
                    e.Handled = true;
                    OnStateChanged();
                    return;
                case Key.Down:
                    MoveVerticalVisual(1);
                    if (shift) {
                        if (_state.TextSelectionAnchor < 0)
                            _state.TextSelectionAnchor = oldCursor;
                    } else {
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
        switch (e.Key) {
            case Key.Right:
                _desiredColumn = -1;
                _state.Document?.BreakCoalescing();
                newCursor = Math.Min(oldCursor + _state.Decoder.MinCharBytes, _state.FileLength);
                newCursor = SkipCrLf(newCursor, 1);
                break;
            case Key.Left:
                _desiredColumn = -1;
                _state.Document?.BreakCoalescing();
                newCursor = Math.Max(oldCursor - _state.Decoder.MinCharBytes, _state.BomLength);
                newCursor = SkipCrLf(newCursor, -1);
                break;
            case Key.Down:
                _state.Document?.BreakCoalescing();
                newCursor = MoveVerticalNoWrap(oldCursor, 1, 1);
                break;
            case Key.Up:
                _state.Document?.BreakCoalescing();
                newCursor = MoveVerticalNoWrap(oldCursor, -1, 1);
                break;
            case Key.PageDown:
                _state.Document?.BreakCoalescing();
                newCursor = MoveVerticalNoWrap(oldCursor, 1, Math.Max(1, _state.VisibleRows));
                break;
            case Key.PageUp:
                _state.Document?.BreakCoalescing();
                newCursor = MoveVerticalNoWrap(oldCursor, -1, Math.Max(1, _state.VisibleRows));
                break;
            case Key.Home:
                _desiredColumn = -1;
                _state.Document?.BreakCoalescing();
                newCursor = ctrl ? _state.BomLength : FindLineStart(oldCursor);
                break;
            case Key.End:
                _desiredColumn = -1;
                _state.Document?.BreakCoalescing();
                if (ctrl) {
                    newCursor = _state.FileLength;
                    _alignViewportToEnd = true;
                } else {
                    newCursor = FindLineEnd(oldCursor);
                }
                break;
            case Key.Back:
                _desiredColumn = -1;
                if (_state.IsReadOnly)
                    break;
                if (TryDeleteSelection()) {
                    newCursor = _state.TextCursorOffset;
                    shift = false;
                    break;
                }
                if (oldCursor > _state.BomLength) {
                    long deleteAt = Math.Max(oldCursor - _state.Decoder.MinCharBytes, _state.BomLength);
                    long deleteLen = oldCursor - deleteAt;
                    _state.Document.Delete(deleteAt, deleteLen, oldCursor);
                    newCursor = deleteAt;
                    _state.InvalidateSearchResults();
                }
                break;
            case Key.Delete:
                _desiredColumn = -1;
                if (_state.IsReadOnly)
                    break;
                if (TryDeleteSelection()) {
                    newCursor = _state.TextCursorOffset;
                    shift = false;
                    break;
                }
                if (oldCursor < _state.FileLength) {
                    _state.Document.Delete(oldCursor, _state.Decoder.MinCharBytes, oldCursor);
                    _state.InvalidateSearchResults();
                }
                break;
            case Key.Enter:
                _desiredColumn = -1;
                if (_state.IsReadOnly)
                    break;
                _state.Document.BreakCoalescing();
                _state.Document.Insert(oldCursor, _state.Decoder.EncodeString("\n"), oldCursor);
                newCursor = oldCursor + _state.Decoder.MinCharBytes;
                _state.InvalidateSearchResults();
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        if (shift) {
            if (_state.TextSelectionAnchor < 0)
                _state.TextSelectionAnchor = oldCursor;
        } else {
            _state.TextSelectionAnchor = -1;
        }

        _state.TextCursorOffset = newCursor;
        e.Handled = true;
        OnStateChanged();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_state.Document is null || string.IsNullOrEmpty(e.Text) || _state.IsReadOnly) return;

        _desiredColumn = -1;

        bool hasSelection = _state.TextSelStart >= 0 && _state.TextSelEnd >= _state.TextSelStart;
        if (hasSelection) {
            _state.Document.BeginUndoGroup(_state.TextCursorOffset);
            TryDeleteSelection();
        }

        byte[] encoded = _state.Decoder.EncodeString(e.Text);
        _state.Document.Insert(_state.TextCursorOffset, encoded, _state.TextCursorOffset);
        _state.TextCursorOffset += encoded.Length;

        if (hasSelection)
            _state.Document.EndUndoGroup(_state.TextCursorOffset);

        _state.InvalidateSearchResults();
        OnStateChanged();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_state.Document is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _state.Document.BreakCoalescing();
        long offset = HitTest(e.GetPosition(this));
        if (offset < 0) return;

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _state.TextSelectionAnchor = ResolvePointerSelectionAnchor(_state.TextSelectionAnchor, _state.TextCursorOffset, offset, shift);
        _state.TextCursorOffset = offset;
        _desiredColumn = -1;
        _isPointerSelectionActive = true;
        _pointerSelectionExtended = false;
        _pointerSelectionStartedWithShift = shift;
        _pointerSelectionStartOffset = offset;
        e.Pointer.Capture(this);
        e.Handled = true;
        OnStateChanged();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isPointerSelectionActive || _state.Document is null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Pointer.Captured != this)
            return;

        long offset = HitTestForPointerSelection(e.GetPosition(this));
        if (offset < 0 || offset == _state.TextCursorOffset)
            return;

        _state.TextCursorOffset = offset;
        _desiredColumn = -1;
        _pointerSelectionExtended = _pointerSelectionExtended || offset != _pointerSelectionStartOffset;
        e.Handled = true;
        OnStateChanged();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isPointerSelectionActive)
            return;

        bool shiftPressed = _pointerSelectionStartedWithShift;
        bool selectionExtended = _pointerSelectionExtended;

        if (e.Pointer.Captured == this)
            e.Pointer.Capture(null);

        _state.TextSelectionAnchor = ResolveSelectionAnchorAfterPointerRelease(_state.TextSelectionAnchor, shiftPressed, selectionExtended);

        _isPointerSelectionActive = false;
        _pointerSelectionExtended = false;
        _pointerSelectionStartedWithShift = false;
        _pointerSelectionStartOffset = -1;
        e.Handled = true;
        OnStateChanged();
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
        ScrollByRows(rows);
        InvalidateVisual();
    }

    private long HitTest(Point point) => HitTestCore(point, clampToViewport: false, allowGutterHit: false);

    private long HitTestForPointerSelection(Point point) => HitTestCore(point, clampToViewport: true, allowGutterHit: true);

    private long HitTestCore(Point point, bool clampToViewport, bool allowGutterHit)
    {
        if (_state.Document is null)
            return -1;

        double charWidth = MeasureCharWidth();
        if (charWidth <= 0)
            return -1;

        double lineHeight = _state.ContentFontSize + LinePadding;
        if (lineHeight <= 0)
            return -1;

        long totalLines = Math.Max(1, _state.EstimatedTotalLines);
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(totalLines + 1)));
        double gutterWidth = _state.GutterVisible ? (gutterDigits + 3) * charWidth : 0;

        if (!allowGutterHit && gutterWidth > 0 && point.X < gutterWidth)
            return -1;

        int row = (int)Math.Floor(point.Y / lineHeight);
        if (!clampToViewport && row < 0)
            return -1;

        double x = allowGutterHit ? Math.Max(point.X, gutterWidth) : point.X;
        int col = (int)Math.Floor((x - gutterWidth) / charWidth);
        col = Math.Max(0, col);
        if (!_state.WordWrap)
            col += Math.Max(0, _state.TextHorizontalScroll);

        long topOffset = _state.TextTopOffset;
        int readLen = (int)Math.Min(_readBuffer.Length, _state.FileLength - topOffset);
        if (readLen <= 0)
            return -1;

        _state.Document.Read(topOffset, _readBuffer.AsSpan(0, readLen));
        ReadOnlySpan<byte> data = _readBuffer.AsSpan(0, readLen);

        double textAreaWidth = Math.Max(charWidth, Bounds.Width - gutterWidth);
        int columnsAvailable = _state.WordWrap ? Math.Max(1, (int)(textAreaWidth / charWidth)) : int.MaxValue;
        int lineCount = _wrapEngine.ComputeVisualLines(data, topOffset, columnsAvailable, _state.WordWrap, _visualLines.AsSpan(), _state.Decoder);
        if (lineCount <= 0)
            return -1;

        if (!clampToViewport && row >= lineCount)
            return -1;

        int clampedRow = Math.Clamp(row, 0, lineCount - 1);
        VisualLine vl = _visualLines[clampedRow];
        int localOffset = (int)(vl.DocOffset - topOffset);
        if (localOffset < 0 || localOffset + vl.ByteLength > data.Length)
            return -1;

        ReadOnlySpan<byte> lineData = data.Slice(localOffset, vl.ByteLength);
        int targetByteOffset = DisplayColumnToByteOffset(lineData, col, _state.Decoder);
        long lineEnd = Math.Min(vl.DocOffset + vl.ByteLength, _state.FileLength);
        return Math.Clamp(vl.DocOffset + targetByteOffset, _state.BomLength, lineEnd);
    }

    /// <summary>Navigates to a specific line number (1-based).</summary>
    internal void GotoLine(long lineNumber, int? columnNumber = null)
    {
        if (_state.Document is null || lineNumber < 1) return;
        _desiredColumn = -1;
        long offset = FindOffsetOfLine(lineNumber);
        offset = Math.Max(offset, _state.BomLength);
        if (columnNumber is int column && column > 1)
            offset = AdvanceOffsetByDisplayColumns(offset, column - 1);

        _state.TextCursorOffset = offset;
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

    /// <summary>Sets the top offset from a byte anchor, used for linked-tab viewport sync.</summary>
    internal void SyncTopOffset(long offset)
    {
        if (_state.Document is null)
            return;

        long clamped = Math.Clamp(offset, _state.BomLength, _state.Document.Length);
        _state.TextTopOffset = clamped;
        _state.TextCursorOffset = clamped;
        _state.TextSelectionAnchor = -1;
        _desiredColumn = -1;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    private long AdvanceOffsetByDisplayColumns(long lineStartOffset, int targetColumn)
    {
        Core.Document? doc = _state.Document;
        if (doc is null || targetColumn <= 0)
            return Math.Max(lineStartOffset, _state.BomLength);

        long maxReadable = Math.Min(doc.Length - lineStartOffset, int.MaxValue);
        int readLen = (int)Math.Min(8192L, maxReadable);
        if (readLen <= 0)
            return Math.Max(lineStartOffset, _state.BomLength);

        while (true) {
            EnsureBuffer(readLen);
            int read = doc.Read(lineStartOffset, _readBuffer.AsSpan(0, readLen));
            if (read <= 0)
                return Math.Max(lineStartOffset, _state.BomLength);

            ReadOnlySpan<byte> lineBytes = _readBuffer.AsSpan(0, read);
            int byteOffset = DisplayColumnToByteOffset(lineBytes, targetColumn, _state.Decoder);
            int availableColumns = ByteOffsetToDisplayColumn(lineBytes, byteOffset, _state.Decoder);
            bool hitLineEnd = byteOffset < read && _state.Decoder.IsNewline(lineBytes, byteOffset, out _);

            long targetOffset = lineStartOffset + byteOffset;
            if (availableColumns >= targetColumn || hitLineEnd || readLen >= maxReadable)
                return Math.Clamp(targetOffset, _state.BomLength, doc.Length);

            readLen = (int)Math.Min(maxReadable, (long)readLen * 2);
        }
    }

    private void EnsureCursorVisible()
    {
        if (_state.TextCursorOffset < 0 || _state.Document is null) return;

        int textAreaCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : GetColumnsAvailable();
        int vpHeight = _state.VisibleRows;
        if (vpHeight <= 0) vpHeight = 24;

        if (_alignViewportToEnd && _state.TextCursorOffset >= _state.Document.Length) {
            _state.TextTopOffset = ComputeViewportTopForDocumentEnd(vpHeight, textAreaCols);
            _alignViewportToEnd = false;
            return;
        }
        _alignViewportToEnd = false;

        if (_state.TextCursorOffset < _state.TextTopOffset) {
            if (_state.WordWrap) {
                _state.TextTopOffset = ComputeViewportTopForCursor(_state.TextCursorOffset, textAreaCols, 0);
            } else {
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
        for (int i = 0; i < rowsToCheck; i++) {
            long vlStart = _visualLines[i].DocOffset;
            long vlEnd = vlStart + _visualLines[i].ByteLength;
            if (_state.TextCursorOffset >= vlStart && _state.TextCursorOffset <= vlEnd) {
                cursorVisible = true;
                break;
            }
        }

        if (!cursorVisible) {
            if (_state.WordWrap) {
                _state.TextTopOffset = ComputeViewportTopForCursor(_state.TextCursorOffset, textAreaCols, vpHeight / 3);
            } else {
                long newTop = FindLineStart(_state.TextCursorOffset);
                int minChar = _state.Decoder.MinCharBytes;
                for (int i = 0; i < vpHeight / 2 && newTop > _state.BomLength; i++) {
                    long prev = FindLineStart(Math.Max(_state.BomLength, newTop - minChar));
                    if (prev >= newTop) break;
                    newTop = prev;
                }
                _state.TextTopOffset = newTop;
            }
        }

        EnsureHorizontalCursorVisibleNoWrap();
    }

    private void EnsureHorizontalCursorVisibleNoWrap()
    {
        if (_state.Document is null || _state.WordWrap)
            return;

        long lineStart = Math.Max(FindLineStart(_state.TextCursorOffset), _state.BomLength);
        long lineEnd = FindLineEnd(_state.TextCursorOffset);
        int lineLength = (int)Math.Min(lineEnd - lineStart + NewlineLengthAt(lineEnd), int.MaxValue);
        if (lineLength <= 0)
            return;

        EnsureBuffer(lineLength);
        _state.Document.Read(lineStart, _readBuffer.AsSpan(0, lineLength));
        int cursorColumn = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, lineLength), _state.TextCursorOffset - lineStart, _state.Decoder);

        int viewportColumns = Math.Max(1, _lastTextAreaCols > 0 ? _lastTextAreaCols : GetColumnsAvailable());
        if (cursorColumn < _state.TextHorizontalScroll)
            _state.TextHorizontalScroll = cursorColumn;
        else if (cursorColumn >= _state.TextHorizontalScroll + viewportColumns)
            _state.TextHorizontalScroll = cursorColumn - viewportColumns + 1;
    }

    private long ComputeViewportTopForDocumentEnd(int viewportRows, int textAreaCols)
    {
        if (_state.Document is null)
            return _state.BomLength;

        long docLength = _state.Document.Length;
        if (docLength <= _state.BomLength)
            return _state.BomLength;

        int rowsToBacktrack = Math.Max(0, viewportRows - 2);
        long anchor = docLength;
        if (IsNewlineBefore(anchor))
            anchor = Math.Max(_state.BomLength, anchor - _state.Decoder.MinCharBytes);

        if (_state.WordWrap) {
            int maxCols = Math.Max(1, textAreaCols);
            long wrapTop = FindVisualLineContaining(anchor, maxCols, out VisualLine anchorLine)
                ? anchorLine.DocOffset
                : FindLineStart(anchor);
            for (int i = 0; i < rowsToBacktrack && wrapTop > _state.BomLength; i++) {
                if (!FindPreviousVisualLine(wrapTop, maxCols, out VisualLine previousVisualLine))
                    break;
                wrapTop = previousVisualLine.DocOffset;
            }

            return Math.Clamp(wrapTop, _state.BomLength, docLength);
        }

        long noWrapTop = FindLineStart(anchor);
        int minChar = _state.Decoder.MinCharBytes;
        for (int i = 0; i < rowsToBacktrack && noWrapTop > _state.BomLength; i++) {
            long prevCandidate = Math.Max(_state.BomLength, noWrapTop - minChar);
            long prev = FindLineStart(prevCandidate);
            if (prev >= noWrapTop) break;
            noWrapTop = prev;
        }

        return Math.Clamp(noWrapTop, _state.BomLength, docLength);
    }

    private void ScrollByRows(int rowDelta)
    {
        if (_state.WordWrap) {
            int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
            long offset = _state.TextTopOffset;
            if (rowDelta > 0) {
                for (int i = 0; i < rowDelta && offset < _state.FileLength; i++) {
                    if (FindNextVisualLine(offset, maxCols, out VisualLine nextVl)) {
                        long next = nextVl.DocOffset + nextVl.ByteLength;
                        if (next <= offset) break;
                        offset = next;
                    } else break;
                }
            } else {
                int steps = -rowDelta;
                for (int i = 0; i < steps && offset > _state.BomLength; i++) {
                    if (FindPreviousVisualLine(offset, maxCols, out VisualLine prevVl))
                        offset = prevVl.DocOffset;
                    else break;
                }
            }

            _state.TextTopOffset = Math.Clamp(offset, _state.BomLength, Math.Max(_state.BomLength, _state.FileLength));
            return;
        }

        if (rowDelta > 0 && TryScrollDownUsingRenderedLines(rowDelta))
            return;

        long off = _state.TextTopOffset;
        if (rowDelta > 0) {
            for (int i = 0; i < rowDelta && off < _state.FileLength; i++) {
                long lineEnd = FindLineEnd(off);
                int newlineLen = NewlineLengthAt(lineEnd);
                off = Math.Min(lineEnd + Math.Max(newlineLen, _state.Decoder.MinCharBytes), _state.FileLength);
            }
        } else {
            int steps = -rowDelta;
            int minChar = _state.Decoder.MinCharBytes;
            for (int i = 0; i < steps && off > _state.BomLength; i++) {
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

    private long MoveVerticalNoWrap(long offset, int direction, int lineCount)
    {
        if (_state.Document is null || lineCount <= 0 || direction == 0)
            return offset;

        int targetColumn = GetDesiredColumnForNoWrap(offset);
        long currentOffset = offset;
        for (int step = 0; step < lineCount; step++) {
            long nextOffset = direction < 0
                ? MoveToPreviousLineAtColumn(currentOffset, targetColumn)
                : MoveToNextLineAtColumn(currentOffset, targetColumn);
            if (nextOffset == currentOffset)
                break;

            currentOffset = nextOffset;
        }

        return currentOffset;
    }

    private int GetDesiredColumnForNoWrap(long offset)
    {
        if (_desiredColumn >= 0)
            return _desiredColumn;

        if (_state.Document is null)
            return 0;

        long lineStart = Math.Max(FindLineStart(offset), _state.BomLength);
        long lineEnd = FindLineEnd(offset);
        int lineLength = (int)Math.Min(lineEnd - lineStart + NewlineLengthAt(lineEnd), int.MaxValue);
        if (lineLength <= 0) {
            _desiredColumn = 0;
            return 0;
        }

        EnsureBuffer(lineLength);
        _state.Document.Read(lineStart, _readBuffer.AsSpan(0, lineLength));
        int displayColumn = ByteOffsetToDisplayColumn(
            _readBuffer.AsSpan(0, lineLength),
            Math.Max(0, offset - lineStart),
            _state.Decoder);
        _desiredColumn = displayColumn;
        return displayColumn;
    }

    private long MoveToPreviousLineAtColumn(long offset, int targetColumn)
    {
        if (_state.Document is null)
            return offset;

        int bom = _state.BomLength;
        long lineStart = FindLineStart(offset);
        if (lineStart <= bom)
            return offset;

        int minChar = _state.Decoder.MinCharBytes;
        long prevLineEnd = Math.Max(bom, lineStart - minChar);
        long prevLineStart = Math.Max(FindLineStart(prevLineEnd), bom);
        int prevLength = (int)Math.Min(prevLineEnd - prevLineStart + NewlineLengthAt(prevLineEnd), int.MaxValue);
        if (prevLength <= 0)
            return prevLineStart;

        EnsureBuffer(prevLength);
        _state.Document.Read(prevLineStart, _readBuffer.AsSpan(0, prevLength));
        int byteColumn = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, prevLength), targetColumn, _state.Decoder);
        return Math.Min(prevLineStart + byteColumn, prevLineEnd);
    }

    private long MoveToNextLineAtColumn(long offset, int targetColumn)
    {
        if (_state.Document is null)
            return offset;

        long lineEnd = FindLineEnd(offset);
        if (lineEnd >= _state.Document.Length)
            return offset;

        long nextLineStart = Math.Min(
            lineEnd + Math.Max(NewlineLengthAt(lineEnd), _state.Decoder.MinCharBytes),
            _state.Document.Length);
        if (nextLineStart >= _state.Document.Length)
            return _state.Document.Length;

        long nextLineEnd = FindLineEnd(nextLineStart);
        int nextLength = (int)Math.Min(nextLineEnd - nextLineStart + NewlineLengthAt(nextLineEnd), int.MaxValue);
        if (nextLength <= 0)
            return nextLineStart;

        EnsureBuffer(nextLength);
        _state.Document.Read(nextLineStart, _readBuffer.AsSpan(0, nextLength));
        int byteColumn = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, nextLength), targetColumn, _state.Decoder);
        return Math.Min(nextLineStart + byteColumn, nextLineEnd);
    }

    private bool TryScrollDownUsingRenderedLines(int rowDelta)
    {
        if (_lastRenderedLineCount <= rowDelta || rowDelta <= 0)
            return false;

        if (_visualLines[0].DocOffset != _state.TextTopOffset)
            return false;

        long newTop = _visualLines[rowDelta].DocOffset;
        if (newTop <= _state.TextTopOffset)
            return false;

        _state.TextTopOffset = Math.Clamp(newTop, _state.BomLength, Math.Max(_state.BomLength, _state.FileLength));
        return true;
    }

    /// <summary>Returns the number of character columns available in the text area.</summary>
    private int GetColumnsAvailable()
    {
        double charWidth = MeasureCharWidth();
        long totalLines = Math.Max(1, _state.EstimatedTotalLines);
        int gutterDigits = Math.Max(4, (int)Math.Ceiling(Math.Log10(totalLines + 1)));
        double gutterWidth = _state.GutterVisible ? (gutterDigits + 3) * charWidth : 0; double textAreaWidth = Bounds.Width - gutterWidth;
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
        for (int i = 0; i < _lastRenderedLineCount; i++) {
            long vlStart = _visualLines[i].DocOffset;
            long vlEnd = vlStart + _visualLines[i].ByteLength;
            if (_state.TextCursorOffset >= vlStart && _state.TextCursorOffset < vlEnd) {
                cursorRow = i;
                break;
            }

            if (_state.TextCursorOffset == vlEnd) {
                bool nextStartsHere = (i + 1 < _lastRenderedLineCount) && _visualLines[i + 1].DocOffset == vlEnd;
                if (!nextStartsHere) {
                    cursorRow = i;
                    break;
                }
            }
        }
        if (cursorRow < 0) cursorRow = 0;

        int displayCol;
        if (_desiredColumn >= 0) {
            displayCol = _desiredColumn;
        } else {
            displayCol = 0;
            if (cursorRow < _lastRenderedLineCount) {
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
        if (direction > 0) {
            for (int i = 0; i < vpHeight && newTop < _state.Document.Length; i++) {
                if (FindNextVisualLine(newTop, maxCols, out VisualLine nextVl)) {
                    long next = nextVl.DocOffset + nextVl.ByteLength;
                    if (next <= newTop) break;
                    newTop = next;
                } else break;
            }
            newTop = Math.Min(newTop, _state.Document.Length);
        } else {
            for (int i = 0; i < vpHeight && newTop > _state.BomLength; i++) {
                if (FindPreviousVisualLine(newTop, maxCols, out VisualLine prevVl))
                    newTop = prevVl.DocOffset;
                else break;
            }
        }

        _state.TextTopOffset = newTop;

        int readSize = Math.Max((vpHeight + 4) * 256, 16384);
        readSize = (int)Math.Min(readSize, _state.Document.Length - _state.TextTopOffset);
        if (readSize > 0) {
            EnsureBuffer(readSize);
            int bytesRead = _state.Document.Read(_state.TextTopOffset, _readBuffer.AsSpan(0, readSize));
            if (bytesRead > 0) {
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
        for (int i = 0; i < _lastRenderedLineCount; i++) {
            long vlStart = _visualLines[i].DocOffset;
            long vlEnd = vlStart + _visualLines[i].ByteLength;
            if (_state.TextCursorOffset >= vlStart && _state.TextCursorOffset < vlEnd) {
                cursorRow = i;
                break;
            }
            if (_state.TextCursorOffset == vlEnd) {
                bool nextLineStartsHere = (i + 1 < _lastRenderedLineCount) && _visualLines[i + 1].DocOffset == vlEnd;
                if (!nextLineStartsHere) {
                    cursorRow = i;
                    break;
                }
            }
        }

        int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
        if (cursorRow < 0) {
            if (FindVisualLineContaining(_state.TextCursorOffset, maxCols, out VisualLine curVlFb)) {
                int col;
                if (_desiredColumn >= 0) {
                    col = _desiredColumn;
                } else {
                    int lineLen = (int)Math.Min(curVlFb.ByteLength, int.MaxValue);
                    EnsureBuffer(lineLen);
                    _state.Document.Read(curVlFb.DocOffset, _readBuffer.AsSpan(0, lineLen));
                    col = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, lineLen),
                        _state.TextCursorOffset - curVlFb.DocOffset, _state.Decoder);
                    _desiredColumn = col;
                }

                if (direction < 0) {
                    if (curVlFb.DocOffset <= _state.BomLength) return;
                    if (FindPreviousVisualLine(curVlFb.DocOffset, maxCols, out VisualLine prevVl))
                        PlaceCursorOnVisualLine(prevVl, col);
                } else {
                    long afterCur = curVlFb.DocOffset + curVlFb.ByteLength;
                    if (afterCur < _state.Document.Length && FindNextVisualLine(afterCur, maxCols, out VisualLine nextVl))
                        PlaceCursorOnVisualLine(nextVl, col);
                }
                return;
            }

            if (direction < 0) {
                _state.TextCursorOffset = MoveUp(_state.TextCursorOffset);
            } else {
                _state.TextCursorOffset = MoveDown(_state.TextCursorOffset);
            }
            return;
        }

        VisualLine curVl = _visualLines[cursorRow];
        int displayCol;
        if (_desiredColumn >= 0) {
            displayCol = _desiredColumn;
        } else {
            int lineLen = (int)Math.Min(curVl.ByteLength, int.MaxValue);
            EnsureBuffer(lineLen);
            _state.Document.Read(curVl.DocOffset, _readBuffer.AsSpan(0, lineLen));
            long byteInLine = _state.TextCursorOffset - curVl.DocOffset;
            displayCol = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, lineLen), byteInLine, _state.Decoder);
            _desiredColumn = displayCol;
        }

        int targetRow = cursorRow + direction;
        if (targetRow < 0) {
            long firstVlStart = _visualLines[0].DocOffset;
            if (firstVlStart <= _state.BomLength) return;
            if (FindPreviousVisualLine(firstVlStart, maxCols, out VisualLine prevVl))
                PlaceCursorOnVisualLine(prevVl, displayCol);
            return;
        }

        if (targetRow >= _lastRenderedLineCount) {
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
        while (pos < lineBytes.Length && pos < byteOffsetInLine) {
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
        while (pos < lineBytes.Length) {
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
        if (vlStartOffset == hardLineStart) {
            long prevPos = Math.Max(_state.BomLength, vlStartOffset - _state.Decoder.MinCharBytes);
            hardLineStart = FindLineStart(prevPos);
        }
        if (hardLineStart < _state.BomLength) hardLineStart = _state.BomLength;

        bool found = false;
        long scanPos = hardLineStart;
        while (scanPos < vlStartOffset) {
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

            for (int i = 0; i < count; i++) {
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
        while (scanPos <= offset && scanPos < _state.Document.Length) {
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

            for (int i = 0; i < count; i++) {
                long vlStart = _navVisualLines[i].DocOffset;
                long vlEnd = vlStart + _navVisualLines[i].ByteLength;
                if (offset >= vlStart && offset < vlEnd) {
                    result = _navVisualLines[i];
                    return true;
                }
                if (offset == vlEnd) {
                    bool nextStartsHere = (i + 1 < count) && _navVisualLines[i + 1].DocOffset == vlEnd;
                    bool bufferExhausted = (i + 1 >= count) && (count >= _navVisualLines.Length);
                    if (!nextStartsHere && !bufferExhausted) {
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

        while (scanPos <= cursorOffset && scanPos < _state.Document.Length) {
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

            for (int i = 0; i < count; i++) {
                long vlStart = _navVisualLines[i].DocOffset;
                long vlEnd = vlStart + _navVisualLines[i].ByteLength;

                recentStarts[recentWritten % bufferSize] = vlStart;
                recentWritten++;

                if (cursorOffset >= vlStart && cursorOffset <= vlEnd) {
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
        while (search > _state.BomLength) {
            int chunkLen = (int)Math.Min(chunkSize, search - _state.BomLength);
            long chunkStart = search - chunkLen;
            EnsureScanBuffer(chunkLen);
            Span<byte> buf = _scanBuffer.AsSpan(0, chunkLen);
            _state.Document.Read(chunkStart, buf);

            for (int i = chunkLen - minChar; i >= 0; i -= minChar) {
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

        while (search < _state.Document.Length) {
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

            for (int i = scanStart; i + minChar <= chunkLen; i += minChar) {
                if (IsLF(buf, i, minChar)) {
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

        if (minChar == 1) {
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

    /// <summary>
    /// Skips the invisible \r in a \r\n pair so the cursor doesn't appear stuck.
    /// For direction +1 (right): if landing on \r followed by \n, advance past \r.
    /// For direction -1 (left): if landing on \r preceded by nothing special and followed by \n, retreat one more.
    /// </summary>
    private long SkipCrLf(long offset, int direction)
    {
        if (_state.Document is null || _state.Decoder.MinCharBytes != 1) return offset;
        if (offset < _state.BomLength || offset >= _state.Document.Length) return offset;

        Span<byte> buf = stackalloc byte[2];
        int readable = (int)Math.Min(2, _state.Document.Length - offset);
        int read = _state.Document.Read(offset, buf[..readable]);
        if (read < 1) return offset;

        if (buf[0] == 0x0D && read >= 2 && buf[1] == 0x0A) {
            // Cursor is on \r of a \r\n pair — skip it
            if (direction > 0)
                return Math.Min(offset + 1, _state.FileLength); // advance to \n
            else
                return Math.Max(offset - 1, _state.BomLength); // retreat past \r
        }

        return offset;
    }

    private long ComputeLineNumber(long offset)
    {
        if (_state.Document is null) return 1;

        if (_state.LineIndex is { SparseEntryCount: > 0 } index) {
            long baseLine = index.EstimateLineForOffset(offset, _state.Document.Length);
            int sparseFactor = index.SparseFactor;
            int sparseIdx = (int)(baseLine / sparseFactor) - 1;
            if (sparseIdx >= index.SparseEntryCount)
                sparseIdx = index.SparseEntryCount - 1;

            long scanFrom;
            long lineNum;
            if (sparseIdx >= 0) {
                scanFrom = index.GetSparseOffset(sparseIdx) + _state.Decoder.MinCharBytes;
                lineNum = (long)(sparseIdx + 1) * sparseFactor + 1;
            } else {
                scanFrom = _state.BomLength;
                lineNum = 1;
            }

            if (offset > scanFrom) {
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
        if (offset >= _cachedTopOffset) {
            from = _cachedTopOffset;
            to = offset;
            lineNumber = _cachedTopLineNumber;
        } else {
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

        while (pos < to) {
            int readLen = (int)Math.Min(_scanBuffer.Length, to - pos);
            Span<byte> buf = _scanBuffer.AsSpan(0, readLen);
            int read = _state.Document.Read(pos, buf);
            if (read == 0) break;

            if (minChar == 2) {
                int alignedLen = read & ~1;
                for (int i = 0; i + 1 < alignedLen; i += 2) {
                    if (buf[i] == 0x0A && buf[i + 1] == 0x00)
                        count++;
                }
            } else {
                for (int i = 0; i < read; i++) {
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

        LineIndex? lineIndex = _state.LineIndex;
        int minChar = _state.Decoder.MinCharBytes;
        long newlinesNeeded = targetLine - 1;
        long startOffset = _state.BomLength;
        long newlinesCounted = 0;

        if (lineIndex is not null && !lineIndex.IsComplete && newlinesNeeded > lineIndex.TotalLineCount)
            return EstimateOffsetOfLine(targetLine, lineIndex);

        if (lineIndex is { SparseEntryCount: > 0 } index) {
            int sparseFactor = index.SparseFactor;
            int sparseIdx = (int)(newlinesNeeded / sparseFactor) - 1;
            if (sparseIdx >= index.SparseEntryCount)
                sparseIdx = index.SparseEntryCount - 1;
            if (sparseIdx >= 0) {
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
        while (pos < _state.Document.Length && found < remaining) {
            int readLen = (int)Math.Min(_scanBuffer.Length, _state.Document.Length - pos);
            Span<byte> buf = _scanBuffer.AsSpan(0, readLen);
            int read = _state.Document.Read(pos, buf);
            if (read == 0) break;

            if (minChar == 2) {
                int alignedLen = read & ~1;
                for (int i = 0; i + 1 < alignedLen; i += 2) {
                    if (buf[i] == 0x0A && buf[i + 1] == 0x00) {
                        found++;
                        if (found >= remaining)
                            return pos + i + 2;
                    }
                }
            } else {
                for (int i = 0; i < read; i++) {
                    if (buf[i] == 0x0A) {
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

    private long EstimateOffsetOfLine(long targetLine, LineIndex? index)
    {
        if (_state.Document is null)
            return _state.BomLength;

        long docLength = _state.Document.Length;
        if (docLength <= _state.BomLength)
            return _state.BomLength;

        int minChar = _state.Decoder.MinCharBytes;
        long approx = _state.BomLength;

        if (index is { SparseEntryCount: > 0 })
        {
            int lastSparseIdx = index.SparseEntryCount - 1;
            long lastSparseOffset = index.GetSparseOffset(lastSparseIdx);
            long sampleNewlines = (long)(lastSparseIdx + 1) * index.SparseFactor;
            if (sampleNewlines > 0 && lastSparseOffset >= _state.BomLength)
            {
                double avgBytesPerLine = Math.Max(1.0, (lastSparseOffset - _state.BomLength + minChar) / (double)sampleNewlines);
                long sampleLine = sampleNewlines + 1;
                long remainingLines = Math.Max(0, targetLine - sampleLine);
                approx = lastSparseOffset + minChar + (long)(remainingLines * avgBytesPerLine);
            }
        }
        else
        {
            long estimatedLines = Math.Max(targetLine, _state.EstimatedTotalLines);
            double ratio = (targetLine - 1) / (double)Math.Max(1, estimatedLines);
            approx = _state.BomLength + (long)((docLength - _state.BomLength) * Math.Clamp(ratio, 0d, 1d));
        }

        approx = Math.Clamp(approx, _state.BomLength, docLength);
        return FindLineStart(approx);
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

    internal static bool ShouldClearSelectionAfterPointerRelease(bool shiftPressed, bool selectionExtended)
        => !shiftPressed && !selectionExtended;

    internal static long ResolveSelectionAnchorAfterPointerRelease(long currentAnchor, bool shiftPressed, bool selectionExtended)
        => ShouldClearSelectionAfterPointerRelease(shiftPressed, selectionExtended) ? -1 : currentAnchor;

    internal static bool TryGetSelectionDeleteRange(long selectionStart, long selectionEnd, out long deleteStart, out long deleteLength)
    {
        if (selectionStart < 0 || selectionEnd < selectionStart) {
            deleteStart = 0;
            deleteLength = 0;
            return false;
        }

        deleteStart = selectionStart;
        deleteLength = selectionEnd - selectionStart + 1;
        return deleteLength > 0;
    }

    private bool TryDeleteSelection()
    {
        if (_state.Document is null)
            return false;

        if (!TryGetSelectionDeleteRange(_state.TextSelStart, _state.TextSelEnd, out long deleteStart, out long deleteLength))
            return false;

        _state.Document.BreakCoalescing();
        _state.Document.Delete(deleteStart, deleteLength, _state.TextCursorOffset);
        _state.TextCursorOffset = deleteStart;
        _state.TextSelectionAnchor = -1;
        _state.InvalidateSearchResults();
        return true;
    }

    private void OnStateChanged()
    {
        EnsureCursorVisible();
        InvalidateVisual();
        StateChanged?.Invoke();
    }
}
