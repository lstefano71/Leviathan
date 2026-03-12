using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.Core.Text;

using System.Runtime.CompilerServices;
using System.Text;

using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Leviathan.TUI2.Views;

/// <summary>
/// Custom text editor view using Leviathan's Document.Read(offset, Span&lt;byte&gt;) directly.
/// Supports encoding-aware rendering, word wrap, selection, and editing for 50 GB+ files.
/// </summary>
internal sealed class LeviathanTextView : View
{
  private readonly AppState _state;
  private byte[] _readBuffer = new byte[128 * 1024];
  private readonly LineWrapEngine _wrapEngine = new();
  private VisualLine[] _visualLines = new VisualLine[2048];
  private readonly List<int> _charByteOffsets = new(4096);
  private long _cachedTopOffset;
  private long _cachedTopLineNumber = 1;
  private int _lastRenderedLineCount;
  private readonly ScrollBar _verticalScrollBar;

  private const int GutterWidth = 9; // "  12345 │"

  /// <summary>
  /// Fired when the view needs the status bar to update.
  /// </summary>
  internal event Action? StateChanged;

  internal LeviathanTextView(AppState state)
  {
    _state = state;
    CanFocus = true;
    ContentSizeTracksViewport = false;

    // Vertical scrollbar
    _verticalScrollBar = new ScrollBar() {
      Orientation = Orientation.Vertical,
      X = Pos.AnchorEnd(1),
      Y = 0,
      Width = 1,
      Height = Dim.Fill(),
      VisibilityMode = ScrollBarVisibilityMode.Always,
    };
    _verticalScrollBar.ValueChanged += (_, e) => {
      if (_state.Document is null) return;
      long totalLines = Math.Max(1, _state.EstimatedTotalLines);
      long newOffset = (long)((double)e.NewValue / Math.Max(1, totalLines) * _state.Document.Length);
      newOffset = Math.Clamp(newOffset, 0, _state.Document.Length);
      _state.TextTopOffset = FindLineStart(newOffset);
      SetNeedsDraw();
    };
    Add(_verticalScrollBar);

    SetupCommands();
    SetupKeyBindings();
    SetupMouseBindings();
  }

  // ─── Commands ───

  private void SetupCommands()
  {
    AddCommand(Command.Left, () => { MoveCursorLeft(false); return true; });
    AddCommand(Command.Right, () => { MoveCursorRight(false); return true; });
    AddCommand(Command.Up, () => { MoveCursorUp(false); return true; });
    AddCommand(Command.Down, () => { MoveCursorDown(false); return true; });
    AddCommand(Command.PageUp, () => { PageUp(false); return true; });
    AddCommand(Command.PageDown, () => { PageDown(false); return true; });
    AddCommand(Command.Start, () => { CtrlHome(false); return true; });
    AddCommand(Command.End, () => { CtrlEnd(false); return true; });
    AddCommand(Command.LeftStart, () => { Home(false); return true; });
    AddCommand(Command.RightEnd, () => { End(false); return true; });
    AddCommand(Command.ScrollUp, () => { ScrollUp(3); return true; });
    AddCommand(Command.ScrollDown, () => { ScrollDown(3); return true; });
    AddCommand(Command.DeleteCharLeft, () => { Backspace(); return true; });
    AddCommand(Command.DeleteCharRight, () => { Delete(); return true; });
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

    KeyBindings.Remove(Key.Space);
    KeyBindings.Remove(Key.Enter);
  }

  private void SetupMouseBindings()
  {
    MouseBindings.ReplaceCommands(MouseFlags.LeftButtonClicked, Command.Activate);
    MouseBindings.Add(MouseFlags.WheeledUp, Command.ScrollUp);
    MouseBindings.Add(MouseFlags.WheeledDown, Command.ScrollDown);
  }

  // ─── Key Input (character typing) ───

  /// <inheritdoc/>
  protected override bool OnKeyDownNotHandled(Key keyEvent)
  {
    if (_state.Document is null) return false;

    // Shift+arrow for selection
    bool extend = keyEvent.IsShift;
    if (extend) {
      if (keyEvent == Key.CursorLeft.WithShift) { MoveCursorLeft(true); return true; }
      if (keyEvent == Key.CursorRight.WithShift) { MoveCursorRight(true); return true; }
      if (keyEvent == Key.CursorUp.WithShift) { MoveCursorUp(true); return true; }
      if (keyEvent == Key.CursorDown.WithShift) { MoveCursorDown(true); return true; }
    }

    // Enter = newline
    if (keyEvent == Key.Enter) {
      InsertNewline();
      return true;
    }

    // Printable character input
    if (!keyEvent.IsCtrl && !keyEvent.IsAlt) {
      char c = (char)(keyEvent.KeyCode & ~KeyCode.ShiftMask);
      if (c >= 0x20 && c < 0x7F) {
        InsertChar(c);
        return true;
      }
      // Space
      if (keyEvent == Key.Space) {
        InsertChar(' ');
        return true;
      }
    }

    return false;
  }

