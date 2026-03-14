using Leviathan.Core;
using Leviathan.Core.Csv;

using System.Text;

using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Leviathan.TUI2.Views;

/// <summary>
/// Tabular CSV grid view. Displays CSV records in aligned columns with a
/// sticky header row, cell-level cursor, and row selection for copy/delete.
/// Handles large files via <see cref="CsvRowIndex"/> sparse indexing.
/// </summary>
/// <remarks>
/// Cell values are decoded as UTF-8. The underlying CSV pipeline (dialect detection,
/// row indexing, field parsing) operates on single-byte tokens, so this view does
/// not support multi-byte encodings such as UTF-16.
/// </remarks>
internal sealed class LeviathanCsvView : View
{
  private readonly AppState _state;
  private byte[] _readBuffer = new byte[64 * 1024];
  private byte[] _scanBuffer = new byte[64 * 1024];

  private const int MinGutterWidth = 8;  // minimum gutter width
  private const int MinColumnWidth = 4;
  private const int MaxColumnWidth = 40;
  private const int ColumnPadding = 1;  // space between columns
  private int _gutterWidth = MinGutterWidth;

  /// <summary>
  /// Fired when the view needs the status bar to update.
  /// </summary>
  internal event Action? StateChanged;

  internal LeviathanCsvView(AppState state)
  {
    _state = state;
    CanFocus = true;
    SetupCommands();
    SetupKeyBindings();
    SetupMouseBindings();
  }

  // ─── Commands ───

  private void SetupCommands()
  {
    AddCommand(Command.Up, () => { MoveCursorRow(-1, false); return true; });
    AddCommand(Command.Down, () => { MoveCursorRow(1, false); return true; });
    AddCommand(Command.Left, () => { MoveCursorCol(-1); return true; });
    AddCommand(Command.Right, () => { MoveCursorCol(1); return true; });
    AddCommand(Command.PageUp, () => { MoveCursorRow(-VisibleDataRows(), false); return true; });
    AddCommand(Command.PageDown, () => { MoveCursorRow(VisibleDataRows(), false); return true; });
    AddCommand(Command.LeftStart, () => { _state.CsvCursorCol = 0; _state.CsvHorizontalScroll = 0; StateChanged?.Invoke(); SetNeedsDraw(); return true; });
    AddCommand(Command.RightEnd, () => { _state.CsvCursorCol = Math.Max(0, _state.CsvColumnCount - 1); EnsureColumnVisible(); StateChanged?.Invoke(); SetNeedsDraw(); return true; });
    AddCommand(Command.Start, () => { _state.CsvCursorRow = 0; _state.CsvTopRowIndex = 0; StateChanged?.Invoke(); SetNeedsDraw(); return true; });
    AddCommand(Command.End, () => { GoToLastRow(); return true; });
    AddCommand(Command.ScrollUp, () => { ScrollViewport(-3); return true; });
    AddCommand(Command.ScrollDown, () => { ScrollViewport(3); return true; });
  }

  private void SetupKeyBindings()
  {
    KeyBindings.Remove(Key.Space);
    KeyBindings.Remove(Key.Enter);

    KeyBindings.Add(Key.CursorUp, Command.Up);
    KeyBindings.Add(Key.CursorDown, Command.Down);
    KeyBindings.Add(Key.CursorLeft, Command.Left);
    KeyBindings.Add(Key.CursorRight, Command.Right);
    KeyBindings.Add(Key.PageUp, Command.PageUp);
    KeyBindings.Add(Key.PageDown, Command.PageDown);
    KeyBindings.Add(Key.Home, Command.LeftStart);
    KeyBindings.Add(Key.End, Command.RightEnd);
    KeyBindings.Add(Key.Home.WithCtrl, Command.Start);
    KeyBindings.Add(Key.End.WithCtrl, Command.End);

    // Shift+nav for selection
    KeyBindings.Add(Key.CursorUp.WithShift, [Command.Up]);
    KeyBindings.Add(Key.CursorDown.WithShift, [Command.Down]);
  }

