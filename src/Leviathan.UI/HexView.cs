using Hexa.NET.ImGui;

using Leviathan.Core;

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Leviathan.UI;

/// <summary>
/// Renders a hex editor view using ImGui immediate-mode drawing.
/// Layout: [Offset] [Hex bytes N per row] [ASCII representation]
/// Zero-allocation in the hot path — works with stack-allocated buffers.
/// </summary>
public sealed class HexView
{
    private readonly Document _document;

    private long _baseOffset;       // first visible byte offset
    private int _bytesPerRow = 16;
    private int _visibleRows;
    private long _selectedOffset = -1; // cursor byte offset; -1 = no selection
    private long _selectionAnchor = -1; // anchor for range selection; -1 = no range
    private bool _nibbleLow;            // false = editing high nibble, true = low nibble

    // Scrollbar state: target row set by keyboard/goto, applied next frame
    private long _scrollTargetRow = -1;

    // Maximum virtual height in pixels to keep float precision reasonable.
    // 10M pixels ÷ ~16px line height ≈ 625K distinct row positions.
    // For a 50GB file at 16 bytes/row (3.36B rows), each distinct scrollbar
    // position maps to ~5,376 rows — imperceptible when dragging.
    private const float MaxVirtualHeight = 10_000_000f;

    // Pre-allocated read buffer — sized for up to 64 bytes/row × 1024 rows
    private byte[] _readBuffer = new byte[64 * 1024];

    // Hex lookup table for zero-allocation formatting
    private static ReadOnlySpan<byte> HexChars => "0123456789ABCDEF"u8;

    // Goto dialog state
    private bool _showGotoDialog;
    private readonly byte[] _gotoInputBuf = new byte[20];
    private bool _gotoFocusInput;

    /// <summary>Current first visible byte offset.</summary>
    public long BaseOffset => _baseOffset;

    /// <summary>Current bytes per row (read-only reflection of auto/fixed mode).</summary>
    public int CurrentBytesPerRow => _bytesPerRow;

    /// <summary>
    /// Desired bytes per row. 0 = auto (adapts to window width).
    /// Positive value = fixed (must be a multiple of 8, minimum 8).
    /// </summary>
    public int BytesPerRowSetting { get; set; }

    /// <summary>Current cursor byte offset (for Find dialog positioning).</summary>
    public long SelectedOffset => _selectedOffset;

    /// <summary>
    /// Jumps to the given offset and highlights it. Used by the Find dialog.
    /// </summary>
    public void JumpToMatch(long offset)
    {
        _selectedOffset = Math.Clamp(offset, 0, Math.Max(0, _document.Length - 1));
        _selectionAnchor = _selectedOffset;
        _nibbleLow = false;
        ScrollTo(offset);
    }

    public HexView(Document document)
    {
        _document = document;
    }

    /// <summary>
    /// Scrolls the view to the specified byte offset.
    /// </summary>
    public void ScrollTo(long offset)
    {
        long effectiveBpr = _bytesPerRow > 0 ? _bytesPerRow : 16;
        _baseOffset = Math.Clamp(offset - (offset % effectiveBpr), 0, Math.Max(0, _document.Length - 1));
        _scrollTargetRow = _baseOffset / effectiveBpr;
    }

    /// <summary>
    /// Opens the Goto Offset dialog.
    /// </summary>
    public void OpenGotoDialog()
    {
        _showGotoDialog = true;
        Array.Clear(_gotoInputBuf);
        _gotoFocusInput = true;
    }