  // ─── Mouse ───

  /// <inheritdoc/>
  protected override bool OnActivating(CommandEventArgs args)
  {
    if (args.Context?.Binding is not MouseBinding { MouseEvent: { } mouse })
      return base.OnActivating(args);

    if (!HasFocus)
      SetFocus();

    var pos = mouse.Position!.Value;
    ClickAtPosition(pos.Y, pos.X, extend: mouse.Flags.HasFlag(MouseFlags.Shift));
    return true;
  }

  // ─── Drawing ───

  /// <inheritdoc/>
  protected override bool OnDrawingContent(DrawContext? context)
  {
    Document? doc = _state.Document;
    if (doc is null) {
      SetAttributeForRole(VisualRole.Normal);
      Move(0, 0);
      AddStr("No file open");
      return true;
    }

    int vpHeight = Viewport.Height;
    int vpWidth = Viewport.Width;
    _state.VisibleRows = vpHeight;

    int textAreaCols = Math.Max(1, vpWidth - GutterWidth);

    EnsureCursorVisible(textAreaCols);

    // Compute visual lines
    ITextDecoder decoder = _state.Decoder;
    int maxCols = _state.WordWrap ? textAreaCols : int.MaxValue;

    int readSize = Math.Max((vpHeight + 4) * 256, 16384);
    int bytesRead;
    int lineCount;
    const int MaxReadSize = 16 * 1024 * 1024;

    while (true) {
      readSize = (int)Math.Min(readSize, doc.Length - _state.TextTopOffset);
      if (readSize <= 0) {
        DrawEmptyLine(0, vpWidth);
        return true;
      }

      EnsureBuffer(readSize);
      Span<byte> buf = _readBuffer.AsSpan(0, readSize);
      bytesRead = doc.Read(_state.TextTopOffset, buf);
      if (bytesRead == 0) {
        DrawEmptyLine(0, vpWidth);
        return true;
      }

      EnsureVisualLines(vpHeight + 8);
      lineCount = _wrapEngine.ComputeVisualLines(
          buf[..bytesRead], _state.TextTopOffset, maxCols, _state.WordWrap, _visualLines, decoder);

      if (lineCount >= vpHeight ||
          _state.TextTopOffset + bytesRead >= doc.Length ||
          readSize >= MaxReadSize)
        break;

      readSize = Math.Min(readSize * 2, MaxReadSize);
    }

    Span<byte> data = _readBuffer.AsSpan(0, bytesRead);

    // Search matches
    long viewStart = _state.TextTopOffset;
    long viewEnd = _state.TextTopOffset + bytesRead;
    List<SearchResult> visibleMatches = CollectVisibleMatches(viewStart, viewEnd);
    int currentMatchIdx = _state.CurrentMatchIndex;
    SearchResult? activeMatch = currentMatchIdx >= 0 && currentMatchIdx < _state.SearchResults.Count
        ? _state.SearchResults[currentMatchIdx]
        : null;

    long currentLineNumber = ComputeLineNumber(_state.TextTopOffset);
    _lastRenderedLineCount = lineCount;

    // Color attributes
    Attribute normalAttr = new(new Color(StandardColor.White), new Color(StandardColor.Black));
    Attribute gutterAttr = new(new Color(100, 130, 160), new Color(StandardColor.Black));
    Attribute cursorAttr = new(new Color(StandardColor.Black), new Color(208, 135, 46));
    Attribute selectionAttr = new(new Color(StandardColor.White), new Color(30, 80, 160));
    Attribute matchAttr = new(new Color(StandardColor.Black), new Color(StandardColor.Yellow));
    Attribute activeMatchAttr = new(new Color(StandardColor.Black), new Color(255, 165, 0));
    Attribute wrapIndicatorAttr = new(new Color(StandardColor.DarkGray), new Color(StandardColor.Black));

    int rowsToDraw = Math.Min(vpHeight, lineCount);
    for (int i = 0; i < rowsToDraw; i++) {
      VisualLine vl = _visualLines[i];
      long lineDocOffset = vl.DocOffset;
      long relativeStart = lineDocOffset - _state.TextTopOffset;

      Move(0, i);

      if (relativeStart < 0 || relativeStart > bytesRead) {
        DrawEmptyLine(i, vpWidth);
        continue;
      }

      int lineStart = (int)relativeStart;
      int lineByteLen = vl.ByteLength;
      if (lineByteLen < 0 || lineByteLen > bytesRead - lineStart)
        lineByteLen = Math.Max(0, bytesRead - lineStart);

      bool isHardLine = lineStart == 0 || IsNewlineAt(data, lineStart - 1, decoder);
      if (isHardLine)
        currentLineNumber++;

      // Gutter
      SetAttribute(gutterAttr);
      if (isHardLine) {
        string lineNumStr = (currentLineNumber - 1).ToString();
        int padding = 7 - lineNumStr.Length;
        for (int p = 0; p < padding; p++)
          AddRune(' ');
        foreach (char c in lineNumStr)
          AddRune(c);
        AddRune(' ');
        AddRune('│');
      } else {
        SetAttribute(wrapIndicatorAttr);
        for (int p = 0; p < 6; p++)
          AddRune(' ');
        AddRune('↪');
        AddRune(' ');
        AddRune('│');
      }

      // Text content
      ReadOnlySpan<byte> lineBytes = data.Slice(lineStart, lineByteLen);
      _charByteOffsets.Clear();
      string text = DecodeLineToDisplay(lineBytes, decoder, _state.TabWidth, _charByteOffsets);

      for (int ci = 0; ci < text.Length && GutterWidth + ci < vpWidth; ci++) {
        long charDocOffset = lineDocOffset;
        if (ci < _charByteOffsets.Count)
          charDocOffset = lineDocOffset + _charByteOffsets[ci];

        Attribute attr = GetCharAttribute(
            charDocOffset, _state.TextCursorOffset,
            _state.TextSelStart, _state.TextSelEnd,
            visibleMatches, activeMatch,
            normalAttr, cursorAttr, selectionAttr, matchAttr, activeMatchAttr);

        SetAttribute(attr);
        AddRune(text[ci]);
      }

      // Cursor at end of line (if cursor is at lineDocOffset + lineByteLen)
      long endOffset = lineDocOffset + lineByteLen;
      bool nextLineStartsHere = (i + 1 < rowsToDraw) && _visualLines[i + 1].DocOffset == endOffset;
      if (_state.TextCursorOffset == endOffset && !nextLineStartsHere && GutterWidth + text.Length < vpWidth) {
        SetAttribute(cursorAttr);
        AddRune(' ');
      }

      // Clear rest of line
      SetAttribute(normalAttr);
      int drawn = GutterWidth + text.Length + (_state.TextCursorOffset == endOffset ? 1 : 0);
      for (int c = drawn; c < vpWidth; c++)
        AddRune(' ');
    }

    // Empty rows below content
    for (int i = rowsToDraw; i < vpHeight; i++)
      DrawEmptyLine(i, vpWidth);

    // Update vertical scrollbar
    UpdateScrollBar(vpHeight);

    return true;
  }