  private void SetupMouseBindings()
  {
    MouseBindings.ReplaceCommands(MouseFlags.LeftButtonClicked, Command.Activate);
    MouseBindings.Add(MouseFlags.WheeledUp, Command.ScrollUp);
    MouseBindings.Add(MouseFlags.WheeledDown, Command.ScrollDown);
  }

  /// <summary>Handles shift+arrow and Tab for selection and column navigation.</summary>
  protected override bool OnKeyDownNotHandled(Key keyEvent)
  {
    if (keyEvent == Key.CursorUp.WithShift) { MoveCursorRow(-1, true); return true; }
    if (keyEvent == Key.CursorDown.WithShift) { MoveCursorRow(1, true); return true; }
    if (keyEvent == Key.Tab) { MoveCursorCol(1); return true; }
    if (keyEvent == Key.Tab.WithShift) { MoveCursorCol(-1); return true; }
    return false;
  }

  // ─── Navigation ───

  /// <summary>Recomputes gutter width based on the current total row count.</summary>
  private void UpdateGutterWidth()
  {
    long total = GetTotalDataRows();
    int digits = total > 0 ? (int)Math.Floor(Math.Log10(total)) + 1 : 1;
    _gutterWidth = Math.Max(MinGutterWidth, digits + 2);
  }

  private int VisibleDataRows()
  {
    int headerRows = _state.CsvDialect.HasHeader ? 1 : 0;
    return Math.Max(1, Viewport.Height - headerRows);
  }

  private void MoveCursorRow(long delta, bool extend)
  {
    if (_state.Document is null) return;

    long totalRows = GetTotalDataRows();
    long newRow = Math.Clamp(_state.CsvCursorRow + delta, 0, Math.Max(0, totalRows - 1));

    if (extend)
    {
      if (_state.CsvSelectionAnchorRow < 0)
        _state.CsvSelectionAnchorRow = _state.CsvCursorRow;
    }
    else
    {
      _state.CsvSelectionAnchorRow = -1;
    }

    _state.CsvCursorRow = newRow;

    // Scroll to keep cursor visible
    int visibleRows = VisibleDataRows();
    if (newRow < _state.CsvTopRowIndex)
      _state.CsvTopRowIndex = newRow;
    else if (newRow >= _state.CsvTopRowIndex + visibleRows)
      _state.CsvTopRowIndex = newRow - visibleRows + 1;

    StateChanged?.Invoke();
    SetNeedsDraw();
  }

  private void MoveCursorCol(int delta)
  {
    int newCol = _state.CsvCursorCol + delta;
    newCol = Math.Clamp(newCol, 0, Math.Max(0, _state.CsvColumnCount - 1));
    _state.CsvCursorCol = newCol;
    EnsureColumnVisible();
    StateChanged?.Invoke();
    SetNeedsDraw();
  }

  private void ScrollViewport(int delta)
  {
    if (_state.Document is null) return;
    long totalRows = GetTotalDataRows();
    if (totalRows <= 0) return;

    int visibleRows = VisibleDataRows();
    long maxTop = Math.Max(0, totalRows - visibleRows);
    long newTop = Math.Clamp(_state.CsvTopRowIndex + delta, 0, maxTop);
    if (newTop == _state.CsvTopRowIndex) return;

    _state.CsvTopRowIndex = newTop;
    StateChanged?.Invoke();
    SetNeedsDraw();
  }

  private void EnsureColumnVisible()
  {
    if (_state.CsvColumnWidths is null) return;

    int col = _state.CsvCursorCol;
    if (col < _state.CsvHorizontalScroll)
    {
      _state.CsvHorizontalScroll = col;
    }
    else
    {
      // Calculate if column is visible
      int available = Viewport.Width - _gutterWidth;
      int usedWidth = 0;
      for (int c = _state.CsvHorizontalScroll; c <= col && c < _state.CsvColumnWidths.Length; c++)
      {
        usedWidth += _state.CsvColumnWidths[c] + ColumnPadding;
      }

      while (usedWidth > available && _state.CsvHorizontalScroll < col)
      {
        usedWidth -= _state.CsvColumnWidths[_state.CsvHorizontalScroll] + ColumnPadding;
        _state.CsvHorizontalScroll++;
      }
    }
  }