    /// <summary>
    /// Renders the hex view into the current ImGui window.
    /// Must be called between ImGui.Begin/End.
    /// </summary>
    public unsafe void Render()
    {
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float charWidth = ImGui.CalcTextSize("F"u8).X;
        var outerAvail = ImGui.GetContentRegionAvail();

        // ── Dynamic bytes per row ──
        ComputeBytesPerRow(charWidth, outerAvail.X);

        // Ensure read buffer is large enough
        int maxReadSize = _visibleRows > 0 ? (_visibleRows + 2) * _bytesPerRow : 1024 * _bytesPerRow;
        if (_readBuffer.Length < maxReadSize)
            _readBuffer = new byte[maxReadSize];

        // ── Total rows for scrollbar ──
        long totalRows = _document.Length > 0
            ? (_document.Length + _bytesPerRow - 1) / _bytesPerRow
            : 1;

        float idealHeight = totalRows * lineHeight;
        float virtualHeight = Math.Min(idealHeight, MaxVirtualHeight);

        // ── Begin scrollable child window ──
        ImGui.BeginChild("##HexScroll"u8, outerAvail, ImGuiChildFlags.None,
            ImGuiWindowFlags.NoMove);

        var innerAvail = ImGui.GetContentRegionAvail();
        _visibleRows = Math.Max(1, (int)(innerAvail.Y / lineHeight));
        long maxFirstRow = Math.Max(0, totalRows - _visibleRows);
        float maxScrollY = virtualHeight - _visibleRows * lineHeight;
        if (maxScrollY < 1f) maxScrollY = 1f;

        // ── Handle keyboard input (must happen before we read scroll pos) ──
        HandleScrollInput(totalRows, maxFirstRow);

        // ── Apply programmatic scroll target ──
        if (_scrollTargetRow >= 0) {
            float targetScrollY = RowToScrollY(_scrollTargetRow, totalRows, maxFirstRow, idealHeight, maxScrollY, lineHeight);
            ImGui.SetScrollY(targetScrollY);
            _scrollTargetRow = -1;
        }

        // ── Derive base offset from scroll position ──
        float scrollY = ImGui.GetScrollY();
        long currentRow = ScrollYToRow(scrollY, totalRows, maxFirstRow, idealHeight, maxScrollY, lineHeight);
        currentRow = Math.Clamp(currentRow, 0, maxFirstRow);
        _baseOffset = currentRow * _bytesPerRow;

        // ── Read visible data ──
        int totalBytesToRead = _visibleRows * _bytesPerRow;
        long remaining = _document.Length - _baseOffset;
        totalBytesToRead = (int)Math.Min(totalBytesToRead, remaining > 0 ? remaining : 0);

        if (totalBytesToRead <= 0 && _document.Length == 0) {
            ImGui.TextUnformatted("(empty file)"u8);
            // Still set cursor for scroll range
            ImGui.SetCursorPosY(virtualHeight);
            ImGui.Dummy(new Vector2(0, 0));
            ImGui.EndChild();
            RenderGotoDialog();
            return;
        }

        int bytesRead = totalBytesToRead > 0
            ? _document.Read(_baseOffset, _readBuffer.AsSpan(0, totalBytesToRead))
            : 0;

        // Position ImGui cursor at the scroll offset so GetCursorScreenPos()
        // returns the top of the visible area, not the content origin.
        ImGui.SetCursorPosY(scrollY);
        var drawList = ImGui.GetWindowDrawList();
        Vector2 cursorStart = ImGui.GetCursorScreenPos();

        // ── Colors ──
        uint colOffset = ImGui.GetColorU32(new Vector4(0.5f, 0.7f, 1.0f, 1.0f));
        uint colHex = ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.9f, 1.0f));
        uint colHexZero = ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1.0f));
        uint colAscii = ImGui.GetColorU32(new Vector4(0.6f, 0.9f, 0.6f, 1.0f));
        uint colSeparator = ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 1.0f));
        uint colHighlight = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 0.5f));
        uint colSelection = ImGui.GetColorU32(new Vector4(0.2f, 0.4f, 0.8f, 0.4f));
        uint colNibbleCursor = ImGui.GetColorU32(new Vector4(0.9f, 0.6f, 0.1f, 0.7f));

        long selStart = _selectionAnchor >= 0 && _selectedOffset >= 0
            ? Math.Min(_selectionAnchor, _selectedOffset) : _selectedOffset;
        long selEnd = _selectionAnchor >= 0 && _selectedOffset >= 0
            ? Math.Max(_selectionAnchor, _selectedOffset) : _selectedOffset;

        // ── Layout columns ──
        float offsetColWidth = 19 * charWidth;
        int numGaps = _bytesPerRow > 8 ? (_bytesPerRow / 8 - 1) : 0;
        float hexColWidth = (_bytesPerRow * 3 + numGaps + 1) * charWidth;
        float asciiColStart = offsetColWidth + hexColWidth;

        // Draw separator
        float sepX = cursorStart.X + asciiColStart - charWidth;
        drawList.AddLine(
            new Vector2(sepX, cursorStart.Y),
            new Vector2(sepX, cursorStart.Y + _visibleRows * lineHeight),
            colSeparator);

        // Stack-allocated formatting buffers
        Span<byte> offsetBuf = stackalloc byte[20];
        Span<byte> hexByteBuf = stackalloc byte[4];
        // ASCII buffer: dynamic size up to max 64 bytes/row + null terminator
        int asciiBufSize = _bytesPerRow + 1;
        Span<byte> asciiBuf = asciiBufSize <= 128 ? stackalloc byte[asciiBufSize] : new byte[asciiBufSize];

        // ── Click hit-testing ──
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered()) {
            var mousePos = ImGui.GetMousePos();
            float relY = mousePos.Y - cursorStart.Y;
            float relX = mousePos.X - cursorStart.X;
            int clickRow = (int)(relY / lineHeight);
            int clickCol = -1;

            if (relX >= offsetColWidth && relX < asciiColStart - charWidth) {
                // Clicked in hex area
                float hexX = relX - offsetColWidth;
                // Account for 8-byte group gaps
                for (int c = 0; c < _bytesPerRow; c++) {
                    float colX = c * 3 * charWidth;
                    if (c >= 8) colX += ((c / 8) - 0) * charWidth; // gap every 8 bytes
                    int gapCount = c >= 8 ? (c / 8) : 0;
                    colX = c * 3 * charWidth + gapCount * charWidth;
                    if (hexX >= colX && hexX < colX + 3 * charWidth) {
                        clickCol = c;
                        break;
                    }
                }
            } else if (relX >= asciiColStart) {
                // Clicked in ASCII area
                clickCol = (int)((relX - asciiColStart) / charWidth);
            }

            if (clickRow >= 0 && clickRow < _visibleRows && clickCol >= 0 && clickCol < _bytesPerRow) {
                long clickedOffset = _baseOffset + clickRow * _bytesPerRow + clickCol;
                if (clickedOffset < _document.Length) {
                    var io2 = ImGui.GetIO();
                    if (io2.KeyShift && _selectedOffset >= 0) {
                        // Shift+click: extend selection
                        if (_selectionAnchor < 0) _selectionAnchor = _selectedOffset;
                        _selectedOffset = clickedOffset;
                    } else {
                        _selectedOffset = clickedOffset;
                        _selectionAnchor = clickedOffset;
                        _nibbleLow = false;
                    }
                } else {
                    _selectedOffset = -1;
                    _selectionAnchor = -1;
                }
            } else {
                _selectedOffset = -1;
                _selectionAnchor = -1;
            }
        }

        // ── Render rows ──
        for (int row = 0; row < _visibleRows; row++) {
            int rowStart = row * _bytesPerRow;
            if (rowStart >= bytesRead) break;

            int rowBytes = Math.Min(_bytesPerRow, bytesRead - rowStart);
            long rowOffset = _baseOffset + rowStart;

            float y = cursorStart.Y + row * lineHeight;

            // ── Offset column ──
            FormatOffset(rowOffset, offsetBuf);
            fixed (byte* pOffset = offsetBuf) {
                drawList.AddText(new Vector2(cursorStart.X, y), colOffset, pOffset);
            }

            // ── Hex bytes column ──
            for (int col = 0; col < _bytesPerRow; col++) {
                int gapCount = col >= 8 ? (col / 8) : 0;
                float xBase = cursorStart.X + offsetColWidth + col * 3 * charWidth + gapCount * charWidth;

                if (col < rowBytes) {
                    byte b = _readBuffer[rowStart + col];
                    long byteOffset = rowOffset + col;

                    // Selection range highlight in hex column
                    if (byteOffset >= selStart && byteOffset <= selEnd && selStart >= 0) {
                        uint hlColor = byteOffset == _selectedOffset ? colHighlight : colSelection;
                        drawList.AddRectFilled(
                            new Vector2(xBase, y),
                            new Vector2(xBase + 2 * charWidth, y + lineHeight),
                            hlColor);
                    }

                    // Nibble cursor: underline the active nibble position
                    if (byteOffset == _selectedOffset && _selectedOffset >= 0) {
                        float nibbleX = xBase + (_nibbleLow ? charWidth : 0);
                        drawList.AddLine(
                            new Vector2(nibbleX, y + lineHeight - 2),
                            new Vector2(nibbleX + charWidth, y + lineHeight - 2),
                            colNibbleCursor, 2f);
                    }

                    FormatHexByte(b, hexByteBuf);
                    uint color = b == 0 ? colHexZero : colHex;
                    fixed (byte* pHex = hexByteBuf) {
                        drawList.AddText(new Vector2(xBase, y), color, pHex);
                    }
                }
            }

            // ── ASCII column ──
            for (int col = 0; col < rowBytes; col++) {
                byte b = _readBuffer[rowStart + col];
                long byteOffset = rowOffset + col;

                // Selection range highlight in ASCII column
                if (byteOffset >= selStart && byteOffset <= selEnd && selStart >= 0) {
                    float asciiX = cursorStart.X + asciiColStart + col * charWidth;
                    uint hlColor = byteOffset == _selectedOffset ? colHighlight : colSelection;
                    drawList.AddRectFilled(
                        new Vector2(asciiX, y),
                        new Vector2(asciiX + charWidth, y + lineHeight),
                        hlColor);
                }

                asciiBuf[col] = (b >= 0x20 && b < 0x7F) ? b : (byte)'.';
            }
            asciiBuf[rowBytes] = 0;

            fixed (byte* pAscii = asciiBuf) {
                drawList.AddText(new Vector2(cursorStart.X + asciiColStart, y), colAscii, pAscii);
            }
        }

        // ── Set virtual content height for scrollbar range ──
        ImGui.SetCursorPosY(virtualHeight);
        ImGui.Dummy(new Vector2(0, 0));
        ImGui.EndChild();

        // ── Goto dialog (rendered outside the child window) ──
        RenderGotoDialog();
    }

    private void HandleScrollInput(long totalRows, long maxFirstRow)
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput) return;
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows)) return;
        if (_document.Length == 0) return;

        // Initialize cursor if not set
        if (_selectedOffset < 0)
            _selectedOffset = _baseOffset;

        long prev = _selectedOffset;

        // Numpad keys act as navigation only when NumLock is off.
        // When NumLock is on, a digit char is queued by GLFW — use that to detect the state.
        bool numpadAsNav;
        unsafe {
            bool hasDigitChar = false;
            ImVector<uint> q = io.InputQueueCharacters;
            for (int i = 0; i < q.Size; i++) {
                char c = (char)q.Data[i];
                if ((c >= '0' && c <= '9') || c == '.') { hasDigitChar = true; break; }
            }
            numpadAsNav = !hasDigitChar;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad6, true)))
            _selectedOffset += 1;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad4, true)))
            _selectedOffset -= 1;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad2, true)))
            _selectedOffset += _bytesPerRow;
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad8, true)))
            _selectedOffset -= _bytesPerRow;
        if (ImGui.IsKeyPressed(ImGuiKey.PageDown, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad3, true)))
            _selectedOffset += (long)_visibleRows * _bytesPerRow;
        if (ImGui.IsKeyPressed(ImGuiKey.PageUp, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad9, true)))
            _selectedOffset -= (long)_visibleRows * _bytesPerRow;

        if (ImGui.IsKeyPressed(ImGuiKey.Home, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad7, true))) {
            if (io.KeyCtrl)
                _selectedOffset = 0;
            else
                _selectedOffset -= _selectedOffset % _bytesPerRow;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.End, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad1, true))) {
            if (io.KeyCtrl)
                _selectedOffset = Math.Max(0, _document.Length - 1);
            else {
                long rowStart = _selectedOffset - (_selectedOffset % _bytesPerRow);
                _selectedOffset = Math.Min(rowStart + _bytesPerRow - 1, Math.Max(0, _document.Length - 1));
            }
        }

        // Ctrl+G = Goto offset
        if (ImGui.IsKeyPressed(ImGuiKey.G) && io.KeyCtrl)
            OpenGotoDialog();

        // ── Hex byte editing ──────────────────────────────────────────

        // Backspace: delete byte before cursor
        if (ImGui.IsKeyPressed(ImGuiKey.Backspace, true) && _selectedOffset > 0) {
            long target = _selectedOffset - 1;
            _document.Delete(target, 1);
            _selectedOffset = target;
            _selectionAnchor = target;
            _nibbleLow = false;
            EnsureSelectedVisible(maxFirstRow);
        }

        // Delete: delete byte at cursor
        if (ImGui.IsKeyPressed(ImGuiKey.Delete, true) && _selectedOffset >= 0
            && _selectedOffset < _document.Length) {
            _document.Delete(_selectedOffset, 1);
            _selectedOffset = Math.Clamp(_selectedOffset, 0, Math.Max(0, _document.Length - 1));
            _selectionAnchor = _selectedOffset;
            _nibbleLow = false;
            EnsureSelectedVisible(maxFirstRow);
        }

        // Hex digit entry (overwrite mode): 0–9 and A–F
        // Only when not holding Ctrl (to avoid hijacking Ctrl+G etc.)
        if (!io.KeyCtrl && !io.KeyAlt && _selectedOffset >= 0 && _selectedOffset < _document.Length) {
            int hexDigit = -1;
            if (ImGui.IsKeyPressed(ImGuiKey.Key0, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad0, true))) hexDigit = 0;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key1, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad1, true))) hexDigit = 1;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key2, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad2, true))) hexDigit = 2;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key3, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad3, true))) hexDigit = 3;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key4, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad4, true))) hexDigit = 4;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key5, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad5, true))) hexDigit = 5;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key6, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad6, true))) hexDigit = 6;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key7, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad7, true))) hexDigit = 7;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key8, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad8, true))) hexDigit = 8;
            else if (ImGui.IsKeyPressed(ImGuiKey.Key9, true) || (!numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad9, true))) hexDigit = 9;
            else if (ImGui.IsKeyPressed(ImGuiKey.A, true)) hexDigit = 10;
            else if (ImGui.IsKeyPressed(ImGuiKey.B, true)) hexDigit = 11;
            else if (ImGui.IsKeyPressed(ImGuiKey.C, true)) hexDigit = 12;
            else if (ImGui.IsKeyPressed(ImGuiKey.D, true)) hexDigit = 13;
            else if (ImGui.IsKeyPressed(ImGuiKey.E, true)) hexDigit = 14;
            else if (ImGui.IsKeyPressed(ImGuiKey.F, true)) hexDigit = 15;

            if (hexDigit >= 0) {
                // Read current byte, modify the active nibble, overwrite
                Span<byte> oneByte = stackalloc byte[1];
                if (_document.Read(_selectedOffset, oneByte) == 1) {
                    byte current = oneByte[0];
                    byte newByte = _nibbleLow
                        ? (byte)((current & 0xF0) | (hexDigit & 0x0F))
                        : (byte)((current & 0x0F) | ((hexDigit & 0x0F) << 4));

                    _document.Delete(_selectedOffset, 1);
                    oneByte[0] = newByte;
                    _document.Insert(_selectedOffset, oneByte);

                    // Advance nibble or byte
                    if (_nibbleLow) {
                        _nibbleLow = false;
                        _selectedOffset = Math.Clamp(_selectedOffset + 1, 0, Math.Max(0, _document.Length - 1));
                        _selectionAnchor = _selectedOffset;
                        EnsureSelectedVisible(maxFirstRow);
                    } else {
                        _nibbleLow = true;
                    }
                }
            }
        }

        _selectedOffset = Math.Clamp(_selectedOffset, 0, Math.Max(0, _document.Length - 1));

        if (_selectedOffset != prev) {
            // Shift extends selection anchor; plain movement resets it and nibble cursor
            if (io.KeyShift) {
                if (_selectionAnchor < 0) _selectionAnchor = prev;
            } else {
                _selectionAnchor = _selectedOffset;
                _nibbleLow = false;
            }
            EnsureSelectedVisible(maxFirstRow);
        }
    }

    private void EnsureSelectedVisible(long maxFirstRow)
    {
        long cursorRow = _selectedOffset / _bytesPerRow;
        long topRow = _baseOffset / _bytesPerRow;
        long bottomRow = topRow + _visibleRows - 1;

        if (cursorRow < topRow) {
            _baseOffset = cursorRow * _bytesPerRow;
            _scrollTargetRow = cursorRow;
        } else if (cursorRow > bottomRow) {
            long newTopRow = Math.Clamp(cursorRow - _visibleRows + 1, 0, maxFirstRow);
            _baseOffset = newTopRow * _bytesPerRow;
            _scrollTargetRow = newTopRow;
        }
    }

    private unsafe void RenderGotoDialog()
    {
        if (_showGotoDialog) {
            ImGui.OpenPopup("Goto Offset"u8);
            _showGotoDialog = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal("Goto Offset"u8, ref open, ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.Text("Enter offset (hex, e.g. 1A2B or 0x1A2B):"u8);

            if (_gotoFocusInput) {
                ImGui.SetKeyboardFocusHere();
                _gotoFocusInput = false;
            }

            bool enter;
            fixed (byte* pBuf = _gotoInputBuf) {
                enter = ImGui.InputText("##gotoInput"u8, pBuf, (nuint)_gotoInputBuf.Length,
                    ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.EnterReturnsTrue);
            }

            bool doGoto = enter || ImGui.Button("Go"u8);
            ImGui.SameLine();
            bool cancel = ImGui.Button("Cancel"u8);

            if (doGoto) {
                string input = System.Text.Encoding.ASCII.GetString(_gotoInputBuf).TrimEnd('\0').Trim();
                if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    input = input[2..];

                if (long.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out long offset)) {
                    ScrollTo(offset);
                    _selectedOffset = offset < _document.Length ? offset : -1;
                }
                ImGui.CloseCurrentPopup();
            }
            if (cancel || ImGui.IsKeyPressed(ImGuiKey.Escape))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    private void ComputeBytesPerRow(float charWidth, float availWidth)
    {
        if (BytesPerRowSetting > 0) {
            _bytesPerRow = BytesPerRowSetting;
            return;
        }

        // Auto mode: fit as many columns as possible (multiples of 8)
        // Layout per byte: 3 chars hex + 1 char ASCII = 4 charWidths
        // Fixed overhead: 19 chars (offset) + 1 char (separator) + group gaps
        // Group gap: 1 char per 8-byte group boundary
        // Approximate: availWidth = offsetCol + bytesPerRow * 4 * charWidth + (bytesPerRow/8) * charWidth + separatorGap
        // Solve for bytesPerRow:
        float overhead = 20 * charWidth; // 19 offset + 1 separator
        float perByte = 4 * charWidth;   // 3 hex + 1 ascii
        float perGroup = charWidth;      // gap every 8 bytes

        // bytesPerRow * perByte + (bytesPerRow/8) * perGroup = availWidth - overhead
        // bytesPerRow * (perByte + perGroup/8) = availWidth - overhead
        float effectivePerByte = perByte + perGroup / 8f;
        int maxCols = (int)((availWidth - overhead) / effectivePerByte);
        maxCols = (maxCols / 8) * 8; // snap to multiple of 8
        _bytesPerRow = Math.Max(8, maxCols);
    }

    // ── Scroll position mapping (handles float precision for huge files) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ScrollYToRow(float scrollY, long totalRows, long maxFirstRow,
        float idealHeight, float maxScrollY, float lineHeight)
    {
        if (idealHeight <= MaxVirtualHeight)
            return (long)(scrollY / lineHeight);

        double fraction = scrollY / maxScrollY;
        return (long)(fraction * maxFirstRow);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float RowToScrollY(long row, long totalRows, long maxFirstRow,
        float idealHeight, float maxScrollY, float lineHeight)
    {
        if (idealHeight <= MaxVirtualHeight)
            return row * lineHeight;

        double fraction = maxFirstRow > 0 ? (double)row / maxFirstRow : 0;
        return (float)(fraction * maxScrollY);
    }

    /// <summary>
    /// Formats a 64-bit offset as "XXXXXXXX:XXXXXXXX" (null-terminated).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FormatOffset(long offset, Span<byte> buf)
    {
        ulong v = (ulong)offset;
        uint hi = (uint)(v >> 32);
        uint lo = (uint)v;

        buf[0] = HexChars[(int)(hi >> 28) & 0xF];
        buf[1] = HexChars[(int)(hi >> 24) & 0xF];
        buf[2] = HexChars[(int)(hi >> 20) & 0xF];
        buf[3] = HexChars[(int)(hi >> 16) & 0xF];
        buf[4] = HexChars[(int)(hi >> 12) & 0xF];
        buf[5] = HexChars[(int)(hi >> 8) & 0xF];
        buf[6] = HexChars[(int)(hi >> 4) & 0xF];
        buf[7] = HexChars[(int)hi & 0xF];
        buf[8] = (byte)':';
        buf[9] = HexChars[(int)(lo >> 28) & 0xF];
        buf[10] = HexChars[(int)(lo >> 24) & 0xF];
        buf[11] = HexChars[(int)(lo >> 20) & 0xF];
        buf[12] = HexChars[(int)(lo >> 16) & 0xF];
        buf[13] = HexChars[(int)(lo >> 12) & 0xF];
        buf[14] = HexChars[(int)(lo >> 8) & 0xF];
        buf[15] = HexChars[(int)(lo >> 4) & 0xF];
        buf[16] = HexChars[(int)lo & 0xF];
        buf[17] = 0;
    }

    /// <summary>
    /// Formats a byte as "XX " (null-terminated).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FormatHexByte(byte value, Span<byte> buf)
    {
        buf[0] = HexChars[value >> 4];
        buf[1] = HexChars[value & 0xF];
        buf[2] = (byte)' ';
        buf[3] = 0;
    }
}
