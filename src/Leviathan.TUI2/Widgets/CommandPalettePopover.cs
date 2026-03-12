using System.Collections.ObjectModel;

using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

using Leviathan.Core;
using Leviathan.Core.Text;

namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Non-modal command palette popover. TextField for fuzzy filtering + ListView of commands.
/// Supports goto mode when text starts with ":" (Ctrl+G inserts ":" automatically).
/// </summary>
internal sealed class CommandPalettePopover : PopoverImpl
{
  private readonly CommandPalette _palette;
  private readonly AppState _state;
  private readonly TextField _queryField;
  private readonly ListView _listView;
  private readonly Label _hintLabel;
  private readonly Action<long> _gotoLine;
  private readonly Action<long> _gotoOffset;
  private bool _isGotoMode;

  internal CommandPalettePopover(CommandPalette palette, AppState state, Action<long> gotoLine, Action<long> gotoOffset)
  {
    _palette = palette;
    _state = state;
    _gotoLine = gotoLine;
    _gotoOffset = gotoOffset;

    FrameView panel = new() {
      Title = "Command Palette",
      X = Pos.Center(),
      Y = 2,
      Width = Dim.Percent(60),
      Height = Dim.Percent(50),
    };

    _queryField = new TextField() {
      X = 0,
      Y = 0,
      Width = Dim.Fill(),
      Text = "",
    };

    _hintLabel = new Label() {
      X = 0,
      Y = 1,
      Width = Dim.Fill(),
      Height = 1,
      Text = "",
      Visible = false,
    };

    _listView = new ListView() {
      X = 0,
      Y = Pos.Bottom(_hintLabel),
      Width = Dim.Fill(),
      Height = Dim.Fill(),
    };

    // Update filtering as user types
    _queryField.TextChanged += (_, _) => {
      string newQuery = _queryField.Text?.ToString() ?? "";
      bool gotoMode = newQuery.StartsWith(":");

      if (gotoMode != _isGotoMode) {
        _isGotoMode = gotoMode;
        _hintLabel.Visible = gotoMode;
        _listView.Visible = !gotoMode;
      }

      if (gotoMode) {
        HandleGotoInput(newQuery);
      } else {
        if (newQuery != _palette.Query) {
          _palette.Query = newQuery;
          UpdateList();
        }
      }
    };

    panel.Add(_queryField, _hintLabel, _listView);
    Add(panel);
  }

  /// <summary>Shows the command palette and focuses the query field.</summary>
  internal void ShowPalette()
  {
    _palette.Reset();
    _queryField.Text = "";
    _isGotoMode = false;
    _hintLabel.Visible = false;
    _listView.Visible = true;
    UpdateList();
    App?.Popovers?.Show(this);
    _queryField.SetFocus();
  }

  /// <summary>Opens the palette in goto mode with ":" pre-inserted.</summary>
  internal void ShowGoto()
  {
    _palette.Reset();
    _isGotoMode = true;
    _hintLabel.Visible = true;
    _listView.Visible = false;

    // Save origin for Esc revert
    _state.GotoPreviewOrigin = _state.ActiveView == ViewMode.Hex
        ? _state.HexCursorOffset
        : _state.TextCursorOffset;
    _state.GotoPreviewTopOrigin = _state.ActiveView == ViewMode.Hex
        ? _state.HexBaseOffset
        : _state.TextTopOffset;

    long totalLines = Math.Max(1, _state.EstimatedTotalLines);
    _hintLabel.Text = _state.ActiveView == ViewMode.Hex
        ? "Enter hex offset (e.g. :0x1A3F)"
        : $"Enter line number (1 to ~{totalLines})";

    _queryField.Text = ":";
    App?.Popovers?.Show(this);
    _queryField.SetFocus();
    _queryField.MoveEnd();
  }

  /// <inheritdoc/>
  protected override bool OnKeyDown(Key key)
  {
    if (!Visible)
      return base.OnKeyDown(key);

    // Ctrl+P when visible -> hide
    if (key == Key.P.WithCtrl) {
      CancelAndHide();
      key.Handled = true;
      return true;
    }

    // Ctrl+G when visible -> hide
    if (key == Key.G.WithCtrl) {
      CancelAndHide();
      key.Handled = true;
      return true;
    }

    // Escape -> revert goto preview and hide
    if (key == Key.Esc) {
      CancelAndHide();
      key.Handled = true;
      return true;
    }

    if (_isGotoMode) {
      // Enter -> confirm goto and dismiss
      if (key == Key.Enter) {
        _state.GotoPreviewOrigin = -1; // confirm: don't revert
        Visible = false;
        key.Handled = true;
        return true;
      }
    } else {
      // Arrow down -> navigate list
      if (key == Key.CursorDown) {
        _palette.MoveDown();
        if (_palette.SelectedIndex >= 0) {
          _listView.SelectedItem = _palette.SelectedIndex;
        } else {
          _listView.SelectedItem = null;
        }
        key.Handled = true;
        return true;
      }

      // Arrow up -> navigate list
      if (key == Key.CursorUp) {
        _palette.MoveUp();
        if (_palette.SelectedIndex >= 0) {
          _listView.SelectedItem = _palette.SelectedIndex;
        } else {
          _listView.SelectedItem = null;
        }
        key.Handled = true;
        return true;
      }

      // Enter -> execute selected command and hide
      if (key == Key.Enter) {
        PaletteCommand? cmd = _palette.GetSelected();
        Visible = false;
        cmd?.Execute();
        key.Handled = true;
        return true;
      }
    }

    return base.OnKeyDown(key);
  }