  // ─── Mouse ───

  /// <inheritdoc/>
  protected override bool OnActivating(CommandEventArgs args)
  {
    if (args.Context?.Binding is not MouseBinding { MouseEvent: { } mouse })
      return base.OnActivating(args);

    if (!HasFocus)
      SetFocus();

    if (_state.Document is null || _state.CsvColumnWidths is null)
      return true;

    int mx = mouse.Position!.Value.X;
    int my = mouse.Position!.Value.Y;

    int headerRows = _state.CsvDialect.HasHeader ? 1 : 0;

    // Ignore clicks on the header row
    if (my < headerRows) return true;

    // Convert screen row to data row
    long dataRow = _state.CsvTopRowIndex + (my - headerRows);
    long totalRows = GetTotalDataRows();
    if (dataRow < 0 || dataRow >= totalRows) return true;

    // Convert screen X to column index
    int col = ScreenXToColumn(mx);

    _state.CsvSelectionAnchorRow = -1;
    _state.CsvCursorRow = dataRow;
    if (col >= 0)
      _state.CsvCursorCol = col;

    StateChanged?.Invoke();
    SetNeedsDraw();
    return true;
  }

  /// <summary>
  /// Maps a viewport X coordinate to the corresponding column index,
  /// accounting for gutter width, horizontal scroll, and column widths.
  /// Returns -1 if the coordinate falls outside any data column (e.g. in the gutter or separator).
  /// </summary>
  private int ScreenXToColumn(int screenX)
  {
    if (_state.CsvColumnWidths is null) return -1;

    int x = _gutterWidth;
    int[] widths = _state.CsvColumnWidths;

    for (int col = _state.CsvHorizontalScroll; col < widths.Length; col++)
    {
      int colEnd = x + widths[col];
      if (screenX >= x && screenX < colEnd)
        return col;
      x = colEnd + ColumnPadding;
    }

    return -1;
  }

  private void GoToLastRow()
  {
    long total = GetTotalDataRows();
    if (total <= 0) return;

    _state.CsvCursorRow = total - 1;
    int visibleRows = VisibleDataRows();
    _state.CsvTopRowIndex = Math.Max(0, total - visibleRows);
    StateChanged?.Invoke();
    SetNeedsDraw();
  }

  /// <summary>
  /// Scrolls the CSV view to show the row containing the given byte offset.
  /// Used by search navigation.
  /// </summary>
  internal void GotoOffset(long offset)
  {
    if (_state.Document is null) return;

    long totalRows = GetTotalDataRows();
    if (totalRows <= 0) return;

    // Binary search: find the last data row whose start offset <= target offset
    long lo = 0, hi = totalRows - 1;
    while (lo < hi)
    {
      long mid = lo + (hi - lo + 1) / 2;
      long midOffset = GetRowByteOffset(mid);
      if (midOffset <= offset)
        lo = mid;
      else
        hi = mid - 1;
    }

    _state.CsvCursorRow = lo;

    // Ensure the row is visible
    int visibleRows = VisibleDataRows();
    if (lo < _state.CsvTopRowIndex || lo >= _state.CsvTopRowIndex + visibleRows)
    {
      _state.CsvTopRowIndex = Math.Max(0, lo - visibleRows / 3);
    }

    StateChanged?.Invoke();
    SetNeedsDraw();
  }

  private long GetTotalDataRows()
  {
    CsvRowIndex? index = _state.CsvRowIndex;
    if (index is null) return 0;

    long total = index.TotalRowCount;
    if (_state.CsvDialect.HasHeader && total > 0)
      total--;
    return total;
  }

  // ─── Copy / Delete ───

