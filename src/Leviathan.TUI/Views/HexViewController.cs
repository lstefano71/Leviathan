using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.TUI.Rendering;

using System.Runtime.CompilerServices;
using System.Text;

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
    for (int row = 0; row < visibleRows; row++) {
      long offset = _state.HexBaseOffset + (long)row * bpr;
      if (offset >= doc.Length) {
        rows[row] = "~";
        continue;
      }

      int rowStart = row * bpr;
      int rowLen = Math.Min(bpr, bytesRead - rowStart);
      if (rowLen <= 0) {
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

    if (_state.NibbleLow) {
      _state.NibbleLow = false;
      if (_state.HexCursorOffset < doc.Length - 1)
        _state.HexCursorOffset++;
    } else {
      _state.NibbleLow = true;
    }
  }

  internal void DeleteAtCursor()
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    if (_state.HexSelectionAnchor >= 0) {
      long start = _state.HexSelStart;
      long len = _state.HexSelEnd - start + 1;
      doc.Delete(start, len);
      _state.HexCursorOffset = start;
      _state.HexSelectionAnchor = -1;
    } else if (_state.HexCursorOffset < doc.Length) {
      doc.Delete(_state.HexCursorOffset, 1);
    }

    _state.NibbleLow = false;
    ClampCursor();
  }

  internal void BackspaceAtCursor()
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    if (_state.HexSelectionAnchor >= 0) {
      DeleteAtCursor();
      return;
    }

    if (_state.HexCursorOffset > 0) {
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

  // ─── Clipboard ───

  /// <summary>
  /// Maximum number of bytes that can be copied to the clipboard.
  /// </summary>
  private const long MaxCopyBytes = 10 * 1024 * 1024;

  /// <summary>
  /// Copies the selected bytes as a space-separated uppercase hex string (e.g. "DE AD BE EF").
  /// Returns null if there is no selection or the selection exceeds the size cap.
  /// </summary>
  internal string? CopySelection()
  {
    Document? doc = _state.Document;
    if (doc is null || _state.HexSelectionAnchor < 0) return null;

    long start = _state.HexSelStart;
    long len = _state.HexSelEnd - start + 1;
    if (len <= 0 || len > MaxCopyBytes) return null;

    byte[] buf = new byte[(int)len];
    int read = doc.Read(start, buf);
    if (read <= 0) return null;

    // "XX" per byte + " " separator between bytes → 3*N - 1 chars
    StringBuilder sb = new(read * 3 - 1);
    for (int i = 0; i < read; i++) {
      if (i > 0) sb.Append(' ');
      sb.Append(buf[i].ToString("X2"));
    }
    return sb.ToString();
  }

  /// <summary>
  /// Parses a hex string (flexible whitespace/separators) and inserts the resulting bytes
  /// at the cursor. If a selection is active it is deleted first.
  /// Accepted formats: "DEADBEEF", "DE AD BE EF", "DE-AD-BE-EF".
  /// </summary>
  internal void Paste(string text)
  {
    Document? doc = _state.Document;
    if (doc is null || string.IsNullOrEmpty(text)) return;

    byte[]? bytes = ParseHexString(text);
    if (bytes is null || bytes.Length == 0) return;

    // Delete selection if active
    if (_state.HexSelectionAnchor >= 0) {
      long start = _state.HexSelStart;
      long len = _state.HexSelEnd - start + 1;
      doc.Delete(start, len);
      _state.HexCursorOffset = start;
      _state.HexSelectionAnchor = -1;
    }

    doc.Insert(_state.HexCursorOffset, bytes);
    _state.HexCursorOffset += bytes.Length;
    _state.NibbleLow = false;
    ClampCursor();
  }

  // ─── Selection ───

  /// <summary>
  /// Selects the entire document (anchor = 0, cursor = last byte).
  /// </summary>
  internal void SelectAll()
  {
    Document? doc = _state.Document;
    if (doc is null || doc.Length == 0) return;

    _state.HexSelectionAnchor = 0;
    _state.HexCursorOffset = doc.Length - 1;
    _state.NibbleLow = false;
  }

  // ─── Mouse ───

  /// <summary>
  /// Moves the cursor to the byte offset corresponding to the given screen position.
  /// When <paramref name="extend"/> is true, the selection anchor is preserved (drag select).
  /// </summary>
  internal void ClickAtPosition(int viewRow, int viewCol, bool extend = false)
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    long? offset = PositionToOffset(viewRow, viewCol);
    if (offset is null) return;

    if (!extend) {
      _state.HexSelectionAnchor = -1;
    } else if (_state.HexSelectionAnchor < 0) {
      _state.HexSelectionAnchor = _state.HexCursorOffset;
    }

    _state.HexCursorOffset = offset.Value;
    _state.NibbleLow = false;
  }

  /// <summary>
  /// Maps screen (row, col) to a document byte offset, or null if outside data area.
  /// Hex row layout: [offset 17+1] [hex: 3 per byte + 1 per 8-byte group] [│1] [ascii: 1 per byte]
  /// </summary>
  internal long? PositionToOffset(int viewRow, int viewCol)
  {
    Document? doc = _state.Document;
    if (doc is null) return null;

    int bpr = _state.BytesPerRow;
    if (bpr <= 0) return null;

    long rowOffset = _state.HexBaseOffset + (long)viewRow * bpr;
    if (rowOffset >= doc.Length) return null;

    const int OffsetWidth = 18;
    int hexWidth = bpr * 3 + (bpr > 0 ? (bpr - 1) / 8 : 0);
    int asciiStart = OffsetWidth + hexWidth + 1;

    int byteIndex;
    if (viewCol < OffsetWidth) {
      byteIndex = 0;
    } else if (viewCol < OffsetWidth + hexWidth) {
      int hexCol = viewCol - OffsetWidth;
      byteIndex = HexColToByteIndex(hexCol, bpr);
    } else if (viewCol >= asciiStart && viewCol < asciiStart + bpr) {
      byteIndex = viewCol - asciiStart;
    } else {
      return null;
    }

    byteIndex = Math.Clamp(byteIndex, 0, bpr - 1);
    long newOffset = rowOffset + byteIndex;
    if (newOffset >= doc.Length) newOffset = doc.Length - 1;
    return newOffset;
  }

  /// <summary>
  /// Maps a column position within the hex area to a byte index.
  /// Layout per byte: "XX " (3 chars), with an extra space between every 8-byte group.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  internal static int HexColToByteIndex(int hexCol, int bytesPerRow)
  {
    // Walk the layout to find the byte
    int col = 0;
    for (int b = 0; b < bytesPerRow; b++) {
      if (b > 0 && b % 8 == 0)
        col++; // group separator

      // This byte occupies cols [col..col+2] (XX + space)
      if (hexCol < col + 3)
        return b;
      col += 3;
    }
    return bytesPerRow - 1;
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
    while (lo < hi) {
      int mid = lo + (hi - lo) / 2;
      if (results[mid].Offset + results[mid].Length <= viewStart)
        lo = mid + 1;
      else
        hi = mid;
    }

    for (int i = lo; i < results.Count; i++) {
      SearchResult m = results[i];
      if (m.Offset >= viewEnd) break;
      if (m.Offset + m.Length > viewStart)
        visible.Add(m);
    }
    return visible;
  }

  /// <summary>
  /// Parses a hex string into bytes. Accepts "DEADBEEF", "DE AD BE EF", "DE-AD-BE-EF",
  /// and mixed whitespace/separators. Returns null if the input contains invalid hex.
  /// </summary>
  private static byte[]? ParseHexString(string text)
  {
    List<byte> result = [];
    int nibbleCount = 0;
    int current = 0;

    for (int i = 0; i < text.Length; i++) {
      char c = text[i];

      // Skip whitespace and common separators
      if (c is ' ' or '\t' or '\r' or '\n' or '-' or ':') {
        // If we have a dangling nibble, it's invalid
        if (nibbleCount == 1) return null;
        continue;
      }

      int digit = c switch {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
      };

      if (digit < 0) return null;

      current = (current << 4) | digit;
      nibbleCount++;

      if (nibbleCount == 2) {
        result.Add((byte)current);
        current = 0;
        nibbleCount = 0;
      }
    }

    // Dangling nibble means invalid input
    if (nibbleCount != 0) return null;

    return result.ToArray();
  }
}