  private void DrawEmptyLine(int row, int vpWidth)
  {
    Attribute normalAttr = new(new Color(StandardColor.DarkGray), new Color(StandardColor.Black));
    Move(0, row);
    SetAttribute(normalAttr);
    AddRune('~');
    for (int c = 1; c < vpWidth; c++)
      AddRune(' ');
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static Attribute GetCharAttribute(
      long charDocOffset, long cursorOffset,
      long selStart, long selEnd,
      List<SearchResult> visibleMatches, SearchResult? activeMatch,
      Attribute normalAttr, Attribute cursorAttr, Attribute selectionAttr,
      Attribute matchAttr, Attribute activeMatchAttr)
  {
    if (charDocOffset == cursorOffset)
      return cursorAttr;

    if (selStart >= 0 && charDocOffset >= selStart && charDocOffset <= selEnd)
      return selectionAttr;

    if (activeMatch is { } match &&
        charDocOffset >= match.Offset && charDocOffset < match.Offset + match.Length)
      return activeMatchAttr;

    for (int i = 0; i < visibleMatches.Count; i++) {
      SearchResult m = visibleMatches[i];
      if (charDocOffset >= m.Offset && charDocOffset < m.Offset + m.Length)
        return matchAttr;
    }

    return normalAttr;
  }

  // ─── Navigation ───

  internal void MoveCursorLeft(bool extend)
  {
    Document? doc = _state.Document;
    if (doc is null || _state.TextCursorOffset <= 0) return;

    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;

    int lookBack = (int)Math.Min(4, _state.TextCursorOffset);
    Span<byte> prev = stackalloc byte[lookBack];
    doc.Read(_state.TextCursorOffset - lookBack, prev);

    ITextDecoder decoder = _state.Decoder;
    int pos = 0;
    int lastRuneStart = 0;
    while (pos < lookBack) {
      lastRuneStart = pos;
      (_, int len) = decoder.DecodeRune(prev, pos);
      if (len <= 0) { pos++; continue; }
      pos += len;
    }
    int stepBack = lookBack - lastRuneStart;
    _state.TextCursorOffset -= stepBack;

    // Skip over newline characters so the cursor lands on visible content.
    // Handles \n and \r\n: after stepping back onto \n, skip back over it
    // (and over a preceding \r if present).
    SkipNewlinesBackward(doc);

    OnStateChanged();
  }

  internal void MoveCursorRight(bool extend)
  {
    Document? doc = _state.Document;
    if (doc is null || _state.TextCursorOffset >= doc.Length) return;

    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;

    Span<byte> next = stackalloc byte[4];
    int read = doc.Read(_state.TextCursorOffset, next);
    if (read == 0) return;

    ITextDecoder decoder = _state.Decoder;
    (Rune rune, int len) = decoder.DecodeRune(next[..read], 0);
    if (len <= 0) len = 1;
    _state.TextCursorOffset = Math.Min(_state.TextCursorOffset + len, doc.Length);

    // Skip over newline characters so the cursor lands on the start of the next line.
    // Handles \r\n: if we just advanced past \r, also skip the following \n.
    SkipNewlinesForward(doc);

    OnStateChanged();
  }

  /// <summary>Skips past \n and \r\n sequences when moving forward.</summary>
  private void SkipNewlinesForward(Document doc)
  {
    while (_state.TextCursorOffset < doc.Length) {
      Span<byte> peek = stackalloc byte[4];
      int peekRead = doc.Read(_state.TextCursorOffset, peek);
      if (peekRead == 0) break;
      (Rune r, int rLen) = _state.Decoder.DecodeRune(peek[..peekRead], 0);
      if (rLen <= 0) break;
      if (r.Value == '\n' || r.Value == '\r') {
        _state.TextCursorOffset = Math.Min(_state.TextCursorOffset + rLen, doc.Length);
      } else {
        break;
      }
    }
  }

  /// <summary>Skips back over \n and \r when moving backward.</summary>
  private void SkipNewlinesBackward(Document doc)
  {
    while (_state.TextCursorOffset > 0) {
      int lookBack = (int)Math.Min(4, _state.TextCursorOffset);
      Span<byte> prev = stackalloc byte[lookBack];
      doc.Read(_state.TextCursorOffset - lookBack, prev);

      ITextDecoder decoder = _state.Decoder;
      int pos = 0;
      int lastRuneStart = 0;
      while (pos < lookBack) {
        lastRuneStart = pos;
        (_, int l) = decoder.DecodeRune(prev, pos);
        if (l <= 0) { pos++; continue; }
        pos += l;
      }

      int prevRuneOffset = lookBack - lastRuneStart;
      Span<byte> check = stackalloc byte[4];
      int checkRead = doc.Read(_state.TextCursorOffset - prevRuneOffset, check);
      if (checkRead == 0) break;
      (Rune r, int rLen) = decoder.DecodeRune(check[..checkRead], 0);
      if (rLen <= 0) break;
      if (r.Value == '\n' || r.Value == '\r') {
        _state.TextCursorOffset -= prevRuneOffset;
      } else {
        break;
      }
    }
  }

  internal void MoveCursorUp(bool extend) => MoveVertical(-1, extend);
  internal void MoveCursorDown(bool extend) => MoveVertical(1, extend);

  internal void PageUp(bool extend)
  {
    for (int i = 0; i < _state.VisibleRows; i++)
      MoveVertical(-1, extend);
  }

  internal void PageDown(bool extend)
  {
    for (int i = 0; i < _state.VisibleRows; i++)
      MoveVertical(1, extend);
  }

  internal void ScrollUp(int lines)
  {
    Document? doc = _state.Document;
    if (doc is null || _state.TextTopOffset <= 0) return;

    long offset = _state.TextTopOffset;
    for (int i = 0; i < lines && offset > 0; i++) {
      long prevLineEnd = Math.Max(0, offset - 1);
      offset = FindLineStart(prevLineEnd);
    }
    _state.TextTopOffset = offset;
    SetNeedsDraw();
  }

  internal void ScrollDown(int lines)
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    long offset = _state.TextTopOffset;
    for (int i = 0; i < lines && offset < doc.Length; i++) {
      long lineEnd = FindLineEnd(offset);
      long nextStart = lineEnd + 1;
      if (nextStart > doc.Length) nextStart = doc.Length;
      offset = nextStart;
    }
    _state.TextTopOffset = Math.Min(offset, doc.Length);
    SetNeedsDraw();
  }

