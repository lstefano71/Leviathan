using Hexa.NET.ImGui;

using Leviathan.Core;
using Leviathan.Core.Text;

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Leviathan.UI;

/// <summary>
/// Renders a text editor view using ImGui immediate-mode drawing.
/// Features multi-encoding decoding via <see cref="ITextDecoder"/>,
/// JIT line wrapping for the visible viewport, and zero-allocation rendering.
/// </summary>
public sealed class TextView
{
    private readonly Document _document;
    private readonly LineWrapEngine _wrapEngine;
    private ITextDecoder _decoder;

    /// <summary>The active text decoder. Setting this switches the encoding interpretation.</summary>
    public ITextDecoder Decoder {
        get => _decoder;
        set {
            _decoder = value;
            InvalidateLineCache();
        }
    }

    private long _topDocOffset;         // document byte offset at top of viewport
    private int _visibleRows;
    private bool _wordWrap = true;
    private int _tabWidth = 4;

    // Scrollbar / navigation
    private long _scrollTargetLine = -1; // logical line to scroll to next frame
    private long _estimatedTotalLines;   // from LineIndex or heuristic

    // Pre-allocated buffers
    private byte[] _readBuffer = new byte[128 * 1024]; // 128 KB chunk window
    private VisualLine[] _visualLines = new VisualLine[2048];
    private byte[] _countBuffer = new byte[64 * 1024]; // for newline counting

    // Cached line number at the current top offset
    private long _cachedTopOffset = -1;
    private long _cachedTopLineNumber = 1;

    // Maximum virtual height for scroll mapping (same strategy as HexView)
    private const float MaxVirtualHeight = 10_000_000f;

    // Goto line dialog state
    private bool _showGotoDialog;
    private readonly byte[] _gotoInputBuf = new byte[20];
    private bool _gotoFocusInput;

    // Selection state
    private long _cursorOffset = -1;
    private long _selectionAnchor = -1;
    private bool _isDragging;

    // Exact goto offset (overrides scroll heuristic for one frame)
    private long _gotoOffset = -1;
    private float _lastScrollY = float.NaN;
    private int _suppressScrollUnlockFrames;

    // Byte offset just past the last visible content (set during Render)
    private long _visibleEndOffset;

    // Caret blink state
    private double _caretBlinkTimer;
    private const double CaretBlinkPeriod = 1.06; // full cycle: 0.53s on + 0.53s off

    /// <summary>Current first visible byte offset.</summary>
    public long TopDocOffset => _topDocOffset;

    /// <summary>Word wrap on/off.</summary>
    public bool WordWrap {
        get => _wordWrap;
        set => _wordWrap = value;
    }

    /// <summary>Tab display width in columns.</summary>
    public int TabWidth {
        get => _tabWidth;
        set { _tabWidth = Math.Max(1, value); }
    }

    public TextView(Document document, ITextDecoder? decoder = null)
    {
        _document = document;
        _decoder = decoder ?? new Utf8TextDecoder();
        _wrapEngine = new LineWrapEngine(_tabWidth);
        // Rough heuristic: assume ~80 bytes per line for initial estimate
        _estimatedTotalLines = Math.Max(1, _document.Length / 80);
    }

    /// <summary>
    /// Provides the line index total count from the background scanner.
    /// Call this each frame if a LineIndex is available.
    /// </summary>
    public void UpdateLineEstimate(long totalHardLines)
    {
        if (totalHardLines > 0)
            _estimatedTotalLines = totalHardLines;
    }

    /// <summary>
    /// Scrolls to the given byte offset.
    /// </summary>
    public void ScrollToOffset(long offset)
    {
        _topDocOffset = Math.Clamp(offset, 0, Math.Max(0, _document.Length - 1));
        // Snap to line start
        _topDocOffset = LineWrapEngine.FindLineStart(
            _topDocOffset, _document.Length,
            (off, buf) => _document.Read(off, buf), _decoder);
        // Keep exact position stable until the user scrolls.
        _gotoOffset = _topDocOffset;
        _suppressScrollUnlockFrames = 2;
        _scrollTargetLine = -1; // will recalc from offset
    }

    /// <summary>
    /// Opens the Goto Line dialog.
    /// </summary>
    public void OpenGotoDialog()
    {
        _showGotoDialog = true;
        Array.Clear(_gotoInputBuf);
        _gotoFocusInput = true;
    }

    private bool HasSelection => _cursorOffset >= 0 && _selectionAnchor >= 0 && _cursorOffset != _selectionAnchor;
    private long SelectionStart => Math.Min(_cursorOffset, _selectionAnchor);
    private long SelectionEnd => Math.Max(_cursorOffset, _selectionAnchor);

    /// <summary>
    /// Jumps to the given offset and sets the selection to [start, end).
    /// Used by the Find dialog to highlight search matches.
    /// </summary>
    public void SetSelection(long start, long end)
    {
        _selectionAnchor = start;
        _cursorOffset = end;
        EnsureCursorVisible();
    }

    /// <summary>Current cursor byte offset (for Find dialog positioning).</summary>
    public long CursorOffset => _cursorOffset;