  private void HandleGotoInput(string rawInput)
  {
    // Strip the leading ":"
    string input = rawInput.Length > 1 ? rawInput[1..].Trim() : "";

    if (string.IsNullOrEmpty(input)) {
      long totalLines = Math.Max(1, _state.EstimatedTotalLines);
      _hintLabel.Text = _state.ActiveView == ViewMode.Hex
          ? "Enter hex offset (e.g. :0x1A3F)"
          : $"Enter line number (1 to ~{totalLines})";
      return;
    }

    if (_state.ActiveView == ViewMode.Hex) {
      // Hex view: parse as hex offset
      if (TryParseOffset(input, out long offset)) {
        offset = Math.Clamp(offset, 0, _state.FileLength);
        _hintLabel.Text = $"Press Enter to jump to offset 0x{offset:X}";
        _gotoOffset(offset);
      } else {
        _hintLabel.Text = "Invalid offset format";
      }
    } else {
      // Text view: parse :line or :line:col
      string[] parts = input.Split(':', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length >= 1 && long.TryParse(parts[0], out long lineNum) && lineNum >= 1) {
        _gotoLine(lineNum);
        if (parts.Length >= 2 && int.TryParse(parts[1], out int col) && col >= 1) {
          _hintLabel.Text = $"Press Enter to jump to line {lineNum}, column {col}";
          MoveToColumn(col - 1);
        } else if (input.EndsWith(':')) {
          _hintLabel.Text = $"Now enter column number for line {lineNum}";
        } else {
          _hintLabel.Text = $"Press Enter to jump to line {lineNum} (add : for column)";
        }
      } else {
        _hintLabel.Text = "Enter a valid line number";
      }
    }
  }

  private void MoveToColumn(int columnChars)
  {
    Document? doc = _state.Document;
    if (doc is null || columnChars <= 0) return;

    long lineStart = _state.TextCursorOffset;
    int readLen = Math.Min(columnChars * 4 + 64, 8192);
    byte[] buf = new byte[readLen];
    int read = doc.Read(lineStart, buf.AsSpan(0, (int)Math.Min(readLen, doc.Length - lineStart)));
    if (read == 0) return;

    ITextDecoder decoder = _state.Decoder;
    int charCount = 0;
    int pos = 0;
    while (pos < read && charCount < columnChars) {
      (_, int len) = decoder.DecodeRune(buf.AsSpan(0, read), pos);
      if (len <= 0) { pos++; continue; }
      if (decoder.IsNewline(buf, pos, out _)) break;
      pos += len;
      charCount++;
    }
    _state.TextCursorOffset = lineStart + pos;
  }

  private void CancelAndHide()
  {
    if (_isGotoMode && _state.GotoPreviewOrigin >= 0) {
      // Revert to original position
      if (_state.ActiveView == ViewMode.Hex) {
        _state.HexCursorOffset = _state.GotoPreviewOrigin;
        _state.HexBaseOffset = _state.GotoPreviewTopOrigin;
      } else {
        _state.TextCursorOffset = _state.GotoPreviewOrigin;
        _state.TextTopOffset = _state.GotoPreviewTopOrigin;
      }
      _state.GotoPreviewOrigin = -1;
    }
    Visible = false;
  }

  private void UpdateList()
  {
    List<string> items = _palette.FilteredCommands
        .Select(c => $"[{c.Category}] {c.Name}  {c.Shortcut}")
        .ToList();
    _listView.SetSource(new ObservableCollection<string>(items));
    if (items.Count == 0 || _palette.SelectedIndex < 0) {
      _listView.SelectedItem = null;
      return;
    }

    _listView.SelectedItem = Math.Clamp(_palette.SelectedIndex, 0, items.Count - 1);
  }

  private static bool TryParseOffset(string input, out long offset)
  {
    offset = 0;
    if (string.IsNullOrWhiteSpace(input)) return false;
    input = input.Trim();
    if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
      return long.TryParse(input[2..], System.Globalization.NumberStyles.HexNumber, null, out offset);
    if (input.Any(c => c is >= 'a' and <= 'f' or >= 'A' and <= 'F'))
      return long.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out offset);
    return long.TryParse(input, out offset);
  }
}