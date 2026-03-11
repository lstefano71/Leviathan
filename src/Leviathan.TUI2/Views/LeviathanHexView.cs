using System.Runtime.CompilerServices;
using System.Text;
using Leviathan.Core;
using Leviathan.Core.Search;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Leviathan.TUI2.Views;

/// <summary>
/// Custom hex editor view using Leviathan's Document.Read(offset, Span&lt;byte&gt;) directly.
/// Modeled on Terminal.Gui's HexView patterns for key/mouse bindings and drawing,
/// but uses our zero-copy architecture for 50 GB+ file support.
/// </summary>
internal sealed class LeviathanHexView : View
{
    private readonly AppState _state;
    private byte[] _readBuffer = new byte[64 * 1024];

    private const int AddressWidth = 10; // "XXXXXXXX: "
    private const int AsciiSeparatorWidth = 2; // " |"

    /// <summary>
    /// Fired when the view needs the status bar to update (cursor moved, edit, etc.).
    /// </summary>
    internal event Action? StateChanged;

    internal LeviathanHexView(AppState state)
    {
        _state = state;
        CanFocus = true;

        // Scrolling setup
        ContentSizeTracksViewport = false;
        ViewportSettings |= ViewportSettingsFlags.AllowYGreaterThanContentHeight;

        SetupCommands();
        SetupKeyBindings();
        SetupMouseBindings();
    }

    // ─── Commands ───

    private void SetupCommands()
    {
        AddCommand(Command.Left, () => { MoveLeft(false); return true; });
        AddCommand(Command.Right, () => { MoveRight(false); return true; });
        AddCommand(Command.Up, () => { MoveUp(false); return true; });
        AddCommand(Command.Down, () => { MoveDown(false); return true; });
        AddCommand(Command.PageUp, () => { PageUp(false); return true; });
        AddCommand(Command.PageDown, () => { PageDown(false); return true; });
        AddCommand(Command.Start, () => { CtrlHome(false); return true; });
        AddCommand(Command.End, () => { CtrlEnd(false); return true; });
        AddCommand(Command.LeftStart, () => { Home(false); return true; });
        AddCommand(Command.RightEnd, () => { End(false); return true; });
        AddCommand(Command.ScrollUp, () => { ScrollUp(3); return true; });
        AddCommand(Command.ScrollDown, () => { ScrollDown(3); return true; });
        AddCommand(Command.DeleteCharLeft, () => { BackspaceAtCursor(); return true; });
        AddCommand(Command.DeleteCharRight, () => { DeleteAtCursor(); return true; });
    }

    private void SetupKeyBindings()
    {
        KeyBindings.Add(Key.CursorLeft, Command.Left);
        KeyBindings.Add(Key.CursorRight, Command.Right);
        KeyBindings.Add(Key.CursorUp, Command.Up);
        KeyBindings.Add(Key.CursorDown, Command.Down);
        KeyBindings.Add(Key.PageUp, Command.PageUp);
        KeyBindings.Add(Key.PageDown, Command.PageDown);
        KeyBindings.Add(Key.Home.WithCtrl, Command.Start);
        KeyBindings.Add(Key.End.WithCtrl, Command.End);
        KeyBindings.Add(Key.Home, Command.LeftStart);
        KeyBindings.Add(Key.End, Command.RightEnd);
        KeyBindings.Add(Key.Backspace, Command.DeleteCharLeft);
        KeyBindings.Add(Key.Delete, Command.DeleteCharRight);

        // Shift+nav for selection
        KeyBindings.Add(Key.CursorLeft.WithShift, Command.Left);
        KeyBindings.Add(Key.CursorRight.WithShift, Command.Right);
        KeyBindings.Add(Key.CursorUp.WithShift, Command.Up);
        KeyBindings.Add(Key.CursorDown.WithShift, Command.Down);

        // Remove keys that conflict with menu/app
        KeyBindings.Remove(Key.Space);
        KeyBindings.Remove(Key.Enter);
    }

    private void SetupMouseBindings()
    {
        MouseBindings.ReplaceCommands(MouseFlags.LeftButtonClicked, Command.Activate);
        MouseBindings.Add(MouseFlags.WheeledUp, Command.ScrollUp);
        MouseBindings.Add(MouseFlags.WheeledDown, Command.ScrollDown);
    }

    // ─── Key Input (hex digits) ───