    /// <summary>
    /// Renders the text view. Must be called between ImGui.Begin/End.
    /// </summary>
    /// <param name="deltaTime">Frame delta time in seconds, used for caret blink.</param>
    public unsafe void Render(float deltaTime)
    {
        _caretBlinkTimer += deltaTime;
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        float charWidth = ImGui.CalcTextSize("M"u8).X;
        var outerAvail = ImGui.GetContentRegionAvail();

        // Gutter sizing: 8 digit positions + 1 space separator = 9 chars
        float gutterWidth = 9 * charWidth;

        // ── Estimated total visual lines for scrollbar ──
        long totalLines = _estimatedTotalLines;
        if (totalLines < 1) totalLines = 1;

        float idealHeight = totalLines * lineHeight;
        float virtualHeight = Math.Min(idealHeight, MaxVirtualHeight);

        // ── Begin scrollable child window ──
        ImGui.BeginChild("##TextScroll"u8, outerAvail, ImGuiChildFlags.None,
            ImGuiWindowFlags.NoMove);

        // Compute maxColumns *inside* the child window so the scrollbar width is excluded
        var innerAvail = ImGui.GetContentRegionAvail();
        float textAreaWidth = innerAvail.X - gutterWidth;
        int maxColumns = Math.Max(1, (int)(textAreaWidth / charWidth));
        _visibleRows = Math.Max(1, (int)(innerAvail.Y / lineHeight));
        long maxFirstLine = Math.Max(0, totalLines - _visibleRows);
        float maxScrollY = virtualHeight - _visibleRows * lineHeight;
        if (maxScrollY < 1f) maxScrollY = 1f;

        // ── Handle keyboard input ──
        HandleScrollInput(totalLines, maxFirstLine);

        // ── Apply programmatic scroll ──
        bool appliedProgrammaticScroll = false;
        if (_scrollTargetLine >= 0) {
            float targetScrollY = LineToScrollY(_scrollTargetLine, totalLines, maxFirstLine, idealHeight, maxScrollY, lineHeight);
            ImGui.SetScrollY(targetScrollY);
            _scrollTargetLine = -1;
            appliedProgrammaticScroll = true;
            _suppressScrollUnlockFrames = 2;
        }

        // ── Derive top offset from scroll position ──
        float scrollY = ImGui.GetScrollY();

        bool scrollMoved = !float.IsNaN(_lastScrollY) && MathF.Abs(scrollY - _lastScrollY) > 0.5f;
        if (scrollMoved && !appliedProgrammaticScroll && _suppressScrollUnlockFrames == 0)
            _gotoOffset = -1;

        if (_suppressScrollUnlockFrames > 0)
            _suppressScrollUnlockFrames--;

        long currentLine = ScrollYToLine(scrollY, totalLines, maxFirstLine, idealHeight, maxScrollY, lineHeight);
        currentLine = Math.Clamp(currentLine, 0, maxFirstLine);

        // Convert line number to byte offset
        if (_gotoOffset >= 0) {
            // Exact goto target — skip heuristic until user scrolls.
            _topDocOffset = _gotoOffset;
        } else if (_document.Length > 0) {
            long approxOffset = (long)((double)currentLine / totalLines * _document.Length);
            approxOffset = Math.Clamp(approxOffset, 0, Math.Max(0, _document.Length - 1));
            _topDocOffset = LineWrapEngine.FindLineStart(
                approxOffset, _document.Length,
                (off, buf) => _document.Read(off, buf), _decoder);
        } else {
            _topDocOffset = 0;
        }

        // ── Read a chunk around the viewport ──
        // Read enough bytes to cover the visible rows + some padding
        int chunkSize = Math.Max((_visibleRows + 4) * 256, 16 * 1024);
        chunkSize = Math.Min(chunkSize, _readBuffer.Length);

        long remaining = _document.Length - _topDocOffset;
        int bytesToRead = (int)Math.Min(chunkSize, remaining > 0 ? remaining : 0);

        if (bytesToRead <= 0 && _document.Length == 0) {
            ImGui.TextUnformatted("(empty file)"u8);
            ImGui.SetCursorPosY(virtualHeight);
            ImGui.Dummy(new Vector2(0, 0));
            ImGui.EndChild();
            RenderGotoDialog();
            return;
        }

        int bytesRead = bytesToRead > 0
            ? _document.Read(_topDocOffset, _readBuffer.AsSpan(0, bytesToRead))
            : 0;

        // ── Character boundary alignment ──
        // If we started mid-sequence, the first few bytes may be continuation bytes.
        // AlignToCharBoundary ensures we start at a valid character.
        int startAdj = 0;
        if (_topDocOffset > 0 && bytesRead > 0) {
            startAdj = _decoder.AlignToCharBoundary(_readBuffer.AsSpan(0, bytesRead), 0);
        }

        // ── Compute visual lines ──
        var dataSpan = _readBuffer.AsSpan(startAdj, bytesRead - startAdj);
        if (_visualLines.Length < _visibleRows + 4)
            _visualLines = new VisualLine[(_visibleRows + 4) * 2];

        int lineCount = _wrapEngine.ComputeVisualLines(
            dataSpan,
            _topDocOffset + startAdj,
            maxColumns,
            _wordWrap,
            _visualLines.AsSpan(),
            _decoder);

        // ── Render ──
        ImGui.SetCursorPosY(scrollY);
        var drawList = ImGui.GetWindowDrawList();
        Vector2 cursorStart = ImGui.GetCursorScreenPos();

        // ── Mouse selection handling ──
        int linesAvail = Math.Min(lineCount, _visibleRows);
        HandleMouseSelection(cursorStart, gutterWidth, charWidth, lineHeight, linesAvail, bytesRead);

        // Colors
        uint colText = ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.9f, 1.0f));
        uint colLineNum = ImGui.GetColorU32(new Vector4(0.5f, 0.6f, 0.7f, 0.8f));
        uint colControl = ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1.0f));
        uint colWrapIndicator = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.7f, 0.5f));
        uint colSelection = ImGui.GetColorU32(new Vector4(0.2f, 0.4f, 0.8f, 0.4f));

        float textStartX = cursorStart.X + gutterWidth;

        // Formatting buffers (stack-allocated)
        Span<byte> lineNumBuf = stackalloc byte[10]; // 8 digits + space + null
                                                     // Render buffer for a single line of UTF-8 text
        Span<byte> renderBuf = stackalloc byte[1024];

        // Get the actual line number at the top of the viewport
        long hardLineNumber = GetLineNumberAtOffset(_topDocOffset) - 1; // -1 because first hard line increments it

        int linesRendered = 0;
        for (int i = 0; i < lineCount && linesRendered < _visibleRows; i++) {
            ref readonly VisualLine vl = ref _visualLines[i];
            float y = cursorStart.Y + linesRendered * lineHeight;

            // ── Selection highlight ──
            if (HasSelection) {
                long selS = SelectionStart, selE = SelectionEnd;
                long lineEnd = vl.DocOffset + vl.ByteLength;
                if (selS < lineEnd && selE > vl.DocOffset) {
                    long hiS = Math.Max(selS, vl.DocOffset);
                    long hiE = Math.Min(selE, lineEnd);
                    float x1 = textStartX + ByteOffsetToPixelX(vl, hiS, charWidth);
                    float x2 = textStartX + ByteOffsetToPixelX(vl, hiE, charWidth);
                    drawList.AddRectFilled(new Vector2(x1, y), new Vector2(x2, y + lineHeight), colSelection);
                }
            }

            // ── Line number gutter ──
            // Only show line number for the first visual line of a hard line
            bool isHardLineStart = (i == 0) ||
                (vl.DocOffset > 0 && IsAfterNewline(vl.DocOffset));

            if (isHardLineStart) {
                hardLineNumber++;
                FormatLineNumber(hardLineNumber, lineNumBuf);
                fixed (byte* pNum = lineNumBuf) {
                    drawList.AddText(new Vector2(cursorStart.X, y), colLineNum, pNum);
                }
            } else {
                // Wrap continuation indicator
                drawList.AddText(new Vector2(cursorStart.X + gutterWidth - 2 * charWidth, y),
                    colWrapIndicator, WrapIndicatorPtr);
            }

            // ── Text content ──
            // Read the visual line's bytes from the buffer
            int vlStartInBuf = (int)(vl.DocOffset - _topDocOffset) - startAdj + startAdj; // net offset in _readBuffer
            int vlBufStart = (int)(vl.DocOffset - _topDocOffset);

            if (vlBufStart >= 0 && vlBufStart + vl.ByteLength <= bytesRead) {
                int renderLen = RenderLineToBuffer(
                    _readBuffer.AsSpan(vlBufStart, vl.ByteLength),
                    renderBuf,
                    _tabWidth,
                    _decoder);

                if (renderLen > 0) {
                    fixed (byte* pText = renderBuf) {
                        drawList.AddText(new Vector2(textStartX, y), colText, pText, pText + renderLen);
                    }
                }
            }

            linesRendered++;
        }

        // ── Blinking caret ──
        if (_cursorOffset >= 0 && _caretBlinkTimer % CaretBlinkPeriod < CaretBlinkPeriod * 0.5) {
            for (int ci = 0; ci < lineCount && ci < _visibleRows; ci++) {
                ref readonly VisualLine cvl = ref _visualLines[ci];
                long lineEnd = cvl.DocOffset + cvl.ByteLength;
                if (_cursorOffset >= cvl.DocOffset && _cursorOffset <= lineEnd) {
                    float caretX = textStartX + ByteOffsetToPixelX(cvl, _cursorOffset, charWidth);
                    float caretY = cursorStart.Y + ci * lineHeight;
                    uint colCaret = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.9f));
                    drawList.AddRectFilled(
                        new Vector2(caretX, caretY),
                        new Vector2(caretX + 2f, caretY + lineHeight),
                        colCaret);
                    break;
                }
            }
        }

        // ── Track visible byte range for cursor visibility checks ──
        if (linesRendered > 0) {
            ref readonly VisualLine lastVl = ref _visualLines[linesRendered - 1];
            _visibleEndOffset = lastVl.DocOffset + lastVl.ByteLength;
        } else {
            _visibleEndOffset = _topDocOffset;
        }

        // ── Set virtual content height for scrollbar ──
        ImGui.SetCursorPosY(virtualHeight);
        ImGui.Dummy(new Vector2(0, 0));
        _lastScrollY = ImGui.GetScrollY();
        ImGui.EndChild();

        // ── Goto dialog ──
        RenderGotoDialog();
    }

    /// <summary>
    /// Checks whether the byte just before <paramref name="docOffset"/> was a newline.
    /// Uses the read buffer if possible.
    /// </summary>
    private bool IsAfterNewline(long docOffset)
    {
        if (docOffset <= 0) return true;

        int checkLen = _decoder.MinCharBytes;
        if (docOffset < checkLen) return false;

        long bufStart = _topDocOffset;
        long posInBuf = docOffset - checkLen - bufStart;

        if (posInBuf >= 0 && posInBuf + checkLen <= _readBuffer.Length) {
            ReadOnlySpan<byte> check = _readBuffer.AsSpan((int)posInBuf, checkLen);
            if (_decoder.IsNewline(check, 0, out _)) {
                var (r, _) = _decoder.DecodeRune(check, 0);
                return r.Value == '\n';
            }
            return false;
        }

        // Fallback: read from document
        Span<byte> tmp = stackalloc byte[4];
        int read = _document.Read(docOffset - checkLen, tmp.Slice(0, checkLen));
        if (read < checkLen) return false;
        ReadOnlySpan<byte> fallback = tmp.Slice(0, checkLen);
        if (_decoder.IsNewline(fallback, 0, out _)) {
            var (r, _) = _decoder.DecodeRune(fallback, 0);
            return r.Value == '\n';
        }
        return false;
    }

    /// <summary>
    /// Renders a single visual line's bytes into a null-terminated UTF-8 display buffer,
    /// replacing control characters and expanding tabs. Decodes from the source encoding
    /// and re-encodes as UTF-8 for ImGui.
    /// Returns the number of bytes written (excluding the null terminator).
    /// </summary>
    private static int RenderLineToBuffer(ReadOnlySpan<byte> lineData, Span<byte> output, int tabWidth, ITextDecoder decoder)
    {
        int outPos = 0;
        int pos = 0;
        int maxOut = output.Length - 1; // leave room for null terminator
        Span<byte> utf8Tmp = stackalloc byte[4]; // for re-encoding runes to UTF-8

        while (pos < lineData.Length && outPos < maxOut) {
            // Check for newlines using the decoder
            if (decoder.IsNewline(lineData, pos, out int nlLen)) {
                pos += nlLen;
                continue;
            }

            // Decode the next rune from the source encoding
            var (rune, byteLen) = decoder.DecodeRune(lineData, pos);
            if (byteLen == 0) { pos++; continue; }

            int cp = rune.Value;

            // Tab expansion
            if (cp == '\t') {
                int spaces = Math.Min(tabWidth, maxOut - outPos);
                for (int s = 0; s < spaces; s++)
                    output[outPos++] = (byte)' ';
                pos += byteLen;
                continue;
            }

            // Control characters → dot
            if (cp < 0x20) {
                output[outPos++] = (byte)'.';
                pos += byteLen;
                continue;
            }

            // Encode the rune as UTF-8 for ImGui display
            if (rune.TryEncodeToUtf8(utf8Tmp, out int utf8Len)) {
                if (outPos + utf8Len > maxOut) break; // not enough space
                utf8Tmp.Slice(0, utf8Len).CopyTo(output.Slice(outPos));
                outPos += utf8Len;
            } else {
                // Can't encode → dot
                if (outPos < maxOut)
                    output[outPos++] = (byte)'.';
            }
            pos += byteLen;
        }

        // Null-terminate
        if (outPos < output.Length)
            output[outPos] = 0;

        return outPos;
    }

    private void HandleScrollInput(long totalLines, long maxFirstLine)
    {
        var io = ImGui.GetIO();
        if (io.WantTextInput) return;
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows)) return;

        // Initialize cursor if not set
        if (_cursorOffset < 0) {
            _cursorOffset = _topDocOffset;
            _selectionAnchor = _cursorOffset;
        }

        bool moved = false;

        // Numpad keys act as navigation only when NumLock is off.
        // GLFW always reports the physical key (e.g. Keypad7) regardless of NumLock.
        // When NumLock is on, a digit char is also queued — use that to detect the state.
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

        // Arrow keys — always move cursor
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad4, true))) {
            _cursorOffset = MoveCursorLeft(_cursorOffset); moved = true;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad6, true))) {
            _cursorOffset = MoveCursorRight(_cursorOffset); moved = true;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad8, true))) {
            _cursorOffset = MoveCursorUp(_cursorOffset); moved = true;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad2, true))) {
            _cursorOffset = MoveCursorDown(_cursorOffset); moved = true;
        }

        // Page Up / Page Down — move cursor by a page of lines
        if (ImGui.IsKeyPressed(ImGuiKey.PageDown, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad3, true))) {
            for (int i = 0; i < _visibleRows; i++)
                _cursorOffset = MoveCursorDown(_cursorOffset);
            moved = true;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.PageUp, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad9, true))) {
            for (int i = 0; i < _visibleRows; i++)
                _cursorOffset = MoveCursorUp(_cursorOffset);
            moved = true;
        }

        // Home / End — line start/end (Ctrl = file start/end)
        if (ImGui.IsKeyPressed(ImGuiKey.Home, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad7, true))) {
            if (io.KeyCtrl)
                _cursorOffset = 0;
            else
                _cursorOffset = LineWrapEngine.FindLineStart(
                    _cursorOffset, _document.Length, (o, b) => _document.Read(o, b), _decoder);
            moved = true;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.End, true) || (numpadAsNav && ImGui.IsKeyPressed(ImGuiKey.Keypad1, true))) {
            if (io.KeyCtrl)
                _cursorOffset = _document.Length;
            else
                _cursorOffset = FindLineEnd(_cursorOffset);
            moved = true;
        }

        // Update selection: Shift extends, plain arrow resets anchor
        if (moved) {
            if (!io.KeyShift)
                _selectionAnchor = _cursorOffset;
            EnsureCursorVisible();
            _caretBlinkTimer = 0;
        }

        // Ctrl+G = Goto line
        if (ImGui.IsKeyPressed(ImGuiKey.G) && io.KeyCtrl)
            OpenGotoDialog();

        // Ctrl+C = Copy selection
        if (ImGui.IsKeyPressed(ImGuiKey.C) && io.KeyCtrl && HasSelection)
            CopySelectionToClipboard();

        // ── Editing ──────────────────────────────────────────────────

        // Ctrl+X = Cut selection
        if (ImGui.IsKeyPressed(ImGuiKey.X) && io.KeyCtrl && HasSelection) {
            CopySelectionToClipboard();
            _document.Delete(SelectionStart, SelectionEnd - SelectionStart);
            _cursorOffset = SelectionStart;
            _selectionAnchor = _cursorOffset;
            InvalidateLineCache();
            moved = true;
        }

        // Ctrl+V = Paste clipboard
        if (ImGui.IsKeyPressed(ImGuiKey.V) && io.KeyCtrl) {
            unsafe {
                byte* clipText = ImGui.GetClipboardText();
                if (clipText != null) {
                    int len = 0;
                    while (clipText[len] != 0) len++;
                    if (len > 0) {
                        if (HasSelection) {
                            _document.Delete(SelectionStart, SelectionEnd - SelectionStart);
                            _cursorOffset = SelectionStart;
                            _selectionAnchor = _cursorOffset;
                        }

                        // Transcode from clipboard UTF-8 to the active encoding
                        ReadOnlySpan<byte> clipSpan = new ReadOnlySpan<byte>(clipText, len);
                        byte[] transcoded = TranscodeFromUtf8(clipSpan);
                        _document.Insert(_cursorOffset, transcoded);
                        _cursorOffset += transcoded.Length;
                        _selectionAnchor = _cursorOffset;
                        InvalidateLineCache();
                        moved = true;
                    }
                }
            }
        }

        // Enter = insert newline
        if ((ImGui.IsKeyPressed(ImGuiKey.Enter, true) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, true)) && !io.KeyCtrl) {
            if (HasSelection) {
                _document.Delete(SelectionStart, SelectionEnd - SelectionStart);
                _cursorOffset = SelectionStart;
                _selectionAnchor = _cursorOffset;
            }
            Span<byte> nlBytes = stackalloc byte[4];
            int nlLen = _decoder.EncodeRune(new Rune('\n'), nlBytes);
            _document.Insert(_cursorOffset, nlBytes.Slice(0, nlLen));
            _cursorOffset += nlLen;
            _selectionAnchor = _cursorOffset;
            InvalidateLineCache();
            moved = true;
        }

        // Backspace = delete rune before cursor
        if (ImGui.IsKeyPressed(ImGuiKey.Backspace, true)) {
            if (HasSelection) {
                _document.Delete(SelectionStart, SelectionEnd - SelectionStart);
                _cursorOffset = SelectionStart;
                _selectionAnchor = _cursorOffset;
            } else if (_cursorOffset > 0) {
                long newCursor = MoveCursorLeft(_cursorOffset);
                int deleteLen = (int)(_cursorOffset - newCursor);
                _document.Delete(newCursor, deleteLen);
                _cursorOffset = newCursor;
                _selectionAnchor = _cursorOffset;
            }
            InvalidateLineCache();
            moved = true;
        }

        // Delete = delete rune at cursor
        if (ImGui.IsKeyPressed(ImGuiKey.Delete, true)) {
            if (HasSelection) {
                _document.Delete(SelectionStart, SelectionEnd - SelectionStart);
                _cursorOffset = SelectionStart;
                _selectionAnchor = _cursorOffset;
            } else if (_cursorOffset < _document.Length) {
                long newNext = MoveCursorRight(_cursorOffset);
                int deleteLen = (int)(newNext - _cursorOffset);
                _document.Delete(_cursorOffset, deleteLen);
                _selectionAnchor = _cursorOffset;
            }
            InvalidateLineCache();
            moved = true;
        }

        // Printable character input (sourced from AppWindow.OnKeyChar → io.AddInputCharacter)
        if (!io.KeyCtrl && !io.KeyAlt) {
            unsafe {
                var charQueue = io.InputQueueCharacters;
                Span<byte> encoded = stackalloc byte[4];
                for (int ci = 0; ci < charQueue.Size; ci++) {
                    char ch = (char)charQueue.Data[ci];
                    if (ch < 0x20 && ch != '\t') continue; // skip control chars (Enter handled above)

                    if (HasSelection) {
                        _document.Delete(SelectionStart, SelectionEnd - SelectionStart);
                        _cursorOffset = SelectionStart;
                        _selectionAnchor = _cursorOffset;
                    }

                    if (Rune.TryCreate(ch, out Rune rune)) {
                        int written = _decoder.EncodeRune(rune, encoded);
                        if (written > 0) {
                            _document.Insert(_cursorOffset, encoded.Slice(0, written));
                            _cursorOffset += written;
                            _selectionAnchor = _cursorOffset;
                            InvalidateLineCache();
                            moved = true;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Invalidates cached line number state after a document edit.</summary>
    private void InvalidateLineCache()
    {
        _cachedTopOffset = -1;
        _estimatedTotalLines = Math.Max(1, _document.Length / 80);
    }

    /// <summary>
    /// Returns the byte offset of the end of the line containing <paramref name="offset"/>
    /// (just before the LF, or at document end).
    /// </summary>
    private long FindLineEnd(long offset)
    {
        Span<byte> buf = stackalloc byte[1024];
        long searchPos = offset;
        while (searchPos < _document.Length) {
            int read = _document.Read(searchPos, buf);
            if (read == 0) break;
            int nlIdx = FindNewlineInSpan(buf.Slice(0, read));
            if (nlIdx >= 0)
                return searchPos + nlIdx;
            searchPos += read;
        }
        return _document.Length;
    }

    /// <summary>
    /// Scrolls by the given number of visual lines (positive = down, negative = up).
    /// </summary>
    private void ScrollByLines(int delta)
    {
        if (delta == 0) return;

        if (delta > 0) {
            // Scroll down: advance past 'delta' newlines/wraps
            long offset = _topDocOffset;
            Span<byte> buf = stackalloc byte[512];
            for (int i = 0; i < delta && offset < _document.Length; i++) {
                int read = _document.Read(offset, buf);
                if (read == 0) break;
                // Find next newline
                int nlPos = FindNewlineInSpan(buf.Slice(0, read));
                if (nlPos >= 0) {
                    _decoder.IsNewline(buf.Slice(0, read), nlPos, out int nlByteLen);
                    offset += nlPos + nlByteLen;
                } else {
                    offset += read;
                }
            }
            _topDocOffset = Math.Min(offset, Math.Max(0, _document.Length - 1));
        } else {
            // Scroll up: go back 'delta' lines
            long offset = _topDocOffset;
            for (int i = 0; i < -delta && offset > 0; i++) {
                // Go to previous line start: step back past the preceding LF
                offset = Math.Max(0, offset - 1);
                offset = LineWrapEngine.FindLineStart(
                    offset, _document.Length,
                    (off, buf) => _document.Read(off, buf), _decoder);
            }
            _topDocOffset = offset;
        }
    }

    private unsafe void RenderGotoDialog()
    {
        if (_showGotoDialog) {
            ImGui.OpenPopup("Goto Line"u8);
            _showGotoDialog = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal("Goto Line"u8, ref open, ImGuiWindowFlags.AlwaysAutoResize)) {
            ImGui.Text("Enter line number:"u8);

            if (_gotoFocusInput) {
                ImGui.SetKeyboardFocusHere();
                _gotoFocusInput = false;
            }

            bool enter;
            fixed (byte* pBuf = _gotoInputBuf) {
                enter = ImGui.InputText("##gotoLine"u8, pBuf, (nuint)_gotoInputBuf.Length,
                    ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
            }

            bool doGoto = enter || ImGui.Button("Go"u8);
            ImGui.SameLine();
            bool cancel = ImGui.Button("Cancel"u8);

            if (doGoto) {
                string input = System.Text.Encoding.ASCII.GetString(_gotoInputBuf).TrimEnd('\0').Trim();
                if (long.TryParse(input, out long lineNum) && lineNum > 0) {
                    long targetOffset = FindOffsetOfLine(lineNum);
                    _topDocOffset = LineWrapEngine.FindLineStart(
                        targetOffset, _document.Length,
                        (off, buf) => _document.Read(off, buf), _decoder);
                    _gotoOffset = _topDocOffset;
                    _scrollTargetLine = lineNum - 1;
                    _suppressScrollUnlockFrames = 2;
                    _cachedTopOffset = _topDocOffset;
                    _cachedTopLineNumber = lineNum;
                }
                ImGui.CloseCurrentPopup();
            }
            if (cancel || ImGui.IsKeyPressed(ImGuiKey.Escape))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    // ── Line number tracking ──────────────────────────────────────

    /// <summary>
    /// Returns the 1-based line number at the given byte offset.
    /// Uses incremental scanning for small deltas and falls back to heuristic for huge jumps.
    /// </summary>
    private long GetLineNumberAtOffset(long offset)
    {
        if (offset == 0) {
            _cachedTopOffset = 0;
            _cachedTopLineNumber = 1;
            return 1;
        }
        if (offset == _cachedTopOffset) return _cachedTopLineNumber;

        long delta = offset - _cachedTopOffset;
        const long MaxScanDelta = 10 * 1024 * 1024; // 10 MB

        if (_cachedTopOffset >= 0 && delta > 0 && delta <= MaxScanDelta) {
            _cachedTopLineNumber += CountNewlinesInRange(_cachedTopOffset, offset);
            _cachedTopOffset = offset;
        } else if (_cachedTopOffset >= 0 && delta < 0 && -delta <= MaxScanDelta) {
            _cachedTopLineNumber -= CountNewlinesInRange(offset, _cachedTopOffset);
            _cachedTopOffset = offset;
        } else if (offset <= MaxScanDelta) {
            _cachedTopLineNumber = 1 + CountNewlinesInRange(0, offset);
            _cachedTopOffset = offset;
        } else {
            // Too far — use heuristic
            _cachedTopLineNumber = _estimatedTotalLines > 0
                ? Math.Max(1, (long)((double)offset / _document.Length * _estimatedTotalLines))
                : 1;
            _cachedTopOffset = offset;
        }
        return _cachedTopLineNumber;
    }

    /// <summary>Counts LF characters in [start, end).</summary>
    private long CountNewlinesInRange(long start, long end)
    {
        long count = 0;
        long pos = start;
        int step = _decoder.MinCharBytes;
        while (pos < end) {
            int chunkLen = (int)Math.Min(end - pos, _countBuffer.Length);
            int read = _document.Read(pos, _countBuffer.AsSpan(0, chunkLen));
            if (read == 0) break;
            var data = _countBuffer.AsSpan(0, read);
            for (int i = 0; i + step <= data.Length; i += step) {
                if (_decoder.IsNewline(data, i, out _)) {
                    var (r, _) = _decoder.DecodeRune(data, i);
                    if (r.Value == '\n') count++;
                }
            }
            pos += read;
        }
        return count;
    }

    /// <summary>Finds the byte offset where the given 1-based line number starts.</summary>
    private long FindOffsetOfLine(long lineNumber)
    {
        if (lineNumber <= 1) return 0;
        long targetNewlines = lineNumber - 1;
        long nlCount = 0;
        long pos = 0;
        int step = _decoder.MinCharBytes;
        while (pos < _document.Length) {
            int chunkLen = (int)Math.Min(_document.Length - pos, _countBuffer.Length);
            int read = _document.Read(pos, _countBuffer.AsSpan(0, chunkLen));
            if (read == 0) break;
            var data = _countBuffer.AsSpan(0, read);
            for (int i = 0; i + step <= data.Length; i += step) {
                if (_decoder.IsNewline(data, i, out int nlLen)) {
                    var (r, _) = _decoder.DecodeRune(data, i);
                    if (r.Value == '\n') {
                        nlCount++;
                        if (nlCount == targetNewlines) return pos + i + nlLen;
                    }
                }
            }
            pos += read;
        }
        return Math.Min(pos, Math.Max(0, _document.Length - 1));
    }

    // ── Scroll mapping (same float-precision strategy as HexView) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ScrollYToLine(float scrollY, long totalLines, long maxFirstLine,
        float idealHeight, float maxScrollY, float lineHeight)
    {
        if (idealHeight <= MaxVirtualHeight)
            return (long)(scrollY / lineHeight);

        double fraction = scrollY / maxScrollY;
        return (long)(fraction * maxFirstLine);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LineToScrollY(long line, long totalLines, long maxFirstLine,
        float idealHeight, float maxScrollY, float lineHeight)
    {
        if (idealHeight <= MaxVirtualHeight)
            return line * lineHeight;

        double fraction = maxFirstLine > 0 ? (double)line / maxFirstLine : 0;
        return (float)(fraction * maxScrollY);
    }

    /// <summary>Formats a line number as right-justified ASCII, null-terminated.</summary>
    /// <remarks>Buffer layout: 8 digit positions + 1 space + null = 10 bytes.</remarks>
    private static void FormatLineNumber(long lineNum, Span<byte> buf)
    {
        // buf = [d d d d d d d d ' ' \0]  (10 bytes)
        buf.Fill((byte)' ');
        buf[buf.Length - 1] = 0;     // null terminator
                                     // buf[buf.Length - 2] remains ' ' (separator between numbers and text)

        int pos = buf.Length - 3;    // rightmost digit position

        if (lineNum == 0) {
            buf[pos] = (byte)'0';
            return;
        }

        long n = lineNum;
        while (n > 0 && pos >= 0) {
            buf[pos--] = (byte)('0' + (int)(n % 10));
            n /= 10;
        }
    }

    // Pointer to a static wrap indicator string "↪\0" (UTF-8: E2 86 AA 00)
    private static ReadOnlySpan<byte> WrapIndicatorBytes => [0xE2, 0x86, 0xAA, 0x00];

    private static unsafe byte* WrapIndicatorPtr {
        get {
            fixed (byte* p = WrapIndicatorBytes)
                return p;
        }
    }

    // ── Selection & clipboard ─────────────────────────────────────

    private void HandleMouseSelection(Vector2 cursorStart, float gutterWidth, float charWidth,
        float lineHeight, int lineCount, int bytesRead)
    {
        if (lineCount <= 0) return;

        // Click to place cursor / start drag
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered()) {
            long hit = HitTestMouse(cursorStart, gutterWidth, charWidth, lineHeight, lineCount, bytesRead);
            if (hit >= 0) {
                var io = ImGui.GetIO();
                if (io.KeyShift && _cursorOffset >= 0) {
                    // Shift+click: extend selection
                    _cursorOffset = hit;
                } else {
                    _cursorOffset = hit;
                    _selectionAnchor = hit;
                }
                _isDragging = true;
                _caretBlinkTimer = 0;
            }
        }

        // Drag to extend selection
        if (_isDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
            long hit = HitTestMouse(cursorStart, gutterWidth, charWidth, lineHeight, lineCount, bytesRead);
            if (hit >= 0)
                _cursorOffset = hit;
        }

        if (_isDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            _isDragging = false;
    }

    private long HitTestMouse(Vector2 cursorStart, float gutterWidth, float charWidth,
        float lineHeight, int lineCount, int bytesRead)
    {
        var mousePos = ImGui.GetMousePos();
        int row = (int)((mousePos.Y - cursorStart.Y) / lineHeight);
        if (row < 0) row = 0;
        if (row >= lineCount) row = lineCount - 1;
        if (row < 0) return -1;

        float targetCol = (mousePos.X - cursorStart.X - gutterWidth) / charWidth;
        if (targetCol < 0) targetCol = 0;

        ref readonly VisualLine vl = ref _visualLines[row];
        int vlBufStart = (int)(vl.DocOffset - _topDocOffset);
        if (vlBufStart < 0 || vlBufStart + vl.ByteLength > bytesRead)
            return vl.DocOffset;

        var lineData = _readBuffer.AsSpan(vlBufStart, vl.ByteLength);
        int pos = 0;
        float col = 0;

        while (pos < lineData.Length) {
            // Check for newline
            if (_decoder.IsNewline(lineData, pos, out _)) break;

            var (rune, byteLen2) = _decoder.DecodeRune(lineData, pos);
            int byteLen = byteLen2 == 0 ? 1 : byteLen2;
            int cp = rune.Value;
            int w;
            if (cp == '\t') { w = _tabWidth; } else if (cp < 0x20) { w = 1; } else {
                w = Utf8Utils.RuneColumnWidth(rune, _tabWidth);
            }

            if (col + w * 0.5f >= targetCol)
                return vl.DocOffset + pos;

            col += w;
            pos += byteLen;
        }

        return vl.DocOffset + pos;
    }

    private float ByteOffsetToPixelX(in VisualLine vl, long targetOffset, float charWidth)
    {
        int bytesIntoLine = (int)(targetOffset - vl.DocOffset);
        int vlBufStart = (int)(vl.DocOffset - _topDocOffset);
        if (vlBufStart < 0) return 0;

        int walkLen = Math.Min(bytesIntoLine, vl.ByteLength);
        if (vlBufStart + walkLen > _readBuffer.Length) return 0;

        var lineData = _readBuffer.AsSpan(vlBufStart, walkLen);
        int col = 0;
        int pos = 0;

        while (pos < lineData.Length) {
            if (_decoder.IsNewline(lineData, pos, out int nlLen)) { pos += nlLen; continue; }

            var (rune, byteLen) = _decoder.DecodeRune(lineData, pos);
            if (byteLen == 0) { pos++; col++; continue; }

            int cp = rune.Value;
            if (cp == '\t') { col += _tabWidth; } else if (cp < 0x20) { col++; } else { col += Utf8Utils.RuneColumnWidth(rune, _tabWidth); }
            pos += byteLen;
        }

        return col * charWidth;
    }

    // ── Cursor movement helpers ───────────────────────────────────

    private long MoveCursorLeft(long offset)
    {
        if (offset <= 0) return 0;
        int backLen = (int)Math.Min(4, offset);
        long readStart = offset - backLen;
        Span<byte> buf = stackalloc byte[4];
        int read = _document.Read(readStart, buf.Slice(0, backLen));
        if (read == 0) return Math.Max(0, offset - 1);

        int pos = 0;
        int lastStart = 0;
        while (pos < read) {
            lastStart = pos;
            var (_, byteLen) = _decoder.DecodeRune(buf.Slice(0, read), pos);
            if (byteLen == 0) { pos++; continue; }
            pos += byteLen;
        }
        return readStart + lastStart;
    }

    private long MoveCursorRight(long offset)
    {
        if (offset >= _document.Length) return _document.Length;
        Span<byte> buf = stackalloc byte[4];
        int read = _document.Read(offset, buf);
        if (read == 0) return offset;
        var (_, byteLen) = _decoder.DecodeRune(buf.Slice(0, read), 0);
        return Math.Min(offset + Math.Max(1, byteLen), _document.Length);
    }

    private long MoveCursorUp(long offset)
    {
        long lineStart = LineWrapEngine.FindLineStart(
            offset, _document.Length, (o, b) => _document.Read(o, b), _decoder);
        int col = (int)(offset - lineStart);

        if (lineStart == 0) return 0;

        // Go to previous line
        long prevLineStart = LineWrapEngine.FindLineStart(
            Math.Max(0, lineStart - 1), _document.Length, (o, b) => _document.Read(o, b), _decoder);
        int prevLineLen = (int)(lineStart - prevLineStart);
        if (prevLineLen > 0) prevLineLen--; // exclude the terminating LF

        return prevLineStart + Math.Min(col, Math.Max(0, prevLineLen));
    }

    private long MoveCursorDown(long offset)
    {
        Span<byte> buf = stackalloc byte[1024];
        long searchPos = offset;
        while (searchPos < _document.Length) {
            int read = _document.Read(searchPos, buf);
            if (read == 0) return _document.Length;
            int nlIdx = FindNewlineInSpan(buf.Slice(0, read));
            if (nlIdx >= 0) {
                _decoder.IsNewline(buf.Slice(0, read), nlIdx, out int nlByteLen);
                long nextLineStart = searchPos + nlIdx + nlByteLen;
                if (nextLineStart >= _document.Length) return _document.Length;

                long lineStart = LineWrapEngine.FindLineStart(
                    offset, _document.Length, (o, b) => _document.Read(o, b), _decoder);
                int col = (int)(offset - lineStart);

                int nextRead = _document.Read(nextLineStart, buf);
                if (nextRead > 0) {
                    int nextNl = FindNewlineInSpan(buf.Slice(0, nextRead));
                    int nextLineLen = nextNl >= 0 ? nextNl : nextRead;
                    return nextLineStart + Math.Min(col, nextLineLen);
                }
                return nextLineStart;
            }
            searchPos += read;
        }
        return _document.Length;
    }

    private void EnsureCursorVisible()
    {
        if (_cursorOffset < 0) return;

        // Check if cursor is outside the currently visible byte range
        if (_cursorOffset < _topDocOffset || _cursorOffset >= _visibleEndOffset) {
            _topDocOffset = LineWrapEngine.FindLineStart(
                _cursorOffset, _document.Length, (o, b) => _document.Read(o, b), _decoder);
            long approxLine = GetLineNumberAtOffset(_topDocOffset) - 1;
            _scrollTargetLine = Math.Max(0, approxLine);
        }

        // Always pin the offset so the heuristic doesn't fight with us
        _gotoOffset = _topDocOffset;
        _suppressScrollUnlockFrames = 2;
    }

    private unsafe void CopySelectionToClipboard()
    {
        if (!HasSelection) return;
        long selLen = Math.Min(SelectionEnd - SelectionStart, 10 * 1024 * 1024);
        byte[] rawBuffer = new byte[(int)selLen];
        int read = _document.Read(SelectionStart, rawBuffer.AsSpan(0, (int)selLen));

        byte[] clipBytes;
        if (_decoder.Encoding == TextEncoding.Utf8) {
            clipBytes = new byte[read + 1];
            rawBuffer.AsSpan(0, read).CopyTo(clipBytes);
            clipBytes[read] = 0;
        } else {
            // Transcode to UTF-8 for clipboard
            var result = new List<byte>(read * 2);
            Span<byte> tmp = stackalloc byte[4];
            int pos = 0;
            var data = rawBuffer.AsSpan(0, read);
            while (pos < data.Length) {
                var (rune, byteLen) = _decoder.DecodeRune(data, pos);
                if (byteLen == 0) { pos++; continue; }
                if (rune.TryEncodeToUtf8(tmp, out int utf8Len)) {
                    for (int i = 0; i < utf8Len; i++)
                        result.Add(tmp[i]);
                }
                pos += byteLen;
            }
            clipBytes = new byte[result.Count + 1];
            for (int i = 0; i < result.Count; i++)
                clipBytes[i] = result[i];
            clipBytes[result.Count] = 0;
        }

        fixed (byte* p = clipBytes) {
            ImGui.SetClipboardText(p);
        }
    }

    /// <summary>Finds the byte offset of the next LF newline in the span, or -1.</summary>
    private int FindNewlineInSpan(ReadOnlySpan<byte> data)
    {
        int step = _decoder.MinCharBytes;
        for (int i = 0; i + step <= data.Length; i += step) {
            if (_decoder.IsNewline(data, i, out _)) {
                var (r, _) = _decoder.DecodeRune(data, i);
                if (r.Value == '\n') return i;
            }
        }
        return -1;
    }

    /// <summary>Transcodes UTF-8 clipboard data to the active encoding.</summary>
    private byte[] TranscodeFromUtf8(ReadOnlySpan<byte> utf8Data)
    {
        if (_decoder.Encoding == TextEncoding.Utf8)
            return utf8Data.ToArray();

        // Decode UTF-8 runes, re-encode in target
        var result = new List<byte>();
        Span<byte> tmp = stackalloc byte[4];
        int pos = 0;
        while (pos < utf8Data.Length) {
            OperationStatus status = Rune.DecodeFromUtf8(utf8Data.Slice(pos), out Rune rune, out int consumed);
            if (status != OperationStatus.Done) {
                consumed = consumed > 0 ? consumed : 1;
                rune = Rune.ReplacementChar;
            }
            int written = _decoder.EncodeRune(rune, tmp);
            for (int i = 0; i < written; i++)
                result.Add(tmp[i]);
            pos += consumed;
        }
        return result.ToArray();
    }
}
