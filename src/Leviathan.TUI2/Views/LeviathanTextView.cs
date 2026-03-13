using Leviathan.Core;
using Leviathan.Core.Indexing;
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
  /// <summary>Remembered display column for Notepad-like vertical movement. -1 = not set.</summary>
  private int _desiredColumn = -1;
  private readonly ScrollBar _verticalScrollBar;
  private readonly ScrollBar _horizontalScrollBar;
  private bool _updatingScrollBar;
  private int _horizontalScrollOffset;
  private int _maxLineWidthInViewport;
  /// <summary>Reusable buffer for navigation visual-line computation (separate from _visualLines).</summary>
  private VisualLine[] _navVisualLines = new VisualLine[256];
  /// <summary>Last text-area column count from rendering, for navigation helpers.</summary>
  private int _lastTextAreaCols;

  private const int GutterWidth = 9; // "  12345 │"

  /// <summary>
  /// Fired when the view needs the status bar to update.
  /// </summary>
  internal event Action? StateChanged;

  internal LeviathanTextView(AppState state)
  {
    _state = state;
    CanFocus = true;

    // Same reason as LeviathanHexView: do NOT set ContentSizeTracksViewport = false.
    // This view manages its own scroll position via _state.TextTopOffset and
    // explicit ScrollBar children whose Value/Visible are set directly in
    // UpdateScrollBar().  Activating Terminal.Gui's built-in scroll management
    // (via ContentSizeTracksViewport or ScrollBarVisibilityMode.Always) triggers
    // the DoDrawAdornments → Margin.VerticalScrollBar NRE in develop build 5185.

    // Vertical scrollbar
    _verticalScrollBar = new ScrollBar() {
      Orientation = Orientation.Vertical,
      X = Pos.AnchorEnd(1),
      Y = 0,
      Width = 1,
      Height = Dim.Fill(),
    };
    _verticalScrollBar.ValueChanged += (_, e) => {
      if (_updatingScrollBar || _state.Document is null) return;
      long totalLines = Math.Max(1, _state.EstimatedTotalLines);
      long newOffset = (long)((double)e.NewValue / Math.Max(1, totalLines) * _state.Document.Length);
      newOffset = Math.Clamp(newOffset, 0, _state.Document.Length);
      _state.TextTopOffset = FindLineStart(newOffset);
      SetNeedsDraw();
    };
    Add(_verticalScrollBar);

    // Horizontal scrollbar (visible only when word wrap is off)
    _horizontalScrollBar = new ScrollBar() {
      Orientation = Orientation.Horizontal,
      X = GutterWidth,
      Y = Pos.AnchorEnd(1),
      Width = Dim.Fill(1), // leave room for vertical scrollbar
      Height = 1,
      Visible = !_state.WordWrap,
    };
    _horizontalScrollBar.ValueChanged += (_, e) => {
      if (_updatingScrollBar) return;
      _horizontalScrollOffset = Math.Max(0, e.NewValue);
      SetNeedsDraw();
    };
    Add(_horizontalScrollBar);

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

    // Printable character input — skip function keys and other special keys
    if (!keyEvent.IsCtrl && !keyEvent.IsAlt
        && (keyEvent.KeyCode & KeyCode.SpecialMask) == 0) {
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

    int textAreaCols = Math.Max(1, vpWidth - GutterWidth - 1); // -1 for vertical scrollbar
    _lastTextAreaCols = textAreaCols;

    // Update horizontal scrollbar visibility
    _horizontalScrollBar.Visible = !_state.WordWrap;
    if (_state.WordWrap) {
      _horizontalScrollOffset = 0;
      _maxLineWidthInViewport = 0;
    }

    // Reserve a row for the horizontal scrollbar so content doesn't draw under it
    if (_horizontalScrollBar.Visible)
      vpHeight = Math.Max(1, vpHeight - 1);

    _state.VisibleRows = vpHeight;

    int frameMaxWidth = 0;

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
    List<SearchResult> searchResults = _state.SearchResults;
    List<SearchResult> visibleMatches = CollectVisibleMatches(searchResults, viewStart, viewEnd);
    int currentMatchIdx = _state.CurrentMatchIndex;
    SearchResult? activeMatch = currentMatchIdx >= 0 && currentMatchIdx < searchResults.Count
        ? searchResults[currentMatchIdx]
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
    int textColumnCapacity = Math.Max(0, vpWidth - 1 - GutterWidth);
    long textCursorOffset = _state.TextCursorOffset;
    long textSelStart = _state.TextSelStart;
    long textSelEnd = _state.TextSelEnd;
    Span<char> lineNumberChars = stackalloc char[20];
    for (int i = 0; i < rowsToDraw; i++) {
      VisualLine vl = _visualLines[i];
      long lineDocOffset = vl.DocOffset;
      long relativeStart = lineDocOffset - _state.TextTopOffset;

      Attribute currentAttr = default;
      bool hasCurrentAttr = false;

      Move(0, i);

      if (relativeStart < 0 || relativeStart > bytesRead) {
        DrawEmptyLine(i, vpWidth);
        continue;
      }

      int lineStart = (int)relativeStart;
      int lineByteLen = vl.ByteLength;
      if (lineByteLen < 0 || lineByteLen > bytesRead - lineStart)
        lineByteLen = Math.Max(0, bytesRead - lineStart);

      bool isHardLine = lineStart == 0 || IsNewlineAt(data, lineStart - decoder.MinCharBytes, decoder);
      if (isHardLine)
        currentLineNumber++;

      // Gutter
      currentAttr = gutterAttr;
      hasCurrentAttr = true;
      SetAttribute(gutterAttr);
      if (isHardLine) {
        long lineNumber = currentLineNumber - 1;
        if (!lineNumber.TryFormat(lineNumberChars, out int lineNumLen))
          lineNumLen = 0;
        int padding = Math.Max(0, 7 - lineNumLen);
        for (int p = 0; p < padding; p++)
          AddRune(' ');
        for (int c = 0; c < lineNumLen; c++)
          AddRune(lineNumberChars[c]);
        AddRune(' ');
        AddRune('│');
      } else {
        if (!currentAttr.Equals(wrapIndicatorAttr)) {
          SetAttribute(wrapIndicatorAttr);
          currentAttr = wrapIndicatorAttr;
        }
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

      // Track max line width for horizontal scrollbar
      if (text.Length > frameMaxWidth)
        frameMaxWidth = text.Length;

      // Apply horizontal scroll offset when word wrap is off
      int hOffset = _state.WordWrap ? 0 : _horizontalScrollOffset;
      int visibleStart = Math.Min(hOffset, text.Length);
      int visibleEnd = text.Length;
      int charsToDraw = Math.Min(visibleEnd - visibleStart, textColumnCapacity);
      int charOffsetsCount = _charByteOffsets.Count;
      int visibleMatchCount = visibleMatches.Count;
      int matchIndex = 0;

      if (visibleMatchCount > 0) {
        long firstVisibleOffset = lineDocOffset;
        if (visibleStart < charOffsetsCount)
          firstVisibleOffset = lineDocOffset + _charByteOffsets[visibleStart];

        while (matchIndex < visibleMatchCount) {
          SearchResult m = visibleMatches[matchIndex];
          if (m.Offset + m.Length > firstVisibleOffset)
            break;
          matchIndex++;
        }
      }

      for (int ci = visibleStart; ci < visibleStart + charsToDraw; ci++) {
        long charDocOffset = lineDocOffset;
        if (ci < charOffsetsCount)
          charDocOffset = lineDocOffset + _charByteOffsets[ci];

        Attribute attr;
        if (charDocOffset == textCursorOffset) {
          attr = cursorAttr;
        } else if (textSelStart >= 0 && charDocOffset >= textSelStart && charDocOffset <= textSelEnd) {
          attr = selectionAttr;
        } else if (activeMatch is { } am
            && charDocOffset >= am.Offset && charDocOffset < am.Offset + am.Length) {
          attr = activeMatchAttr;
        } else {
          while (matchIndex < visibleMatchCount) {
            SearchResult m = visibleMatches[matchIndex];
            long mEnd = m.Offset + m.Length;
            if (charDocOffset < mEnd) {
              if (charDocOffset >= m.Offset) {
                attr = matchAttr;
              } else {
                attr = normalAttr;
              }
              goto ApplyTextAttr;
            }
            matchIndex++;
          }

          attr = normalAttr;
        }

      ApplyTextAttr:
        if (!currentAttr.Equals(attr)) {
          SetAttribute(attr);
          currentAttr = attr;
        }
        AddRune(text[ci]);
      }

      // Cursor at end of line: either exactly at endOffset (last line / no next line)
      // or in the newline-bytes area (after the last visible char, before endOffset).
      // Also handles empty lines where cursor == lineDocOffset.
      long endOffset = lineDocOffset + lineByteLen;
      bool nextLineStartsHere = (i + 1 < rowsToDraw) && _visualLines[i + 1].DocOffset == endOffset;
      int endCol = GutterWidth + (text.Length - visibleStart);
      bool cursorInNewlineArea = text.Length > 0
          && _charByteOffsets.Count > 0
          && _state.TextCursorOffset > lineDocOffset + _charByteOffsets[_charByteOffsets.Count - 1]
          && _state.TextCursorOffset < endOffset;
      bool cursorAtEnd = (_state.TextCursorOffset == endOffset && !nextLineStartsHere)
          || cursorInNewlineArea;
      bool cursorOnEmptyLine = text.Length == 0 && _state.TextCursorOffset == lineDocOffset;
      if ((cursorAtEnd || cursorOnEmptyLine)
          && endCol < vpWidth - 1 && text.Length >= hOffset) {
        if (!currentAttr.Equals(cursorAttr)) {
          SetAttribute(cursorAttr);
          currentAttr = cursorAttr;
        }
        AddRune(' ');
        endCol++;
      }

      // Clear rest of line
      if (!currentAttr.Equals(normalAttr)) {
        SetAttribute(normalAttr);
        currentAttr = normalAttr;
      }
      for (int c = Math.Max(GutterWidth, endCol); c < vpWidth; c++)
        AddRune(' ');
    }

    // Empty rows below content
    for (int i = rowsToDraw; i < vpHeight; i++)
      DrawEmptyLine(i, vpWidth);

    _maxLineWidthInViewport = frameMaxWidth;

    // Update vertical scrollbar
    UpdateScrollBar(vpHeight, textAreaCols);

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
    if (doc is null || _state.TextCursorOffset <= _state.BomLength) return;

    _desiredColumn = -1;
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
    // Never step into the BOM
    _state.TextCursorOffset = Math.Max(_state.TextCursorOffset, _state.BomLength);

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

    _desiredColumn = -1;
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

  /// <summary>Skips past exactly one newline sequence (\n or \r\n) when moving forward.</summary>
  private void SkipNewlinesForward(Document doc)
  {
    if (_state.TextCursorOffset >= doc.Length) return;
    Span<byte> peek = stackalloc byte[4];
    int peekRead = doc.Read(_state.TextCursorOffset, peek);
    if (peekRead == 0) return;
    (Rune r, int rLen) = _state.Decoder.DecodeRune(peek[..peekRead], 0);
    if (rLen <= 0) return;
    if (r.Value == '\r') {
      _state.TextCursorOffset = Math.Min(_state.TextCursorOffset + rLen, doc.Length);
      // Also skip a following \n
      if (_state.TextCursorOffset < doc.Length) {
        peekRead = doc.Read(_state.TextCursorOffset, peek);
        if (peekRead > 0) {
          (Rune r2, int rLen2) = _state.Decoder.DecodeRune(peek[..peekRead], 0);
          if (rLen2 > 0 && r2.Value == '\n')
            _state.TextCursorOffset = Math.Min(_state.TextCursorOffset + rLen2, doc.Length);
        }
      }
    } else if (r.Value == '\n') {
      _state.TextCursorOffset = Math.Min(_state.TextCursorOffset + rLen, doc.Length);
    }
  }

  /// <summary>Skips back over exactly one newline sequence (\n or \r\n) when moving backward.</summary>
  private void SkipNewlinesBackward(Document doc)
  {
    if (_state.TextCursorOffset <= _state.BomLength) return;
    Span<byte> buf = stackalloc byte[4];
    ITextDecoder decoder = _state.Decoder;

    // Decode the rune immediately before the cursor
    int lookBack = (int)Math.Min(4, _state.TextCursorOffset - _state.BomLength);
    Span<byte> prevSlice = buf[..lookBack];
    doc.Read(_state.TextCursorOffset - lookBack, prevSlice);

    int pos = 0;
    int lastRuneStart = 0;
    while (pos < lookBack) {
      lastRuneStart = pos;
      (_, int l) = decoder.DecodeRune(prevSlice, pos);
      if (l <= 0) { pos++; continue; }
      pos += l;
    }
    int prevRuneOffset = lookBack - lastRuneStart;
    int checkRead = doc.Read(_state.TextCursorOffset - prevRuneOffset, buf);
    if (checkRead == 0) return;
    (Rune r, int rLen) = decoder.DecodeRune(buf[..checkRead], 0);
    if (rLen <= 0) return;

    if (r.Value == '\n') {
      _state.TextCursorOffset -= prevRuneOffset;
      // Also skip a preceding \r (to handle \r\n as one sequence)
      if (_state.TextCursorOffset > _state.BomLength) {
        lookBack = (int)Math.Min(4, _state.TextCursorOffset - _state.BomLength);
        prevSlice = buf[..lookBack];
        doc.Read(_state.TextCursorOffset - lookBack, prevSlice);
        pos = 0; lastRuneStart = 0;
        while (pos < lookBack) {
          lastRuneStart = pos;
          (_, int l) = decoder.DecodeRune(prevSlice, pos);
          if (l <= 0) { pos++; continue; }
          pos += l;
        }
        prevRuneOffset = lookBack - lastRuneStart;
        checkRead = doc.Read(_state.TextCursorOffset - prevRuneOffset, buf);
        if (checkRead > 0) {
          (Rune r2, int rLen2) = decoder.DecodeRune(buf[..checkRead], 0);
          if (rLen2 > 0 && r2.Value == '\r')
            _state.TextCursorOffset -= prevRuneOffset;
        }
      }
    } else if (r.Value == '\r') {
      _state.TextCursorOffset -= prevRuneOffset;
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

    if (_state.WordWrap) {
      int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
      long offset = _state.TextTopOffset;
      for (int i = 0; i < lines && offset > _state.BomLength; i++) {
        if (FindPreviousVisualLine(offset, maxCols, out VisualLine prevVl))
          offset = prevVl.DocOffset;
        else break;
      }
      _state.TextTopOffset = offset;
      SetNeedsDraw();
      return;
    }

    int minChar = _state.Decoder.MinCharBytes;
    long off = _state.TextTopOffset;
    for (int i = 0; i < lines && off > 0; i++) {
      long prevLineEnd = Math.Max(0, off - minChar);
      off = FindLineStart(prevLineEnd);
    }
    _state.TextTopOffset = off;
    SetNeedsDraw();
  }

  internal void ScrollDown(int lines)
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    if (_state.WordWrap) {
      int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
      long offset = _state.TextTopOffset;
      for (int i = 0; i < lines && offset < doc.Length; i++) {
        if (FindNextVisualLine(offset, maxCols, out VisualLine nextVl)) {
          long newOffset = nextVl.DocOffset + nextVl.ByteLength;
          if (newOffset <= offset) break;
          offset = newOffset;
        } else break;
      }
      _state.TextTopOffset = Math.Min(offset, doc.Length);
      SetNeedsDraw();
      return;
    }

    long off = _state.TextTopOffset;
    for (int i = 0; i < lines && off < doc.Length; i++) {
      long lineEnd = FindLineEnd(off);
      int nlLen = NewlineLengthAt(lineEnd);
      long nextStart = lineEnd + (nlLen > 0 ? nlLen : 1);
      if (nextStart > doc.Length) nextStart = doc.Length;
      off = nextStart;
    }
    _state.TextTopOffset = Math.Min(off, doc.Length);
    SetNeedsDraw();
  }

  internal void Home(bool extend)
  {
    Document? doc = _state.Document;
    if (doc is null) return;
    _desiredColumn = -1;
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
    _desiredColumn = -1;
    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;
    _state.TextCursorOffset = FindLineEnd(_state.TextCursorOffset);
    OnStateChanged();
  }

  internal void CtrlHome(bool extend)
  {
    _desiredColumn = -1;
    if (!extend) _state.TextSelectionAnchor = -1;
    else if (_state.TextSelectionAnchor < 0)
      _state.TextSelectionAnchor = _state.TextCursorOffset;
    _state.TextCursorOffset = _state.BomLength;
    OnStateChanged();
  }

  internal void CtrlEnd(bool extend)
  {
    if (_state.Document is null) return;
    _desiredColumn = -1;
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
    _desiredColumn = -1;
    long offset = FindOffsetOfLine(lineNumber);
    _state.TextCursorOffset = offset;
    _state.TextSelectionAnchor = -1;
    ResetHorizontalExtent();
    OnStateChanged();
  }

  internal void GotoOffset(long offset)
  {
    if (_state.Document is null) return;
    _state.TextCursorOffset = Math.Clamp(offset, 0, _state.Document.Length);
    _state.TextSelectionAnchor = -1;
    ResetHorizontalExtent();
    OnStateChanged();
  }

  /// <summary>Resets horizontal scroll state. Call on structural changes (word wrap toggle, goto, file open).</summary>
  internal void ResetHorizontalExtent()
  {
    _maxLineWidthInViewport = 0;
    _horizontalScrollOffset = 0;
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
    _desiredColumn = -1;
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
    _desiredColumn = -1;
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

    _desiredColumn = -1;
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

    _desiredColumn = -1;
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
    _desiredColumn = -1;
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

    // When word wrap is on, navigate by visual lines using the last rendered layout
    if (_state.WordWrap && _lastRenderedLineCount > 0) {
      MoveVerticalVisual(direction);
      OnStateChanged();
      return;
    }

    // No wrap: navigate by hard lines (original behavior) using display columns
    int bom = _state.BomLength;
    ITextDecoder decoder = _state.Decoder;

    if (direction < 0) {
      long lineStart = FindLineStart(_state.TextCursorOffset);
      if (lineStart == 0 && _state.TextCursorOffset <= bom) return;
      if (lineStart == 0) lineStart = bom; // effective start on line 1

      // Compute display column on current line
      long lineEnd = FindLineEnd(_state.TextCursorOffset);
      int curLineLen = (int)Math.Min(lineEnd - lineStart + NewlineLengthAt(lineEnd), int.MaxValue);
      EnsureBuffer(curLineLen);
      doc.Read(lineStart, _readBuffer.AsSpan(0, curLineLen));
      int col;
      if (_desiredColumn >= 0) {
        col = _desiredColumn;
      } else {
        col = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, curLineLen), _state.TextCursorOffset - lineStart, decoder);
        _desiredColumn = col;
      }

      int minChar = decoder.MinCharBytes;
      long prevLineEnd = lineStart - minChar;
      long prevLineStart = FindLineStart(prevLineEnd);
      long effectivePrevStart = prevLineStart < bom ? bom : prevLineStart;

      int prevLen = (int)Math.Min(prevLineEnd - effectivePrevStart + NewlineLengthAt(prevLineEnd), int.MaxValue);
      EnsureBuffer(prevLen);
      doc.Read(effectivePrevStart, _readBuffer.AsSpan(0, prevLen));
      int byteCol = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, prevLen), col, decoder);
      _state.TextCursorOffset = Math.Min(effectivePrevStart + byteCol, prevLineEnd);
    } else {
      long lineEnd = FindLineEnd(_state.TextCursorOffset);
      if (lineEnd >= doc.Length) return;
      long lineStart = FindLineStart(_state.TextCursorOffset);
      long effectiveStart = lineStart < bom ? bom : lineStart;

      // Compute display column on current line
      int curLineLen = (int)Math.Min(lineEnd - effectiveStart + NewlineLengthAt(lineEnd), int.MaxValue);
      EnsureBuffer(curLineLen);
      doc.Read(effectiveStart, _readBuffer.AsSpan(0, curLineLen));
      int col;
      if (_desiredColumn >= 0) {
        col = _desiredColumn;
      } else {
        col = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, curLineLen), _state.TextCursorOffset - effectiveStart, decoder);
        _desiredColumn = col;
      }

      long nextLineStart = lineEnd + NewlineLengthAt(lineEnd);
      long nextLineEnd = FindLineEnd(nextLineStart);
      int nextLen = (int)Math.Min(nextLineEnd - nextLineStart + NewlineLengthAt(nextLineEnd), int.MaxValue);
      EnsureBuffer(nextLen);
      doc.Read(nextLineStart, _readBuffer.AsSpan(0, nextLen));
      int byteCol = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, nextLen), col, decoder);
      _state.TextCursorOffset = Math.Min(nextLineStart + byteCol, nextLineEnd);
    }
    OnStateChanged();
  }

  /// <summary>
  /// Moves cursor up/down by one visual (wrapped) line using the last rendered layout.
  /// </summary>
  private void MoveVerticalVisual(int direction)
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    // Find which visual line the cursor is on
    int cursorRow = -1;
    for (int i = 0; i < _lastRenderedLineCount; i++) {
      long vlStart = _visualLines[i].DocOffset;
      long vlEnd = vlStart + _visualLines[i].ByteLength;
      if (_state.TextCursorOffset >= vlStart && _state.TextCursorOffset < vlEnd) {
        cursorRow = i;
        break;
      }
      // Cursor at the very end of this line (and not at start of next)
      if (_state.TextCursorOffset == vlEnd) {
        bool nextLineStartsHere = (i + 1 < _lastRenderedLineCount) && _visualLines[i + 1].DocOffset == vlEnd;
        if (!nextLineStartsHere) {
          cursorRow = i;
          break;
        }
      }
    }

    if (cursorRow < 0) {
      // Cursor not in rendered lines — try visual-line navigation first
      int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
      ITextDecoder dec = _state.Decoder;
      int bom = _state.BomLength;

      if (FindVisualLineContaining(_state.TextCursorOffset, maxCols, out VisualLine curVlFb)) {
        int col;
        if (_desiredColumn >= 0) {
          col = _desiredColumn;
        } else {
          int lineLen = (int)Math.Min(curVlFb.ByteLength, int.MaxValue);
          EnsureBuffer(lineLen);
          doc.Read(curVlFb.DocOffset, _readBuffer.AsSpan(0, lineLen));
          col = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, lineLen),
              _state.TextCursorOffset - curVlFb.DocOffset, dec);
          _desiredColumn = col;
        }

        if (direction < 0) {
          if (curVlFb.DocOffset <= bom) return;
          if (FindPreviousVisualLine(curVlFb.DocOffset, maxCols, out VisualLine prevVl))
            PlaceCursorOnVisualLine(prevVl, col);
        } else {
          long afterCur = curVlFb.DocOffset + curVlFb.ByteLength;
          if (afterCur < doc.Length && FindNextVisualLine(afterCur, maxCols, out VisualLine nextVl))
            PlaceCursorOnVisualLine(nextVl, col);
        }
        return;
      }

      // Last resort: hard-line navigation (only if visual-line lookup fails)
      int minChar = dec.MinCharBytes;
      if (direction < 0) {
        long lineStart = FindLineStart(_state.TextCursorOffset);
        if (lineStart <= bom) return;
        long lineEnd = FindLineEnd(_state.TextCursorOffset);
        int curLineLen = (int)Math.Min(lineEnd - lineStart + NewlineLengthAt(lineEnd), int.MaxValue);
        EnsureBuffer(curLineLen);
        doc.Read(lineStart, _readBuffer.AsSpan(0, curLineLen));
        int col;
        if (_desiredColumn >= 0) {
          col = _desiredColumn;
        } else {
          col = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, curLineLen), _state.TextCursorOffset - lineStart, dec);
          _desiredColumn = col;
        }

        long prevLineEnd = lineStart - minChar;
        long prevLineStart = FindLineStart(prevLineEnd);
        int prevLen = (int)Math.Min(prevLineEnd - prevLineStart + NewlineLengthAt(prevLineEnd), int.MaxValue);
        EnsureBuffer(prevLen);
        doc.Read(prevLineStart, _readBuffer.AsSpan(0, prevLen));
        int byteCol = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, prevLen), col, dec);
        _state.TextCursorOffset = Math.Min(prevLineStart + byteCol, prevLineEnd);
      } else {
        long lineEnd = FindLineEnd(_state.TextCursorOffset);
        if (lineEnd >= doc.Length) return;
        long lineStart = FindLineStart(_state.TextCursorOffset);
        long effectiveStart = lineStart < bom ? bom : lineStart;
        int curLineLen = (int)Math.Min(lineEnd - effectiveStart + NewlineLengthAt(lineEnd), int.MaxValue);
        EnsureBuffer(curLineLen);
        doc.Read(effectiveStart, _readBuffer.AsSpan(0, curLineLen));
        int col;
        if (_desiredColumn >= 0) {
          col = _desiredColumn;
        } else {
          col = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, curLineLen), _state.TextCursorOffset - effectiveStart, dec);
          _desiredColumn = col;
        }

        long nextLineStart = lineEnd + NewlineLengthAt(lineEnd);
        long nextLineEnd = FindLineEnd(nextLineStart);
        int nextLen = (int)Math.Min(nextLineEnd - nextLineStart + NewlineLengthAt(nextLineEnd), int.MaxValue);
        EnsureBuffer(nextLen);
        doc.Read(nextLineStart, _readBuffer.AsSpan(0, nextLen));
        int byteCol = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, nextLen), col, dec);
        _state.TextCursorOffset = Math.Min(nextLineStart + byteCol, nextLineEnd);
      }
      return;
    }

    // Compute the cursor's display column within the current visual line
    VisualLine curVl = _visualLines[cursorRow];
    ITextDecoder decoder = _state.Decoder;
    int displayCol;
    if (_desiredColumn >= 0) {
      displayCol = _desiredColumn;
    } else {
      int lineLen = (int)Math.Min(curVl.ByteLength, int.MaxValue);
      EnsureBuffer(lineLen);
      doc.Read(curVl.DocOffset, _readBuffer.AsSpan(0, lineLen));
      long byteInLine = _state.TextCursorOffset - curVl.DocOffset;
      displayCol = ByteOffsetToDisplayColumn(_readBuffer.AsSpan(0, lineLen), byteInLine, decoder);
      _desiredColumn = displayCol;
    }

    int targetRow = cursorRow + direction;

    if (targetRow < 0) {
      // Need to scroll up — move cursor to previous visual line
      int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
      long firstVlStart = _visualLines[0].DocOffset;
      if (firstVlStart <= _state.BomLength) return;
      if (FindPreviousVisualLine(firstVlStart, maxCols, out VisualLine prevVl))
        PlaceCursorOnVisualLine(prevVl, displayCol);
      return;
    }

    if (targetRow >= _lastRenderedLineCount) {
      // Need to scroll down — move cursor to next visual line after viewport
      int maxCols = _lastTextAreaCols > 0 ? _lastTextAreaCols : 80;
      VisualLine lastVl = _visualLines[_lastRenderedLineCount - 1];
      long afterLast = lastVl.DocOffset + lastVl.ByteLength;
      if (afterLast >= doc.Length) return;
      if (FindNextVisualLine(afterLast, maxCols, out VisualLine nextVl))
        PlaceCursorOnVisualLine(nextVl, displayCol);
      return;
    }

    // Move to the same display column on the target visual line
    VisualLine targetVl = _visualLines[targetRow];
    int tLen = (int)Math.Min(targetVl.ByteLength, int.MaxValue);
    EnsureBuffer(tLen);
    doc.Read(targetVl.DocOffset, _readBuffer.AsSpan(0, tLen));
    int targetByteCol = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, tLen), displayCol, decoder);
    _state.TextCursorOffset = Math.Clamp(targetVl.DocOffset + targetByteCol,
        targetVl.DocOffset, Math.Min(targetVl.DocOffset + targetVl.ByteLength, doc.Length));
  }

  private long FindLineStart(long offset)
  {
    Document? doc = _state.Document;
    if (doc is null || offset <= 0) return 0;

    int minChar = _state.Decoder.MinCharBytes;
    const int InitialChunk = 4096;
    const int MaxChunk = 16 * 1024 * 1024;

    long search = offset;
    int chunkSize = InitialChunk;

    while (search > 0) {
      int chunkLen = (int)Math.Min(chunkSize, search);
      long chunkStart = search - chunkLen;
      byte[] buf = new byte[chunkLen];
      doc.Read(chunkStart, buf);

      for (int i = chunkLen - minChar; i >= 0; i -= minChar) {
        if (IsLF(buf, i, minChar))
          return chunkStart + i + minChar;
      }

      search = chunkStart;
      if (chunkSize < MaxChunk) chunkSize = Math.Min(chunkSize * 2, MaxChunk);
    }

    return 0;
  }

  private long FindLineEnd(long offset)
  {
    Document? doc = _state.Document;
    if (doc is null) return offset;

    int minChar = _state.Decoder.MinCharBytes;
    const int InitialChunk = 8192;
    const int MaxChunk = 16 * 1024 * 1024;

    // crPrefix is the max bytes a CR can occupy before a LF (1 for UTF-8, 2 for UTF-16LE).
    // On subsequent chunks we back up by this amount so CRLF spanning a boundary is detected.
    int crPrefix = minChar;
    long search = offset;
    int chunkSize = InitialChunk;
    bool firstChunk = true;

    while (search < doc.Length) {
      // Overlap with previous chunk to catch CRLF at boundary
      long readStart = firstChunk ? search : Math.Max(offset, search - crPrefix);
      int overlap = (int)(search - readStart);
      int chunkLen = (int)Math.Min(chunkSize + overlap, doc.Length - readStart);
      if (chunkLen <= 0) break;

      byte[] buf = new byte[chunkLen];
      doc.Read(readStart, buf);

      // Start scanning past the overlap (which was already checked) unless it's the first chunk
      int scanStart = firstChunk ? 0 : overlap;
      // Align scan start to character boundary
      if (minChar > 1)
        scanStart = (scanStart + minChar - 1) / minChar * minChar;

      for (int i = scanStart; i + minChar <= chunkLen; i += minChar) {
        if (IsLF(buf, i, minChar)) {
          // For CRLF, return position of \r (start of newline sequence)
          if (minChar == 1 && i > 0 && buf[i - 1] == 0x0D)
            return readStart + i - 1;
          if (minChar == 2 && i >= 2 && buf[i - 2] == 0x0D && buf[i - 1] == 0x00)
            return readStart + i - 2;
          return readStart + i;
        }
      }

      search = readStart + chunkLen;
      firstChunk = false;
      if (chunkSize < MaxChunk) chunkSize = Math.Min(chunkSize * 2, MaxChunk);
    }

    return doc.Length;
  }

  /// <summary>Checks if position i in buf is a LF (0x0A) character, respecting encoding width.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static bool IsLF(byte[] buf, int i, int minChar)
  {
    if (minChar == 1)
      return buf[i] == 0x0A;
    // UTF-16LE: LF is 0x0A 0x00
    return i + 1 < buf.Length && buf[i] == 0x0A && buf[i + 1] == 0x00;
  }

  /// <summary>Returns the byte length of the newline sequence at the given file offset (2 for CRLF, else minChar). Returns 0 if the byte at offset is not a newline.</summary>
  private int NewlineLengthAt(long offset)
  {
    Document? doc = _state.Document;
    if (doc is null) return 0;

    int minChar = _state.Decoder.MinCharBytes;
    byte[] buf = new byte[minChar * 2];
    int read = doc.Read(offset, buf.AsSpan(0, (int)Math.Min(buf.Length, doc.Length - offset)));
    if (read < minChar) return 0;

    if (minChar == 1) {
      if (buf[0] == 0x0D && read >= 2 && buf[1] == 0x0A) return 2; // CRLF
      if (buf[0] == 0x0A) return 1; // LF
      if (buf[0] == 0x0D) return 1; // bare CR
      return 0; // not a newline
    }

    // UTF-16LE
    if (read >= 4 && buf[0] == 0x0D && buf[1] == 0x00 && buf[2] == 0x0A && buf[3] == 0x00) return 4; // CRLF
    if (read >= 2 && buf[0] == 0x0A && buf[1] == 0x00) return 2; // LF
    if (read >= 2 && buf[0] == 0x0D && buf[1] == 0x00) return 2; // bare CR
    return 0; // not a newline
  }

  private void EnsureCursorVisible(int textAreaCols)
  {
    if (_state.TextCursorOffset < 0) return;
    Document? doc = _state.Document;
    if (doc is null) return;

    int vpHeight = _state.VisibleRows;
    if (vpHeight <= 0) vpHeight = 24;

    // If cursor is before the viewport top, scroll up
    if (_state.TextCursorOffset < _state.TextTopOffset) {
      if (_state.WordWrap) {
        _state.TextTopOffset = ComputeViewportTopForCursor(
            _state.TextCursorOffset, textAreaCols, 0);
      } else {
        _state.TextTopOffset = FindLineStart(_state.TextCursorOffset);
      }
      return;
    }

    // Compute visual lines from top to check if cursor is visible
    int maxCols = _state.WordWrap ? textAreaCols : int.MaxValue;
    ITextDecoder decoder = _state.Decoder;

    int readSize = Math.Max((vpHeight + 4) * 256, 16384);
    readSize = (int)Math.Min(readSize, doc.Length - _state.TextTopOffset);
    if (readSize <= 0) return;

    int expandedRead = Math.Min(readSize * 4, 16 * 1024 * 1024);
    expandedRead = (int)Math.Min(expandedRead, doc.Length - _state.TextTopOffset);
    EnsureBuffer(expandedRead);
    int bytesRead = doc.Read(_state.TextTopOffset, _readBuffer.AsSpan(0, expandedRead));
    if (bytesRead == 0) return;

    EnsureVisualLines(vpHeight + 64);
    int lineCount = _wrapEngine.ComputeVisualLines(
        _readBuffer.AsSpan(0, bytesRead), _state.TextTopOffset, maxCols, _state.WordWrap, _visualLines, decoder);

    // Check if cursor falls within the first vpHeight visual lines
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
        _state.TextTopOffset = ComputeViewportTopForCursor(
            _state.TextCursorOffset, textAreaCols, vpHeight / 3);
      } else {
        long newTop = FindLineStart(_state.TextCursorOffset);
        int minChar = _state.Decoder.MinCharBytes;
        for (int i = 0; i < vpHeight / 2 && newTop > 0; i++) {
          long prev = FindLineStart(Math.Max(0, newTop - minChar));
          if (prev >= newTop) break;
          newTop = prev;
        }
        _state.TextTopOffset = newTop;
      }
    }

    // Horizontal auto-pan (only when word wrap is off)
    if (!_state.WordWrap) {
      int cursorCol = ComputeCursorColumn();
      if (cursorCol < _horizontalScrollOffset)
        _horizontalScrollOffset = Math.Max(0, cursorCol - 4);
      else if (cursorCol >= _horizontalScrollOffset + textAreaCols)
        _horizontalScrollOffset = cursorCol - textAreaCols + 4;
    }
  }

  /// <summary>Computes the display column of the cursor within its line (0-based).</summary>
  private int ComputeCursorColumn()
  {
    Document? doc = _state.Document;
    if (doc is null) return 0;

    long lineStart = FindLineStart(_state.TextCursorOffset);
    int lineLen = (int)Math.Min(_state.TextCursorOffset - lineStart, 8192);
    if (lineLen <= 0) return 0;

    Span<byte> buf = lineLen <= 1024 ? stackalloc byte[lineLen] : new byte[lineLen];
    int read = doc.Read(lineStart, buf);
    if (read == 0) return 0;

    ReadOnlySpan<byte> lineBytes = buf[..Math.Min(read, lineLen)];
    string text = DecodeLineToDisplay(lineBytes, _state.Decoder, _state.TabWidth, null);
    return text.Length;
  }


  private long ComputeLineNumber(long offset)
  {
    Document? doc = _state.Document;
    if (doc is null) return 1;

    // Use sparse line index when available
    LineIndex? idx = _state.LineIndex;
    if (idx is not null && idx.SparseEntryCount > 0) {
      long baseLine = idx.EstimateLineForOffset(offset, doc.Length);

      // Refine: count exact newlines from nearest sparse entry to offset
      int sparseFactor = idx.SparseFactor;
      int sparseIdx = (int)(baseLine / sparseFactor) - 1;
      if (sparseIdx >= idx.SparseEntryCount)
        sparseIdx = idx.SparseEntryCount - 1;

      long scanFrom;
      long lineNum;
      if (sparseIdx >= 0) {
        scanFrom = idx.GetSparseOffset(sparseIdx) + _state.Decoder.MinCharBytes;
        lineNum = (long)(sparseIdx + 1) * sparseFactor + 1;
      } else {
        scanFrom = 0;
        lineNum = 1;
      }

      if (offset > scanFrom) {
        long gap = offset - scanFrom;
        if (gap <= 4 * 1024 * 1024) {
          // Small enough to count exactly
          lineNum += CountNewlines(scanFrom, offset);
        }
        // else: stick with the sparse estimate
      }

      _cachedTopOffset = offset;
      _cachedTopLineNumber = lineNum;
      return lineNum;
    }

    // Fallback: incremental count from cached position
    long from, to;
    long lineNumber;
    if (offset >= _cachedTopOffset) {
      from = _cachedTopOffset;
      to = offset;
      lineNumber = _cachedTopLineNumber;
    } else {
      from = 0;
      to = offset;
      lineNumber = 1;
    }

    if (to - from > 10 * 1024 * 1024)
      lineNumber = Math.Max(1, (long)((double)offset / doc.Length * _state.EstimatedTotalLines));
    else
      lineNumber += CountNewlines(from, to);

    _cachedTopOffset = offset;
    _cachedTopLineNumber = lineNumber;
    return lineNumber;
  }

  private long CountNewlines(long from, long to)
  {
    Document? doc = _state.Document;
    if (doc is null) return 0;

    int minChar = _state.Decoder.MinCharBytes;
    long count = 0;
    long pos = from;
    byte[] buf = new byte[65536];

    while (pos < to) {
      int readLen = (int)Math.Min(buf.Length, to - pos);
      int read = doc.Read(pos, buf.AsSpan(0, readLen));
      if (read == 0) break;

      if (minChar == 2) {
        // UTF-16 LE: scan for 0x0A 0x00 at even byte offsets
        int alignedLen = read & ~1; // ensure we process whole code units
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
    Document? doc = _state.Document;
    if (doc is null || targetLine <= 1) return _state.BomLength;

    int minChar = _state.Decoder.MinCharBytes;
    long newlinesNeeded = targetLine - 1;

    // Try using the sparse line index for O(1) lookup + small scan
    LineIndex? idx = _state.LineIndex;
    long startOffset = 0;
    long newlinesCounted = 0;

    if (idx is not null && idx.SparseEntryCount > 0) {
      int sparseFactor = idx.SparseFactor;
      // Find largest sparse entry where (sparseIdx+1)*sparseFactor <= newlinesNeeded
      int sparseIdx = (int)(newlinesNeeded / sparseFactor) - 1;
      if (sparseIdx >= idx.SparseEntryCount)
        sparseIdx = idx.SparseEntryCount - 1;
      if (sparseIdx >= 0) {
        startOffset = idx.GetSparseOffset(sparseIdx) + minChar; // byte after the stored LF code unit
        newlinesCounted = (long)(sparseIdx + 1) * sparseFactor;
      }
    }

    long remaining = newlinesNeeded - newlinesCounted;
    if (remaining <= 0)
      return Math.Min(startOffset, doc.Length);

    // Linear scan from startOffset counting LF code units
    long pos = startOffset;
    byte[] buf = new byte[65536];
    long found = 0;

    while (pos < doc.Length && found < remaining) {
      int readLen = (int)Math.Min(buf.Length, doc.Length - pos);
      int read = doc.Read(pos, buf.AsSpan(0, readLen));
      if (read == 0) break;

      if (minChar == 2) {
        // UTF-16 LE: scan for 0x0A 0x00 at even byte offsets
        int alignedLen = read & ~1;
        for (int i = 0; i + 1 < alignedLen; i += 2) {
          if (buf[i] == 0x0A && buf[i + 1] == 0x00) {
            found++;
            if (found >= remaining)
              return pos + i + 2; // byte after the LF code unit (2 bytes)
          }
        }
      } else {
        for (int i = 0; i < read; i++) {
          if (buf[i] == 0x0A) {
            found++;
            if (found >= remaining)
              return pos + i + 1; // byte after the \n
          }
        }
      }
      pos += read;
    }
    return Math.Min(pos, doc.Length);
  }

  private static bool IsNewlineAt(ReadOnlySpan<byte> buf, int index, ITextDecoder decoder)
  {
    if (index < 0 || index >= buf.Length) return false;
    return decoder.IsNewline(buf, index, out _);
  }

  private static List<SearchResult> CollectVisibleMatches(List<SearchResult> results, long viewStart, long viewEnd)
  {
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

  // ─── Display-column ↔ byte-offset helpers ───

  /// <summary>
  /// Computes the 0-based display column for a byte offset within a visual line's raw bytes.
  /// Skips BOM (U+FEFF), expands tabs, counts wide CJK chars as 2 columns.
  /// <paramref name="byteOffsetInLine"/> is relative to the start of <paramref name="lineBytes"/>.
  /// </summary>
  private int ByteOffsetToDisplayColumn(ReadOnlySpan<byte> lineBytes, long byteOffsetInLine, ITextDecoder decoder)
  {
    int col = 0;
    int pos = 0;
    while (pos < lineBytes.Length && pos < byteOffsetInLine) {
      (Rune rune, int len) = decoder.DecodeRune(lineBytes, pos);
      if (len <= 0) { pos++; continue; }
      int cp = rune.Value;
      if (cp == '\n' || cp == '\r') break;
      if (cp == 0xFEFF) { pos += len; continue; } // BOM — zero display width
      col += Utf8Utils.RuneColumnWidth(rune, _state.TabWidth);
      pos += len;
    }
    return col;
  }

  /// <summary>
  /// Converts a 0-based display column to a byte offset within a visual line's raw bytes.
  /// Returns the byte offset of the character at the target column, or the end of displayable
  /// content if the line is shorter. <paramref name="lineBytes"/> starts at the visual line's DocOffset.
  /// </summary>
  private int DisplayColumnToByteOffset(ReadOnlySpan<byte> lineBytes, int targetColumn, ITextDecoder decoder)
  {
    int col = 0;
    int pos = 0;
    while (pos < lineBytes.Length) {
      (Rune rune, int len) = decoder.DecodeRune(lineBytes, pos);
      if (len <= 0) { pos++; continue; }
      int cp = rune.Value;
      if (cp == '\n' || cp == '\r') break;
      if (cp == 0xFEFF) { pos += len; continue; } // BOM
      if (col >= targetColumn) return pos;
      col += Utf8Utils.RuneColumnWidth(rune, _state.TabWidth);
      pos += len;
    }
    // Target column is beyond line content; return end of displayable content
    return pos;
  }

  // ─── Visual-line navigation helpers (word-wrap aware) ───

  /// <summary>
  /// Places the cursor at the same display column on the target visual line.
  /// </summary>
  private void PlaceCursorOnVisualLine(VisualLine targetVl, int displayCol)
  {
    Document? doc = _state.Document;
    if (doc is null) return;
    ITextDecoder decoder = _state.Decoder;
    int tLen = (int)Math.Min(targetVl.ByteLength, int.MaxValue);
    EnsureBuffer(tLen);
    doc.Read(targetVl.DocOffset, _readBuffer.AsSpan(0, tLen));
    int targetByteCol = DisplayColumnToByteOffset(_readBuffer.AsSpan(0, tLen), displayCol, decoder);
    _state.TextCursorOffset = Math.Clamp(targetVl.DocOffset + targetByteCol,
        targetVl.DocOffset, Math.Min(targetVl.DocOffset + targetVl.ByteLength, doc.Length));
  }

  /// <summary>
  /// Computes one visual line starting at the given document offset (must be a visual-line boundary).
  /// Returns false if at end of document.
  /// </summary>
  private bool FindNextVisualLine(long fromOffset, int maxCols, out VisualLine result)
  {
    result = default;
    Document? doc = _state.Document;
    if (doc is null || fromOffset >= doc.Length) return false;

    int readLen = (int)Math.Min(maxCols * 8 + 64, doc.Length - fromOffset);
    if (readLen <= 0) return false;

    EnsureBuffer(readLen);
    int bytesRead = doc.Read(fromOffset, _readBuffer.AsSpan(0, readLen));
    if (bytesRead == 0) return false;

    Span<VisualLine> output = stackalloc VisualLine[2];
    int count = _wrapEngine.ComputeVisualLines(
        _readBuffer.AsSpan(0, bytesRead), fromOffset, maxCols, true, output, _state.Decoder);
    if (count == 0) return false;

    result = output[0];
    return true;
  }

  /// <summary>
  /// Finds the visual line immediately before the one starting at <paramref name="vlStartOffset"/>.
  /// Scans forward from the hard-line start, returning the last visual line whose DocOffset is
  /// strictly less than <paramref name="vlStartOffset"/>.
  /// </summary>
  private bool FindPreviousVisualLine(long vlStartOffset, int maxCols, out VisualLine result)
  {
    result = default;
    Document? doc = _state.Document;
    if (doc is null || vlStartOffset <= _state.BomLength) return false;

    int bom = _state.BomLength;
    ITextDecoder decoder = _state.Decoder;

    long hardLineStart = FindLineStart(vlStartOffset);
    if (vlStartOffset == hardLineStart) {
      // At hard-line boundary — need previous hard line
      long prevPos = Math.Max(0, vlStartOffset - decoder.MinCharBytes);
      hardLineStart = FindLineStart(prevPos);
    }
    if (hardLineStart < bom) hardLineStart = bom;

    bool found = false;
    long scanPos = hardLineStart;

    while (scanPos < vlStartOffset) {
      int readLen = (int)Math.Min(
          Math.Max((long)maxCols * 256, vlStartOffset - scanPos + maxCols * 8),
          doc.Length - scanPos);
      if (readLen <= 0) break;

      EnsureBuffer(readLen);
      int bytesRead = doc.Read(scanPos, _readBuffer.AsSpan(0, readLen));
      if (bytesRead == 0) break;

      EnsureNavVisualLines(256);
      int count = _wrapEngine.ComputeVisualLines(
          _readBuffer.AsSpan(0, bytesRead), scanPos, maxCols, true, _navVisualLines, decoder);
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

  /// <summary>
  /// Finds the visual line containing the given document offset when word wrap is active.
  /// Scans from the hard-line start forward through visual lines.
  /// </summary>
  private bool FindVisualLineContaining(long offset, int maxCols, out VisualLine result)
  {
    result = default;
    Document? doc = _state.Document;
    if (doc is null || offset < 0 || offset > doc.Length) return false;

    int bom = _state.BomLength;
    ITextDecoder decoder = _state.Decoder;

    long hardLineStart = FindLineStart(offset);
    if (hardLineStart < bom) hardLineStart = bom;

    long scanPos = hardLineStart;

    while (scanPos <= offset && scanPos < doc.Length) {
      int readLen = (int)Math.Min(
          Math.Max((long)maxCols * 256, offset - scanPos + maxCols * 8),
          doc.Length - scanPos);
      if (readLen <= 0) break;

      EnsureBuffer(readLen);
      int bytesRead = doc.Read(scanPos, _readBuffer.AsSpan(0, readLen));
      if (bytesRead == 0) break;

      EnsureNavVisualLines(256);
      int count = _wrapEngine.ComputeVisualLines(
          _readBuffer.AsSpan(0, bytesRead), scanPos, maxCols, true, _navVisualLines, decoder);
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

  /// <summary>
  /// Computes a viewport top offset that places the cursor's visual line approximately
  /// <paramref name="contextBefore"/> visual lines from the top of the viewport.
  /// Falls back to the cursor's visual-line start if the context cannot be determined.
  /// </summary>
  private long ComputeViewportTopForCursor(long cursorOffset, int maxCols, int contextBefore)
  {
    Document? doc = _state.Document;
    if (doc is null) return cursorOffset;

    int bom = _state.BomLength;
    ITextDecoder decoder = _state.Decoder;

    long hardLineStart = FindLineStart(cursorOffset);
    if (hardLineStart < bom) hardLineStart = bom;

    long scanPos = hardLineStart;

    int bufSize = Math.Max(contextBefore + 1, 1);
    long[] recentStarts = new long[bufSize];
    int recentWritten = 0;

    while (scanPos <= cursorOffset && scanPos < doc.Length) {
      int readLen = (int)Math.Min(
          Math.Max((long)maxCols * 256, cursorOffset - scanPos + maxCols * 8),
          doc.Length - scanPos);
      if (readLen <= 0) break;

      EnsureBuffer(readLen);
      int bytesRead = doc.Read(scanPos, _readBuffer.AsSpan(0, readLen));
      if (bytesRead == 0) break;

      EnsureNavVisualLines(256);
      int count = _wrapEngine.ComputeVisualLines(
          _readBuffer.AsSpan(0, bytesRead), scanPos, maxCols, true, _navVisualLines, decoder);
      if (count == 0) break;

      for (int i = 0; i < count; i++) {
        long vlStart = _navVisualLines[i].DocOffset;
        long vlEnd = vlStart + _navVisualLines[i].ByteLength;

        recentStarts[recentWritten % bufSize] = vlStart;
        recentWritten++;

        if (cursorOffset >= vlStart && cursorOffset <= vlEnd) {
          int backLines = Math.Min(contextBefore, recentWritten - 1);
          int idx = ((recentWritten - 1 - backLines) % bufSize + bufSize) % bufSize;
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

  private void EnsureNavVisualLines(int count)
  {
    if (_navVisualLines.Length < count)
      _navVisualLines = new VisualLine[count];
  }

  private void OnStateChanged()
  {
    SetNeedsDraw();
    StateChanged?.Invoke();
  }

  private void UpdateScrollBar(int vpHeight, int textAreaCols)
  {
    Document? doc = _state.Document;
    if (doc is null) return;

    // Update estimated total lines from indexer when available
    LineIndex? lineIdx = _state.LineIndex;
    if (lineIdx is not null && lineIdx.TotalLineCount > 0)
      _state.EstimatedTotalLines = lineIdx.TotalLineCount;

    long totalLines = Math.Max(1, _state.EstimatedTotalLines);
    int scrollTotal = (int)Math.Min(totalLines, int.MaxValue - 1);
    double fraction = doc.Length > 0 ? (double)_state.TextTopOffset / doc.Length : 0;
    int scrollPos = (int)(fraction * scrollTotal);

    _updatingScrollBar = true;

    // Vertical
    _verticalScrollBar.ScrollableContentSize = scrollTotal;
    _verticalScrollBar.VisibleContentSize = vpHeight;
    _verticalScrollBar.Value = Math.Clamp(scrollPos, 0, Math.Max(0, scrollTotal - vpHeight));

    // Horizontal (only meaningful when word wrap is off)
    if (!_state.WordWrap) {
      int effectiveWidth = Math.Max(_maxLineWidthInViewport, _horizontalScrollOffset + textAreaCols);
      if (effectiveWidth > textAreaCols) {
        _horizontalScrollBar.ScrollableContentSize = effectiveWidth;
        _horizontalScrollBar.VisibleContentSize = textAreaCols;
        _horizontalScrollBar.Value = Math.Clamp(_horizontalScrollOffset, 0,
            Math.Max(0, effectiveWidth - textAreaCols));
      } else {
        _horizontalScrollBar.ScrollableContentSize = textAreaCols;
        _horizontalScrollBar.VisibleContentSize = textAreaCols;
        _horizontalScrollBar.Value = 0;
      }
    } else {
      _horizontalScrollBar.ScrollableContentSize = textAreaCols;
      _horizontalScrollBar.VisibleContentSize = textAreaCols;
      _horizontalScrollBar.Value = 0;
    }

    _updatingScrollBar = false;
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