    /// <inheritdoc/>
    protected override bool OnKeyDownNotHandled(Key keyEvent)
    {
        if (_state.Document is null) return false;

        // Check for shift+arrow (selection extension)
        bool extend = keyEvent.IsShift;
        if (extend)
        {
            if (keyEvent.KeyCode is (KeyCode.CursorLeft | KeyCode.ShiftMask))
            { MoveLeft(true); return true; }
            if (keyEvent.KeyCode is (KeyCode.CursorRight | KeyCode.ShiftMask))
            { MoveRight(true); return true; }
            if (keyEvent.KeyCode is (KeyCode.CursorUp | KeyCode.ShiftMask))
            { MoveUp(true); return true; }
            if (keyEvent.KeyCode is (KeyCode.CursorDown | KeyCode.ShiftMask))
            { MoveDown(true); return true; }
        }

        // Hex digit input
        int digit = HexDigitFromKey(keyEvent);
        if (digit >= 0)
        {
            InputHexDigit(digit);
            return true;
        }

        return false;
    }

    private static int HexDigitFromKey(Key key)
    {
        char c = (char)(key.KeyCode & ~KeyCode.ShiftMask & ~KeyCode.CtrlMask & ~KeyCode.AltMask);
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };
    }

    // ─── Mouse ───

    /// <inheritdoc/>
    protected override bool OnActivating(CommandEventArgs args)
    {
        if (args.Context?.Binding is not MouseBinding { MouseEvent: { } mouse })
            return base.OnActivating(args);

        if (!HasFocus)
            SetFocus();

        System.Drawing.Point pos = mouse.Position!.Value;
        ClickAtPosition(pos.Y, pos.X, extend: mouse.Flags.HasFlag(MouseFlags.Shift));
        return true;
    }

    // ─── Drawing ───

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        Document? doc = _state.Document;
        if (doc is null)
        {
            SetAttributeForRole(VisualRole.Normal);
            Move(0, 0);
            AddStr("No file open");
            return true;
        }

        int bpr = _state.BytesPerRow;
        int vpHeight = Viewport.Height;
        int vpWidth = Viewport.Width;
        _state.VisibleRows = vpHeight;

        EnsureCursorVisible();

        int totalBytes = vpHeight * bpr;
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

        // Color attributes
        Attribute normalAttr = new(new Color(StandardColor.White), new Color(StandardColor.Black));
        Attribute addressAttr = new(new Color(StandardColor.DarkGray), new Color(StandardColor.Black));
        Attribute cursorAttr = new(new Color(StandardColor.Black), new Color(208, 135, 46)); // Orange
        Attribute selectionAttr = new(new Color(StandardColor.White), new Color(30, 80, 160)); // Blue
        Attribute matchAttr = new(new Color(StandardColor.Black), new Color(StandardColor.Yellow));
        Attribute activeMatchAttr = new(new Color(StandardColor.Black), new Color(255, 165, 0)); // Bright orange
        Attribute asciiAttr = new(new Color(StandardColor.Cyan), new Color(StandardColor.Black));
        Attribute nonPrintableAttr = new(new Color(StandardColor.DarkGray), new Color(StandardColor.Black));
        Attribute separatorAttr = new(new Color(StandardColor.DarkGray), new Color(StandardColor.Black));

        for (int row = 0; row < vpHeight; row++)
        {
            long rowOffset = _state.HexBaseOffset + (long)row * bpr;
            Move(0, row);

            if (rowOffset >= doc.Length)
            {
                SetAttribute(normalAttr);
                AddStr("~");
                // Clear rest of line
                for (int c = 1; c < vpWidth; c++)
                    AddRune(' ');
                continue;
            }

            int rowStart = row * bpr;
            int rowLen = Math.Min(bpr, bytesRead - rowStart);

            // Address column
            SetAttribute(addressAttr);
            Span<char> addrChars = stackalloc char[9];
            FormatAddress(rowOffset, addrChars);
            foreach (char ac in addrChars)
                AddRune(ac);

            // Hex bytes
            int hexCol = AddressWidth;
            for (int b = 0; b < bpr; b++)
            {
                // Group separator every 8 bytes
                if (b > 0 && b % 8 == 0)
                {
                    SetAttribute(separatorAttr);
                    AddRune(' ');
                    hexCol++;
                }

                if (b < rowLen)
                {
                    long byteOffset = rowOffset + b;
                    byte val = buf[rowStart + b];

                    Attribute attr = GetByteAttribute(
                        byteOffset, selStart, selEnd, visibleMatches, activeMatch,
                        normalAttr, cursorAttr, selectionAttr, matchAttr, activeMatchAttr);

                    SetAttribute(attr);
                    AddRune(HexChars[(val >> 4) & 0xF]);
                    AddRune(HexChars[val & 0xF]);
                }
                else
                {
                    SetAttribute(normalAttr);
                    AddRune(' ');
                    AddRune(' ');
                }

                SetAttribute(normalAttr);
                AddRune(' ');
                hexCol += 3;
            }

            // ASCII separator
            SetAttribute(separatorAttr);
            AddRune('│');

            // ASCII column
            for (int b = 0; b < bpr; b++)
            {
                if (b < rowLen)
                {
                    long byteOffset = rowOffset + b;
                    byte val = buf[rowStart + b];

                    Attribute attr = GetByteAttribute(
                        byteOffset, selStart, selEnd, visibleMatches, activeMatch,
                        asciiAttr, cursorAttr, selectionAttr, matchAttr, activeMatchAttr);

                    // Use non-printable color for control chars
                    if (val < 0x20 || val == 0x7F)
                    {
                        if (attr == asciiAttr)
                            attr = nonPrintableAttr;
                    }

                    SetAttribute(attr);
                    char display = (val >= 0x20 && val < 0x7F) ? (char)val : '.';
                    AddRune(display);
                }
                else
                {
                    SetAttribute(normalAttr);
                    AddRune(' ');
                }
            }
        }

        return true;
    }

    private static readonly char[] HexChars = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F'];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FormatAddress(long offset, Span<char> dest)
    {
        // Format as "XXXXXXXX: " (10 chars total, but 9 chars for 8 hex + colon)
        for (int i = 7; i >= 0; i--)
        {
            dest[i] = HexChars[(int)(offset & 0xF)];
            offset >>= 4;
        }
        dest[8] = ':';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Attribute GetByteAttribute(
        long byteOffset, long selStart, long selEnd,
        List<SearchResult> visibleMatches, SearchResult? activeMatch,
        Attribute normalAttr, Attribute cursorAttr, Attribute selectionAttr,
        Attribute matchAttr, Attribute activeMatchAttr)
    {
        if (byteOffset == _state.HexCursorOffset)
            return cursorAttr;

        if (selStart >= 0 && byteOffset >= selStart && byteOffset <= selEnd)
            return selectionAttr;

        // Active match highlight
        if (activeMatch is { } am &&
            byteOffset >= am.Offset && byteOffset < am.Offset + am.Length)
            return activeMatchAttr;

        // Regular match highlight
        for (int i = 0; i < visibleMatches.Count; i++)
        {
            SearchResult m = visibleMatches[i];
            if (byteOffset >= m.Offset && byteOffset < m.Offset + m.Length)
                return matchAttr;
        }

        return normalAttr;
    }

    // ─── Navigation ───

    internal void MoveCursor(int deltaBytes, bool extend)
    {
        if (_state.Document is null) return;
        long newOffset = Math.Clamp(
            _state.HexCursorOffset + deltaBytes,
            0, Math.Max(0, _state.Document.Length - 1));

        if (!extend)
            _state.HexSelectionAnchor = -1;
        else if (_state.HexSelectionAnchor < 0)
            _state.HexSelectionAnchor = _state.HexCursorOffset;

        _state.HexCursorOffset = newOffset;
        _state.NibbleLow = false;
        OnStateChanged();
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
        OnStateChanged();
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

        OnStateChanged();
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
        OnStateChanged();
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
        OnStateChanged();
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
        SetNeedsDraw();
    }

    internal void ScrollDown(int rows)
    {
        if (_state.Document is null) return;
        long maxBase = Math.Max(0, _state.Document.Length - (long)_state.BytesPerRow * _state.VisibleRows);
        long newBase = _state.HexBaseOffset + (long)rows * _state.BytesPerRow;
        _state.HexBaseOffset = Math.Min(maxBase, newBase);
        SetNeedsDraw();
    }

    // ─── Clipboard ───

    private const long MaxCopyBytes = 10 * 1024 * 1024;

    internal string? CopySelection()
    {
        Document? doc = _state.Document;
        if (doc is null || _state.HexSelectionAnchor < 0) return null;

        long start = _state.HexSelStart;
        long len = _state.HexSelEnd - start + 1;
        if (len <= 0 || len > MaxCopyBytes) return null;

        byte[] copyBuf = new byte[(int)len];
        int read = doc.Read(start, copyBuf);
        if (read <= 0) return null;

        StringBuilder sb = new(read * 3 - 1);
        for (int i = 0; i < read; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(copyBuf[i].ToString("X2"));
        }
        return sb.ToString();
    }

    internal void Paste(string text)
    {
        Document? doc = _state.Document;
        if (doc is null || string.IsNullOrEmpty(text)) return;

        byte[]? bytes = ParseHexString(text);
        if (bytes is null || bytes.Length == 0) return;

        if (_state.HexSelectionAnchor >= 0)
        {
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
        OnStateChanged();
    }

    internal void SelectAll()
    {
        Document? doc = _state.Document;
        if (doc is null || doc.Length == 0) return;

        _state.HexSelectionAnchor = 0;
        _state.HexCursorOffset = doc.Length - 1;
        _state.NibbleLow = false;
        OnStateChanged();
    }

    // ─── Mouse click ───

    internal void ClickAtPosition(int viewRow, int viewCol, bool extend = false)
    {
        Document? doc = _state.Document;
        if (doc is null) return;

        long? offset = PositionToOffset(viewRow, viewCol);
        if (offset is null) return;

        if (!extend)
            _state.HexSelectionAnchor = -1;
        else if (_state.HexSelectionAnchor < 0)
            _state.HexSelectionAnchor = _state.HexCursorOffset;

        _state.HexCursorOffset = offset.Value;
        _state.NibbleLow = false;
        OnStateChanged();
    }

    internal long? PositionToOffset(int viewRow, int viewCol)
    {
        Document? doc = _state.Document;
        if (doc is null) return null;

        int bpr = _state.BytesPerRow;
        if (bpr <= 0) return null;

        long rowOffset = _state.HexBaseOffset + (long)viewRow * bpr;
        if (rowOffset >= doc.Length) return null;

        int hexWidth = bpr * 3 + (bpr > 0 ? (bpr - 1) / 8 : 0);
        int asciiStart = AddressWidth + hexWidth + 1; // +1 for the │ separator

        int byteIndex;
        if (viewCol < AddressWidth)
        {
            byteIndex = 0;
        }
        else if (viewCol < AddressWidth + hexWidth)
        {
            int hexCol = viewCol - AddressWidth;
            byteIndex = HexColToByteIndex(hexCol, bpr);
        }
        else if (viewCol >= asciiStart && viewCol < asciiStart + bpr)
        {
            byteIndex = viewCol - asciiStart;
        }
        else
        {
            return null;
        }

        byteIndex = Math.Clamp(byteIndex, 0, bpr - 1);
        long newOffset = rowOffset + byteIndex;
        if (newOffset >= doc.Length) newOffset = doc.Length - 1;
        return newOffset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int HexColToByteIndex(int hexCol, int bytesPerRow)
    {
        int col = 0;
        for (int b = 0; b < bytesPerRow; b++)
        {
            if (b > 0 && b % 8 == 0)
                col++;
            if (hexCol < col + 3)
                return b;
            col += 3;
        }
        return bytesPerRow - 1;
    }

    // ─── Layout ───

    /// <summary>
    /// Recalculates bytes per row when the viewport changes.
    /// </summary>
    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);
        int newBpr = _state.ComputeBytesPerRow(Viewport.Width);
        if (newBpr != _state.BytesPerRow)
        {
            _state.BytesPerRow = newBpr;
            SetNeedsDraw();
        }
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

    private void OnStateChanged()
    {
        SetNeedsDraw();
        StateChanged?.Invoke();
    }

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

    private static byte[]? ParseHexString(string text)
    {
        List<byte> result = [];
        int nibbleCount = 0;
        int current = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ' ' or '\t' or '\r' or '\n' or '-' or ':')
            {
                if (nibbleCount == 1) return null;
                continue;
            }

            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };

            if (digit < 0) return null;

            current = (current << 4) | digit;
            nibbleCount++;

            if (nibbleCount == 2)
            {
                result.Add((byte)current);
                current = 0;
                nibbleCount = 0;
            }
        }

        if (nibbleCount != 0) return null;
        return result.ToArray();
    }
}
