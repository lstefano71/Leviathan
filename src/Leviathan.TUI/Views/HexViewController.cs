using System.Runtime.CompilerServices;
using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.TUI.Rendering;

namespace Leviathan.TUI.Views;

/// <summary>
/// Hex view logic: navigation, editing, and line formatting.
/// Pure logic — no hex1b dependencies. Produces strings for the TUI to render.
/// </summary>
internal sealed class HexViewController
{
    private readonly AppState _state;
    private byte[] _readBuffer = new byte[64 * 1024];

    internal HexViewController(AppState state)
    {
        _state = state;
    }

    /// <summary>
    /// Produces the visible hex rows as ANSI-colored strings.
    /// </summary>
    internal string[] RenderRows(int terminalWidth, int terminalHeight)
    {
        Document? doc = _state.Document;
        if (doc is null)
            return [];

        int bpr = _state.ComputeBytesPerRow(terminalWidth);
        _state.BytesPerRow = bpr;

        // Reserve 2 rows for status bar
        int visibleRows = terminalHeight - 2;
        if (visibleRows < 1) visibleRows = 1;
        _state.VisibleRows = visibleRows;

        // Ensure cursor is visible
        EnsureCursorVisible();

        int totalBytes = visibleRows * bpr;
        EnsureBuffer(totalBytes);

        Span<byte> buf = _readBuffer.AsSpan(0, totalBytes);
        int bytesRead = doc.Read(_state.HexBaseOffset, buf);

        long selStart = _state.HexSelStart;
        long selEnd = _state.HexSelEnd;

        // Collect visible search matches
        long viewStart = _state.HexBaseOffset;
        long viewEnd = _state.HexBaseOffset + bytesRead;
        List<SearchResult> visibleMatches = CollectVisibleMatches(viewStart, viewEnd);
        int currentMatchIdx = _state.CurrentMatchIndex;
        SearchResult? activeMatch = currentMatchIdx >= 0 && currentMatchIdx < _state.SearchResults.Count
            ? _state.SearchResults[currentMatchIdx]
            : null;

        string[] rows = new string[visibleRows];
        for (int row = 0; row < visibleRows; row++)
        {
            long offset = _state.HexBaseOffset + (long)row * bpr;
            if (offset >= doc.Length)
            {
                rows[row] = "~";
                continue;
            }

            int rowStart = row * bpr;
            int rowLen = Math.Min(bpr, bytesRead - rowStart);
            if (rowLen <= 0)
            {
                rows[row] = "~";
                continue;
            }

            ReadOnlySpan<byte> rowBytes = buf.Slice(rowStart, rowLen);
            rows[row] = AnsiBuilder.BuildHexRow(
                offset, rowBytes, bpr,
                _state.HexCursorOffset, selStart, selEnd,
                visibleMatches, activeMatch);
        }

        return rows;
    }

    // ─── Navigation ───

    internal void MoveCursor(int deltaBytes, bool extend)
    {
        if (_state.Document is null) return;
        long newOffset = Math.Clamp(
            _state.HexCursorOffset + deltaBytes,
            0, _state.Document.Length - 1);

        if (!extend)
            _state.HexSelectionAnchor = -1;
        else if (_state.HexSelectionAnchor < 0)
            _state.HexSelectionAnchor = _state.HexCursorOffset;

        _state.HexCursorOffset = newOffset;
        _state.NibbleLow = false;
    }

    internal void MoveLeft(bool extend) => MoveCursor(-1, extend);
    internal void MoveRight(bool extend) => MoveCursor(1, extend);
    internal void MoveUp(bool extend) => MoveCursor(-_state.BytesPerRow, extend);
    internal void MoveDown(bool extend) => MoveCursor(_state.BytesPerRow, extend);

    internal void PageUp(bool extend) =>
        MoveCursor(-_state.BytesPerRow * _state.VisibleRows, extend);

    internal void PageDown(bool extend) =>
        MoveCursor(_state.BytesPerRow * _state.VisibleRows, extend);

    internal void Home(bool extend)
    {
        if (_state.Document is null) return;
        long rowStart = (_state.HexCursorOffset / _state.BytesPerRow) * _state.BytesPerRow;
        long delta = rowStart - _state.HexCursorOffset;
        MoveCursor((int)delta, extend);
    }

    internal void End(bool extend)
    {
        if (_state.Document is null) return;
        long rowEnd = ((_state.HexCursorOffset / _state.BytesPerRow) + 1) * _state.BytesPerRow - 1;
        rowEnd = Math.Min(rowEnd, _state.Document.Length - 1);
        long delta = rowEnd - _state.HexCursorOffset;
        MoveCursor((int)delta, extend);
    }