  /// <summary>Copies the selected row(s) as CSV text to the clipboard.</summary>
  internal string CopySelection()
  {
    if (_state.Document is null) return "";

    long selStart = _state.CsvCursorRow;
    long selEnd = _state.CsvCursorRow;
    if (_state.CsvSelectionAnchorRow >= 0)
    {
      selStart = Math.Min(_state.CsvCursorRow, _state.CsvSelectionAnchorRow);
      selEnd = Math.Max(_state.CsvCursorRow, _state.CsvSelectionAnchorRow);
    }

    StringBuilder sb = new();
    for (long row = selStart; row <= selEnd; row++)
    {
      string rowText = ReadRowAsText(row);
      sb.AppendLine(rowText);
    }
    return sb.ToString();
  }

  /// <summary>Copies the value of the current cell to the clipboard.</summary>
  internal string CopyCellValue()
  {
    return ReadCellValue(_state.CsvCursorRow, _state.CsvCursorCol);
  }

  /// <summary>Deletes the selected row(s) from the document.</summary>
  internal void DeleteSelectedRows()
  {
    if (_state.Document is null) return;

    long selStart = _state.CsvCursorRow;
    long selEnd = _state.CsvCursorRow;
    if (_state.CsvSelectionAnchorRow >= 0)
    {
      selStart = Math.Min(_state.CsvCursorRow, _state.CsvSelectionAnchorRow);
      selEnd = Math.Max(_state.CsvCursorRow, _state.CsvSelectionAnchorRow);
    }

    // Find byte offsets for the rows to delete
    long startOffset = GetRowByteOffset(selStart);
    long endOffset = GetRowByteOffset(selEnd + 1); // start of row after last selected
    if (endOffset < 0) endOffset = _state.Document.Length;

    if (startOffset >= 0 && endOffset > startOffset)
    {
      _state.Document.Delete(startOffset, endOffset - startOffset);
      _state.CsvSelectionAnchorRow = -1;

      // Re-index after delete
      _state.InitCsvView();
      _state.CsvCursorRow = Math.Min(selStart, Math.Max(0, GetTotalDataRows() - 1));
      StateChanged?.Invoke();
      SetNeedsDraw();
    }
  }

  // ─── Drawing ───

  protected override bool OnDrawingContent(DrawContext? context)
  {
    if (_state.Document is null || _state.CsvColumnWidths is null || _state.CsvColumnWidths.Length == 0)
    {
      DrawWelcome();
      return true;
    }

    UpdateGutterWidth();

    Attribute normalAttr = new(Color.White, Color.Black);
    Attribute gutterAttr = new(new Color(100, 130, 170), Color.Black);
    Attribute headerAttr = new(Color.BrightCyan, new Color(30, 30, 60));
    Attribute cursorAttr = new(Color.White, new Color(180, 100, 0));
    Attribute selectionAttr = new(Color.White, new Color(50, 80, 160));
    Attribute separatorAttr = new(new Color(80, 80, 80), Color.Black);

    int vpWidth = Viewport.Width;
    int vpHeight = Viewport.Height;
    int headerRows = _state.CsvDialect.HasHeader ? 1 : 0;
    int dataAreaStart = headerRows;

    // Draw header row
    if (_state.CsvDialect.HasHeader && vpHeight > 0)
    {
      DrawHeaderRow(headerAttr, separatorAttr, vpWidth);
    }

    // Draw data rows
    long totalDataRows = GetTotalDataRows();
    long topRow = _state.CsvTopRowIndex;

    long selRowStart = -1, selRowEnd = -1;
    if (_state.CsvSelectionAnchorRow >= 0)
    {
      selRowStart = Math.Min(_state.CsvCursorRow, _state.CsvSelectionAnchorRow);
      selRowEnd = Math.Max(_state.CsvCursorRow, _state.CsvSelectionAnchorRow);
    }

    for (int screenRow = dataAreaStart; screenRow < vpHeight; screenRow++)
    {
      long dataRow = topRow + (screenRow - dataAreaStart);
      if (dataRow >= totalDataRows)
      {
        // Clear remaining rows
        ClearRow(screenRow, normalAttr, vpWidth);
        continue;
      }

      bool isCursorRow = dataRow == _state.CsvCursorRow;
      bool isSelected = selRowStart >= 0 && dataRow >= selRowStart && dataRow <= selRowEnd;

      // Gutter: row number
      string rowNum = (dataRow + 1).ToString();
      string gutter = rowNum.PadLeft(_gutterWidth - 1) + " ";
      DrawText(0, screenRow, gutter, gutterAttr);

      // Parse and draw fields
      DrawDataRow(screenRow, dataRow, isCursorRow, isSelected,
          normalAttr, cursorAttr, selectionAttr, separatorAttr, vpWidth);
    }

    return true;
  }