  internal void Home(bool extend)
  {
    Document? doc = _state.Document;
    if (doc is null) return;
    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;
    long lineStart = FindLineStart(_state.TextCursorOffset);
    // On line 1, Home stops at BOM boundary, not byte 0
    if (lineStart < _state.BomLength)
      lineStart = _state.BomLength;
    _state.TextCursorOffset = lineStart;
    OnStateChanged();
  }

  internal void End(bool extend)
  {
    Document? doc = _state.Document;
    if (doc is null) return;
    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;
    _state.TextCursorOffset = FindLineEnd(_state.TextCursorOffset);
    OnStateChanged();
  }

  internal void CtrlHome(bool extend)
  {
    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;
    _state.TextCursorOffset = _state.BomLength;
    OnStateChanged();
  }

  internal void CtrlEnd(bool extend)
  {
    if (_state.Document is null) return;
    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;
    _state.TextCursorOffset = _state.Document.Length;
    OnStateChanged();
  }

  internal void GotoLine(long lineNumber)
  {
    Document? doc = _state.Document;
    if (doc is null || lineNumber < 1) return;
    long offset = FindOffsetOfLine(lineNumber);
    _state.TextCursorOffset = offset;
    _state.TextSelectionAnchor = -1;
    OnStateChanged();
  }

