using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Non-modal goto bar popover. Enter navigates and dismisses; Esc cancels.
/// </summary>
internal sealed class GotoBar : PopoverImpl
{
  private readonly AppState _state;
  private readonly Label _promptLabel;
  private readonly TextField _inputField;
  private readonly Action<long> _gotoOffset;
  private readonly Action<long> _gotoLine;

  internal GotoBar(AppState state, Action<long> gotoOffset, Action<long> gotoLine)
  {
    _state = state;
    _gotoOffset = gotoOffset;
    _gotoLine = gotoLine;

    Attribute barNormal = new(new Color(StandardColor.White), new Color(40, 40, 60));
    Attribute barHot = new(new Color(StandardColor.Yellow), new Color(40, 40, 60));
    
    FrameView bar = new() {
      Title = "Go To",
      X = 0,
      Y = 0,
      Width = Dim.Fill(),
      Height = 3,    
    };

    _promptLabel = new Label() {
      X = 0,
      Y = 0,
      Text = "Line: ",
    };

    _inputField = new TextField() {
      X = Pos.Right(_promptLabel),
      Y = 0,
      Width = 30,
      Text = "",
    };

    Label hints = new() {
      X = Pos.Right(_inputField) + 2,
      Y = 0,
      Text = "Enter:Go  Esc:Cancel",
    };

    bar.Add(_promptLabel, _inputField, hints);
    Add(bar);
  }

  /// <summary>Shows the goto bar and focuses the input field.</summary>
  internal void ShowBar()
  {
    _promptLabel.Text = _state.ActiveView == ViewMode.Hex
        ? "Offset (hex e.g. 0x1A3F): "
        : "Line: ";
    _inputField.Text = "";
    App?.Popovers.Show(this);
    _inputField.SetFocus();
  }

  /// <inheritdoc/>
  protected override bool OnKeyDown(Key key)
  {
    if (!Visible)
      return base.OnKeyDown(key);

    // Ctrl+G when visible → hide
    if (key == Key.G.WithCtrl) {
      Visible = false;
      key.Handled = true;
      return true;
    }

    // Enter → parse and navigate, then hide
    if (key == Key.Enter) {
      ExecuteGoto();
      key.Handled = true;
      return true;
    }

    return base.OnKeyDown(key);
  }

  private void ExecuteGoto()
  {
    string input = _inputField.Text?.Trim() ?? "";
    if (!string.IsNullOrEmpty(input)) {
      if (_state.ActiveView == ViewMode.Hex) {
        if (TryParseOffset(input, out long offset))
          _gotoOffset(offset);
      } else {
        if (long.TryParse(input, out long lineNum))
          _gotoLine(lineNum);
      }
    }
    Visible = false;
  }

  private static bool TryParseOffset(string input, out long offset)
  {
    offset = 0;
    if (string.IsNullOrWhiteSpace(input)) return false;

    input = input.Trim();
    if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("0X", StringComparison.OrdinalIgnoreCase)) {
      return long.TryParse(input[2..], System.Globalization.NumberStyles.HexNumber, null, out offset);
    }

    if (input.Any(c => c is >= 'a' and <= 'f' or >= 'A' and <= 'F'))
      return long.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out offset);

    return long.TryParse(input, out offset);
  }
}