    internal void CtrlHome(bool extend)
    {
        if (_state.Document is null) return;
        long delta = -_state.HexCursorOffset;
        MoveCursor((int)delta, extend);
    }

    internal void CtrlEnd(bool extend)
    {
        if (_state.Document is null) return;
        long delta = _state.Document.Length - 1 - _state.HexCursorOffset;
        MoveCursor((int)delta, extend);
    }

    internal void GotoOffset(long offset)
    {
        if (_state.Document is null) return;
        _state.HexCursorOffset = Math.Clamp(offset, 0, Math.Max(0, _state.Document.Length - 1));
        _state.HexSelectionAnchor = -1;
        _state.NibbleLow = false;
    }

    // ─── Editing ───

    internal void InputHexDigit(int digit)
    {
        Document? doc = _state.Document;
        if (doc is null || _state.HexCursorOffset < 0) return;
        if (_state.HexCursorOffset >= doc.Length) return;

        Span<byte> oneByte = stackalloc byte[1];
        doc.Read(_state.HexCursorOffset, oneByte);
        byte current = oneByte[0];

        byte newByte = _state.NibbleLow
            ? (byte)((current & 0xF0) | (digit & 0x0F))
            : (byte)((current & 0x0F) | (digit << 4));

        doc.Delete(_state.HexCursorOffset, 1);
        doc.Insert(_state.HexCursorOffset, [newByte]);

        if (_state.NibbleLow)
        {
            _state.NibbleLow = false;
            if (_state.HexCursorOffset < doc.Length - 1)
                _state.HexCursorOffset++;
        }
        else
        {
            _state.NibbleLow = true;
        }
    }

    internal void DeleteAtCursor()
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        if (_state.HexSelectionAnchor >= 0)
        {
            long start = _state.HexSelStart;
            long len = _state.HexSelEnd - start + 1;
            doc.Delete(start, len);
            _state.HexCursorOffset = start;
            _state.HexSelectionAnchor = -1;
        }
        else if (_state.HexCursorOffset < doc.Length)
        {
            doc.Delete(_state.HexCursorOffset, 1);
        }

        _state.NibbleLow = false;
        ClampCursor();
    }

    internal void BackspaceAtCursor()
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        if (_state.HexSelectionAnchor >= 0)
        {
            DeleteAtCursor();
            return;
        }

        if (_state.HexCursorOffset > 0)
        {
            _state.HexCursorOffset--;
            doc.Delete(_state.HexCursorOffset, 1);
        }

        _state.NibbleLow = false;
        ClampCursor();
    }

    // ─── Scrolling ───

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCursorVisible()
    {
        if (_state.HexCursorOffset < 0) return;

        long cursorRow = _state.HexCursorOffset / _state.BytesPerRow;
        long topRow = _state.HexBaseOffset / _state.BytesPerRow;

        if (cursorRow < topRow)
            _state.HexBaseOffset = cursorRow * _state.BytesPerRow;
        else if (cursorRow >= topRow + _state.VisibleRows)
            _state.HexBaseOffset = (cursorRow - _state.VisibleRows + 1) * _state.BytesPerRow;
    }

    internal void ScrollUp(int rows)
    {
        long newBase = _state.HexBaseOffset - (long)rows * _state.BytesPerRow;
        _state.HexBaseOffset = Math.Max(0, newBase);
    }

    internal void ScrollDown(int rows)
    {
        if (_state.Document is null) return;
        long maxBase = Math.Max(0, _state.Document.Length - (long)_state.BytesPerRow * _state.VisibleRows);
        long newBase = _state.HexBaseOffset + (long)rows * _state.BytesPerRow;
        _state.HexBaseOffset = Math.Min(maxBase, newBase);
    }

    // ─── Helpers ───

    private void ClampCursor()
    {
        if (_state.Document is null) return;
        _state.HexCursorOffset = Math.Clamp(
            _state.HexCursorOffset, 0,
            Math.Max(0, _state.Document.Length - 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureBuffer(int size)
    {
        if (_readBuffer.Length < size)
            _readBuffer = new byte[size];
    }

    /// <summary>
    /// Collects search matches that overlap the visible byte range [viewStart, viewEnd).
    /// </summary>
    private List<SearchResult> CollectVisibleMatches(long viewStart, long viewEnd)
    {
        List<SearchResult> results = _state.SearchResults;
        List<SearchResult> visible = [];
        if (results.Count == 0) return visible;

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
}