  internal void GotoOffset(long offset)
  {
    if (_state.Document is null) return;
    _state.TextCursorOffset = Math.Clamp(offset, 0, _state.Document.Length);
    _state.TextSelectionAnchor = -1;
    OnStateChanged();
  }

  internal void SelectAll()
  {
    Document? doc = _state.Document;
    if (doc is null || doc.Length == 0) return;
    _state.TextSelectionAnchor = 0;
    _state.TextCursorOffset = doc.Length - 1;
    OnStateChanged();
  }

  // ─── Editing ───

  internal void InsertChar(char c)
  {
    Document? doc = _state.Document;
    if (doc is null) return;
    DeleteSelection();

    Span<byte> encoded = stackalloc byte[8];
    int len = _state.Decoder.EncodeRune(new Rune(c), encoded);
    if (len > 0) {
      doc.Insert(_state.TextCursorOffset, encoded[..len]);
      _state.TextCursorOffset += len;
    }
    OnStateChanged();
  }

  internal void InsertNewline()
  {
    Document? doc = _state.Document;
    if (doc is null) return;
    DeleteSelection();

    ITextDecoder decoder = _state.Decoder;
    if (decoder.Encoding == TextEncoding.Utf16Le) {
      doc.Insert(_state.TextCursorOffset, [0x0A, 0x00]);
      _state.TextCursorOffset += 2;
    } else {
      doc.Insert(_state.TextCursorOffset, [(byte)'\n']);
      _state.TextCursorOffset++;
    }
    OnStateChanged();
  }

  internal void Backspace()
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    if (_state.TextSelectionAnchor >= 0) { DeleteSelection(); OnStateChanged(); return; }
    if (_state.TextCursorOffset <= 0) return;

    int lookBack = (int)Math.Min(4, _state.TextCursorOffset);
    Span<byte> prev = stackalloc byte[lookBack];
    doc.Read(_state.TextCursorOffset - lookBack, prev);

    ITextDecoder decoder = _state.Decoder;
    int pos = 0;
    int lastRuneStart = 0;
    while (pos < lookBack) {
      lastRuneStart = pos;
      (_, int len) = decoder.DecodeRune(prev, pos);
      if (len <= 0) { pos++; continue; }
      pos += len;
    }