  private void DrawHeaderRow(Attribute headerAttr, Attribute separatorAttr, int vpWidth)
  {
    // Clear row
    SetAttribute(headerAttr);
    Move(0, 0);
    for (int x = 0; x < vpWidth; x++)
      AddRune(' ');

    // Gutter
    DrawText(0, 0, new string(' ', _gutterWidth), headerAttr);

    int x_pos = _gutterWidth;
    int[] widths = _state.CsvColumnWidths!;
    string[] headers = _state.CsvHeaderNames;

    for (int col = _state.CsvHorizontalScroll; col < widths.Length && x_pos < vpWidth; col++)
    {
      int w = widths[col];
      string name = col < headers.Length ? headers[col] : $"Col{col + 1}";
      if (name.Length > w)
        name = name[..(w - 1)] + "\u2026"; // ellipsis

      DrawText(x_pos, 0, name.PadRight(w), headerAttr);
      x_pos += w;

      // Column separator
      if (x_pos < vpWidth)
      {
        DrawText(x_pos, 0, "\u2502", separatorAttr); // │
        x_pos += ColumnPadding;
      }
    }
  }

  private void DrawDataRow(int screenRow, long dataRow, bool isCursorRow,
      bool isSelected, Attribute normalAttr, Attribute cursorAttr,
      Attribute selectionAttr, Attribute separatorAttr, int vpWidth)
  {
    int[] widths = _state.CsvColumnWidths!;

    // Read row bytes
    ReadOnlySpan<byte> rowBytes = ReadRowBytes(dataRow);
    Span<CsvField> fields = stackalloc CsvField[Math.Min(widths.Length, 256)];
    int fieldCount = 0;

    if (rowBytes.Length > 0)
      fieldCount = CsvFieldParser.ParseRecord(rowBytes, _state.CsvDialect, fields);

    int x_pos = _gutterWidth;
    Span<byte> unescaped = stackalloc byte[1024];

    for (int col = _state.CsvHorizontalScroll; col < widths.Length && x_pos < vpWidth; col++)
    {
      int w = widths[col];
      bool isCursorCell = isCursorRow && col == _state.CsvCursorCol;

      Attribute cellAttr = isCursorCell ? cursorAttr
          : isSelected ? selectionAttr
          : normalAttr;

      string displayText;
      if (col < fieldCount)
      {
        int written = CsvFieldParser.UnescapeField(rowBytes, fields[col], _state.CsvDialect, unescaped);
        displayText = FormatCellPreview(unescaped[..written], w);
      }
      else
      {
        displayText = new string(' ', w);
      }

      DrawText(x_pos, screenRow, displayText.PadRight(w), cellAttr);
      x_pos += w;

      // Column separator
      if (x_pos < vpWidth)
      {
        DrawText(x_pos, screenRow, "\u2502", separatorAttr);
        x_pos += ColumnPadding;
      }
    }

    // Clear remainder
    if (x_pos < vpWidth)
    {
      Attribute attr = isSelected ? selectionAttr : normalAttr;
      SetAttribute(attr);
      Move(x_pos, screenRow);
      for (int x = x_pos; x < vpWidth; x++)
        AddRune(' ');
    }
  }