    int deleteLen = lookBack - lastRuneStart;
    _state.TextCursorOffset -= deleteLen;
    doc.Delete(_state.TextCursorOffset, deleteLen);
    OnStateChanged();
  }

  internal void Delete()
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    if (_state.TextSelectionAnchor >= 0) { DeleteSelection(); OnStateChanged(); return; }
    if (_state.TextCursorOffset >= doc.Length) return;

    Span<byte> next = stackalloc byte[4];
    int read = doc.Read(_state.TextCursorOffset, next);
    if (read == 0) return;

    (_, int len) = _state.Decoder.DecodeRune(next[..read], 0);
    if (len <= 0) len = 1;
    doc.Delete(_state.TextCursorOffset, len);
    OnStateChanged();
  }

  // ─── Clipboard ───

  internal string? CopySelection()
  {
    Document? doc = _state.Document;
    if (doc is null || _state.TextSelectionAnchor < 0) return null;

    long start = _state.TextSelStart;
    long len = _state.TextSelEnd - start + 1;
    if (len <= 0 || len > 10 * 1024 * 1024) return null;

    byte[] selBytes = new byte[(int)len];
    doc.Read(start, selBytes);

    ITextDecoder decoder = _state.Decoder;
    if (decoder.Encoding == TextEncoding.Utf8)
      return Encoding.UTF8.GetString(selBytes);

    StringBuilder sb = new();
    int pos = 0;
    while (pos < selBytes.Length) {
      (Rune rune, int runeLen) = decoder.DecodeRune(selBytes, pos);
      if (runeLen <= 0) { pos++; continue; }
      sb.Append(char.ConvertFromUtf32(rune.Value));
      pos += runeLen;
    }
    return sb.ToString();
  }

  internal void Paste(string text)
  {
    Document? doc = _state.Document;
    if (doc is null || string.IsNullOrEmpty(text)) return;
    DeleteSelection();

    ITextDecoder decoder = _state.Decoder;
    if (decoder.Encoding == TextEncoding.Utf8) {
      byte[] utf8 = Encoding.UTF8.GetBytes(text);
      doc.Insert(_state.TextCursorOffset, utf8);
      _state.TextCursorOffset += utf8.Length;
    } else {
      Span<byte> runeBuf = stackalloc byte[8];
      List<byte> encoded = [];
      foreach (int cp in StringToCodePoints(text)) {
        if (Rune.TryCreate(cp, out Rune rune)) {
          int len = decoder.EncodeRune(rune, runeBuf);
          if (len > 0)
            encoded.AddRange(runeBuf[..len].ToArray());
        }
      }
      byte[] data = encoded.ToArray();
      doc.Insert(_state.TextCursorOffset, data);
      _state.TextCursorOffset += data.Length;
    }
    OnStateChanged();
  }

  // ─── Mouse click ───

  internal void ClickAtPosition(int viewRow, int viewCol, bool extend = false)
  {
    Document? doc = _state.Document;
    if (doc is null) return;
    if (viewRow < 0 || viewRow >= _lastRenderedLineCount) return;

    VisualLine vl = _visualLines[viewRow];

    int textCol = viewCol - GutterWidth;
    if (textCol < 0) textCol = 0;

    long relativeStart = vl.DocOffset - _state.TextTopOffset;
    if (relativeStart < 0) return;

    int lineStart = (int)relativeStart;
    int lineByteLen = vl.ByteLength;
    int maxLen = _readBuffer.Length - lineStart;
    if (lineByteLen > maxLen) lineByteLen = maxLen;
    if (lineByteLen <= 0) {
      if (!extend) _state.TextSelectionAnchor = -1;
      else if (_state.TextSelectionAnchor < 0)
        _state.TextSelectionAnchor = _state.TextCursorOffset;
      _state.TextCursorOffset = vl.DocOffset;
      OnStateChanged();
      return;
    }

    ReadOnlySpan<byte> lineBytes = _readBuffer.AsSpan(lineStart, lineByteLen);
    List<int> offsets = new(lineByteLen);
    ITextDecoder decoder = _state.Decoder;
    DecodeLineToDisplay(lineBytes, decoder, _state.TabWidth, offsets);

    int charIdx = Math.Min(textCol, offsets.Count);
    long newOffset;
    if (charIdx >= offsets.Count) {
      newOffset = vl.DocOffset + lineByteLen;
      if (newOffset > doc.Length) newOffset = doc.Length;
      if (newOffset > 0 && newOffset == doc.Length) newOffset = doc.Length - 1;
    } else {
      newOffset = vl.DocOffset + offsets[charIdx];
    }

    newOffset = Math.Clamp(newOffset, 0, Math.Max(0, doc.Length - 1));

    if (!extend)
      _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;

    _state.TextCursorOffset = newOffset;
    OnStateChanged();
  }

  // ─── Helpers ───

  private void DeleteSelection()
  {
    Document? doc = _state.Document;
    if (doc is null || _state.TextSelectionAnchor < 0) return;

    long start = _state.TextSelStart;
    long len = _state.TextSelEnd - start + 1;
    doc.Delete(start, len);
    _state.TextCursorOffset = start;
    _state.TextSelectionAnchor = -1;
  }

  private void MoveVertical(int direction, bool extend)
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;

    int bom = _state.BomLength;

    if (direction < 0) {
      long lineStart = FindLineStart(_state.TextCursorOffset);
      if (lineStart == 0 && _state.TextCursorOffset <= bom) return;
      if (lineStart == 0) lineStart = bom; // effective start on line 1

      long prevLineEnd = lineStart - 1;
      long prevLineStart = FindLineStart(prevLineEnd);
      // Effective start accounts for BOM on line 1
      long effectivePrevStart = prevLineStart < bom ? bom : prevLineStart;

      long col = _state.TextCursorOffset - (FindLineStart(_state.TextCursorOffset) < bom ? bom : FindLineStart(_state.TextCursorOffset));
      _state.TextCursorOffset = Math.Min(effectivePrevStart + col, prevLineEnd);
    } else {
      long lineEnd = FindLineEnd(_state.TextCursorOffset);
      if (lineEnd >= doc.Length) return;
      long lineStart = FindLineStart(_state.TextCursorOffset);
      long effectiveStart = lineStart < bom ? bom : lineStart;
      long col = _state.TextCursorOffset - effectiveStart;
      long nextLineStart = lineEnd + _state.Decoder.MinCharBytes;
      long nextLineEnd = FindLineEnd(nextLineStart);
      _state.TextCursorOffset = Math.Min(nextLineStart + col, nextLineEnd);
    }
    OnStateChanged();
  }

  private long FindLineStart(long offset)
  {
    Document? doc = _state.Document;
    if (doc is null || offset <= 0) return 0;

    int lookBack = (int)Math.Min(4096, offset);
    byte[] buf = new byte[lookBack];
    doc.Read(offset - lookBack, buf);

    ITextDecoder decoder = _state.Decoder;
    int minChar = decoder.MinCharBytes;

    for (int i = lookBack - minChar; i >= 0; i -= minChar) {
      if (decoder.IsNewline(buf, i, out _))
        return offset - lookBack + i + minChar;
    }
    return offset - lookBack;
  }

  private long FindLineEnd(long offset)
  {
    Document? doc = _state.Document;
    if (doc is null) return offset;

    int lookAhead = (int)Math.Min(4096, doc.Length - offset);
    if (lookAhead <= 0) return offset;

    byte[] buf = new byte[lookAhead];
    doc.Read(offset, buf);

    ITextDecoder decoder = _state.Decoder;
    int minChar = decoder.MinCharBytes;

    for (int i = 0; i + minChar <= lookAhead; i += minChar) {
      if (decoder.IsNewline(buf, i, out _))
        return offset + i;
    }
    return Math.Min(offset + lookAhead, doc.Length);
  }

  private void EnsureCursorVisible(int textAreaCols)
  {
    if (_state.TextCursorOffset < 0) return;
    if (_state.TextCursorOffset < _state.TextTopOffset)
      _state.TextTopOffset = FindLineStart(_state.TextCursorOffset);

    long visibleEnd = ComputeVisibleEnd(_state.TextTopOffset, _state.VisibleRows);
    if (_state.TextCursorOffset > visibleEnd) {
      long newTop = FindLineStart(_state.TextCursorOffset);
      for (int i = 0; i < _state.VisibleRows / 2 && newTop > 0; i++) {
        long prev = FindLineStart(Math.Max(0, newTop - 1));
        if (prev >= newTop) break;
        newTop = prev;
      }
      _state.TextTopOffset = newTop;
    }
  }

  private long ComputeVisibleEnd(long startOffset, int lineCount)
  {
    Document? doc = _state.Document;
    if (doc is null) return startOffset;

    ITextDecoder decoder = _state.Decoder;
    int linesFound = 0;
    long pos = startOffset;
    byte[] scanBuf = new byte[32768];

    while (pos < doc.Length && linesFound < lineCount) {
      int readLen = (int)Math.Min(scanBuf.Length, doc.Length - pos);
      int read = doc.Read(pos, scanBuf.AsSpan(0, readLen));
      if (read == 0) break;

      int minChar = decoder.MinCharBytes;
      for (int i = 0; i + minChar <= read; i += minChar) {
        if (decoder.IsNewline(scanBuf, i, out _)) {
          linesFound++;
          if (linesFound >= lineCount)
            return pos + i + minChar;
        }
      }
      pos += read;
    }
    return pos;
  }

  private long ComputeLineNumber(long offset)
  {
    Document? doc = _state.Document;
    if (doc is null) return 1;

    long from, to;
    long lineNum;
    if (offset >= _cachedTopOffset) {
      from = _cachedTopOffset;
      to = offset;
      lineNum = _cachedTopLineNumber;
    } else {
      from = 0;
      to = offset;
      lineNum = 1;
    }

    if (to - from > 10 * 1024 * 1024)
      lineNum = Math.Max(1, (long)((double)offset / doc.Length * _state.EstimatedTotalLines));
    else
      lineNum += CountNewlines(from, to);

    _cachedTopOffset = offset;
    _cachedTopLineNumber = lineNum;
    return lineNum;
  }

  private long CountNewlines(long from, long to)
  {
    Document? doc = _state.Document;
    if (doc is null) return 0;

    ITextDecoder decoder = _state.Decoder;
    long count = 0;
    long pos = from;
    byte[] buf = new byte[8192];

    while (pos < to) {
      int readLen = (int)Math.Min(buf.Length, to - pos);
      int read = doc.Read(pos, buf.AsSpan(0, readLen));
      if (read == 0) break;

      int minChar = decoder.MinCharBytes;
      for (int i = 0; i + minChar <= read; i += minChar) {
        if (decoder.IsNewline(buf, i, out _))
          count++;
      }
      pos += read;
    }
    return count;
  }

  private long FindOffsetOfLine(long targetLine)
  {
    Document? doc = _state.Document;
    if (doc is null || targetLine <= 1) return 0;

    ITextDecoder decoder = _state.Decoder;
    long linesFound = 0;
    long pos = 0;
    byte[] buf = new byte[8192];

    while (pos < doc.Length && linesFound < targetLine - 1) {
      int readLen = (int)Math.Min(buf.Length, doc.Length - pos);
      int read = doc.Read(pos, buf.AsSpan(0, readLen));
      if (read == 0) break;

      int minChar = decoder.MinCharBytes;
      for (int i = 0; i + minChar <= read; i += minChar) {
        if (decoder.IsNewline(buf, i, out _)) {
          linesFound++;
          if (linesFound >= targetLine - 1)
            return pos + i + minChar;
        }
      }
      pos += read;
    }
    return pos;
  }

  private static bool IsNewlineAt(ReadOnlySpan<byte> buf, int index, ITextDecoder decoder)
  {
    if (index < 0 || index >= buf.Length) return false;
    return decoder.IsNewline(buf, index, out _);
  }

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

  private static string DecodeLineToDisplay(ReadOnlySpan<byte> bytes, ITextDecoder decoder, int tabWidth,
      List<int>? byteOffsets = null)
  {
    StringBuilder sb = new(bytes.Length);
    byteOffsets?.Clear();
    int pos = 0;
    while (pos < bytes.Length) {
      (Rune rune, int len) = decoder.DecodeRune(bytes, pos);
      if (len <= 0) { pos++; continue; }

      int codePoint = rune.Value;
      if (codePoint == '\n' || codePoint == '\r') {
        pos += len;
        continue;
      }
      // Skip BOM character (U+FEFF) — invisible zero-width no-break space
      if (codePoint == 0xFEFF) {
        pos += len;
        continue;
      }
      if (codePoint == '\t') {
        for (int i = 0; i < tabWidth; i++) {
          sb.Append(' ');
          byteOffsets?.Add(pos);
        }
        pos += len;
        continue;
      }
      if (codePoint < 0x20) {
        sb.Append('.');
        byteOffsets?.Add(pos);
        pos += len;
        continue;
      }

      if (codePoint <= 0xFFFF) {
        sb.Append((char)codePoint);
        byteOffsets?.Add(pos);
      } else {
        string surr = char.ConvertFromUtf32(codePoint);
        sb.Append(surr);
        byteOffsets?.Add(pos);
        if (surr.Length > 1)
          byteOffsets?.Add(pos);
      }

      pos += len;
    }
    return sb.ToString();
  }

  private static IEnumerable<int> StringToCodePoints(string s)
  {
    for (int i = 0; i < s.Length; i++) {
      if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) {
        yield return char.ConvertToUtf32(s[i], s[i + 1]);
        i++;
      } else {
        yield return s[i];
      }
    }
  }

  private void OnStateChanged()
  {
    SetNeedsDraw();
    StateChanged?.Invoke();
  }

  private void UpdateScrollBar(int vpHeight)
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    long totalLines = Math.Max(1, _state.EstimatedTotalLines);
    int scrollTotal = (int)Math.Min(totalLines, int.MaxValue - 1);
    double fraction = doc.Length > 0 ? (double)_state.TextTopOffset / doc.Length : 0;
    int scrollPos = (int)(fraction * scrollTotal);

    _verticalScrollBar.ScrollableContentSize = scrollTotal;
    _verticalScrollBar.VisibleContentSize = vpHeight;
    _verticalScrollBar.Value = Math.Clamp(scrollPos, 0, Math.Max(0, scrollTotal - vpHeight));
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void EnsureBuffer(int size)
  {
    if (_readBuffer.Length < size)
      _readBuffer = new byte[size];
  }

  private void EnsureVisualLines(int count)
  {
    if (_visualLines.Length < count)
      _visualLines = new VisualLine[count];
  }
}