  /// <summary>
  /// Formats a cell value for display: replaces newlines with ⏎,
  /// truncates to width with ellipsis.
  /// </summary>
  private static string FormatCellPreview(ReadOnlySpan<byte> value, int maxWidth)
  {
    if (value.IsEmpty)
      return "";

    // Build a preview string, replacing newlines with ⏎
    StringBuilder sb = new(Math.Min(value.Length, maxWidth + 1));
    for (int i = 0; i < value.Length && sb.Length < maxWidth + 1; i++)
    {
      byte b = value[i];
      if (b == (byte)'\n')
      {
        sb.Append('\u23CE'); // ⏎
      }
      else if (b == (byte)'\r')
      {
        if (i + 1 < value.Length && value[i + 1] == (byte)'\n')
          i++; // skip \n in \r\n
        sb.Append('\u23CE');
      }
      else if (b < 0x20)
      {
        sb.Append('\u00B7'); // · for control chars
      }
      else
      {
        sb.Append((char)b);
      }
    }

    string text = sb.ToString();
    if (text.Length > maxWidth)
      text = text[..(maxWidth - 1)] + "\u2026"; // …

    return text;
  }

  private void DrawWelcome()
  {
    SetAttributeForRole(VisualRole.Normal);
    string msg = "No CSV data to display";
    int x = Math.Max(0, (Viewport.Width - msg.Length) / 2);
    int y = Viewport.Height / 2;
    Move(x, y);
    AddStr(msg);
  }

  private void ClearRow(int row, Attribute attr, int width)
  {
    SetAttribute(attr);
    Move(0, row);
    for (int x = 0; x < width; x++)
      AddRune(' ');
  }

  private void DrawText(int x, int y, string text, Attribute attr)
  {
    SetAttribute(attr);
    Move(x, y);
    for (int i = 0; i < text.Length && x + i < Viewport.Width; i++)
      AddRune(text[i]);
  }

  // ─── Data reading helpers ───

  private ReadOnlySpan<byte> ReadRowBytes(long dataRowIndex)
  {
    if (_state.Document is null) return ReadOnlySpan<byte>.Empty;

    long rowOffset = GetRowByteOffset(dataRowIndex);
    if (rowOffset < 0) return ReadOnlySpan<byte>.Empty;

    long nextRowOffset = GetRowByteOffset(dataRowIndex + 1);
    if (nextRowOffset < 0) nextRowOffset = _state.Document.Length;

    int rowLen = (int)Math.Min(nextRowOffset - rowOffset, _readBuffer.Length);
    if (rowLen <= 0) return ReadOnlySpan<byte>.Empty;

    EnsureBuffer(rowLen);
    int read = _state.Document.Read(rowOffset, _readBuffer.AsSpan(0, rowLen));

    // Trim trailing newline
    while (read > 0 && (_readBuffer[read - 1] == (byte)'\n' || _readBuffer[read - 1] == (byte)'\r'))
      read--;

    return _readBuffer.AsSpan(0, read);
  }

  /// <summary>
  /// Gets the byte offset of data row <paramref name="dataRowIndex"/>.
  /// Data row 0 is the first row after the header (if header is present).
  /// </summary>
  private long GetRowByteOffset(long dataRowIndex)
  {
    if (_state.Document is null) return -1;

    // Actual row index including header
    long actualRow = _state.CsvDialect.HasHeader ? dataRowIndex + 1 : dataRowIndex;

    if (actualRow == 0) return 0;

    CsvRowIndex? index = _state.CsvRowIndex;
    if (index is null) return -1;

    if (actualRow > index.TotalRowCount) return -1;

    // The index records the byte offset of the start of the NEXT row
    // after each newline. Row N starts at the offset recorded at row N-1.

    // Use sparse index to get close, clamping to last available entry
    int sparseIdx = (int)((actualRow - 1) / index.SparseFactor);
    int effectiveSparseIdx = Math.Min(sparseIdx, index.SparseEntryCount);
    long offset;
    long rowsScanned;

    if (effectiveSparseIdx > 0)
    {
      offset = index.GetSparseOffset(effectiveSparseIdx - 1);
      rowsScanned = (long)effectiveSparseIdx * index.SparseFactor;
    }
    else if (actualRow == 1 && index.FirstDataRowOffset > 0)
    {
      return index.FirstDataRowOffset;
    }
    else
    {
      offset = 0;
      rowsScanned = 0;
    }

    // Linear scan from the sparse entry to the target row
    long remaining = actualRow - rowsScanned;
    if (remaining <= 0) return offset;

    // Read and scan forward using the reusable scan buffer
    int scanBufSize = _scanBuffer.Length;
    bool inQuoted = false;
    byte quote = _state.CsvDialect.Quote;

    while (remaining > 0 && offset < _state.Document.Length)
    {
      int toRead = (int)Math.Min(scanBufSize, _state.Document.Length - offset);
      int read = _state.Document.Read(offset, _scanBuffer.AsSpan(0, toRead));
      if (read == 0) break;

      for (int i = 0; i < read && remaining > 0; i++)
      {
        byte b = _scanBuffer[i];

        if (inQuoted)
        {
          if (b == quote)
          {
            if (i + 1 < read && _scanBuffer[i + 1] == quote)
            {
              i++;
              continue;
            }
            inQuoted = false;
          }
          continue;
        }

        if (b == quote && quote != 0)
        {
          inQuoted = true;
          continue;
        }

        if (b == (byte)'\n')
        {
          remaining--;
          if (remaining == 0)
            return offset + i + 1;
        }
        else if (b == (byte)'\r')
        {
          if (i + 1 < read && _scanBuffer[i + 1] == (byte)'\n')
            i++;
          remaining--;
          if (remaining == 0)
            return offset + i + 1;
        }
      }

      offset += read;
    }

    return offset;
  }

  private string ReadRowAsText(long dataRowIndex)
  {
    ReadOnlySpan<byte> rowBytes = ReadRowBytes(dataRowIndex);
    return Encoding.UTF8.GetString(rowBytes);
  }

  private string ReadCellValue(long dataRowIndex, int colIndex)
  {
    ReadOnlySpan<byte> rowBytes = ReadRowBytes(dataRowIndex);
    if (rowBytes.IsEmpty) return "";

    Span<CsvField> fields = stackalloc CsvField[256];
    int count = CsvFieldParser.ParseRecord(rowBytes, _state.CsvDialect, fields);
    if (colIndex >= count) return "";

    Span<byte> unescaped = stackalloc byte[4096];
    int written = CsvFieldParser.UnescapeField(rowBytes, fields[colIndex], _state.CsvDialect, unescaped);
    return Encoding.UTF8.GetString(unescaped[..written]);
  }

  /// <summary>
  /// Reads the full record for a given data row, returning field name/value pairs.
  /// Used by the detail dialog.
  /// </summary>
  internal (string Name, string Value)[] ReadRecordDetails(long dataRowIndex)
  {
    ReadOnlySpan<byte> rowBytes = ReadRowBytes(dataRowIndex);
    if (rowBytes.IsEmpty) return [];

    Span<CsvField> fields = stackalloc CsvField[256];
    int count = CsvFieldParser.ParseRecord(rowBytes, _state.CsvDialect, fields);

    (string Name, string Value)[] details = new (string, string)[count];
    Span<byte> unescaped = stackalloc byte[4096];

    for (int i = 0; i < count; i++)
    {
      string name = i < _state.CsvHeaderNames.Length ? _state.CsvHeaderNames[i] : $"Column {i + 1}";
      int written = CsvFieldParser.UnescapeField(rowBytes, fields[i], _state.CsvDialect, unescaped);
      details[i] = (name, Encoding.UTF8.GetString(unescaped[..written]));
    }

    return details;
  }

  private void EnsureBuffer(int needed)
  {
    if (_readBuffer.Length < needed)
      _readBuffer = new byte[needed];
  }
}
