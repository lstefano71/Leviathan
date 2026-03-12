using System.Collections.ObjectModel;

using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Non-modal command palette popover. TextField for fuzzy filtering + ListView of commands.
/// </summary>
internal sealed class CommandPalettePopover : PopoverImpl
{
  private readonly CommandPalette _palette;
  private readonly TextField _queryField;
  private readonly ListView _listView;

  internal CommandPalettePopover(CommandPalette palette)
  {
    _palette = palette;

    Attribute panelNormal = new(new Color(StandardColor.White), new Color(30, 30, 50));
    Attribute panelHot = new(new Color(StandardColor.Yellow), new Color(30, 30, 50));

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

    _listView = new ListView() {
      X = 0,
      Y = Pos.Bottom(_queryField),
      Width = Dim.Fill(),
      Height = Dim.Fill(),
    };

    // Update filtering as user types
    _queryField.KeyUp += (_, _) => {
      string newQuery = _queryField.Text?.ToString() ?? "";
      if (newQuery != _palette.Query) {
        _palette.Query = newQuery;
        UpdateList();
      }
    };

    panel.Add(_queryField, _listView);
    Add(panel);
  }

  /// <summary>Shows the command palette and focuses the query field.</summary>
  internal void ShowPalette()
  {
    _palette.Reset();
    _queryField.Text = "";
    UpdateList();
    App?.Popovers.Show(this);
    _queryField.SetFocus();
  }

  /// <inheritdoc/>
  protected override bool OnKeyDown(Key key)
  {
    if (!Visible)
      return base.OnKeyDown(key);

    // Ctrl+P when visible → hide
    if (key == Key.P.WithCtrl) {
      Visible = false;
      key.Handled = true;
      return true;
    }

    // Arrow down → navigate list
    if (key == Key.CursorDown) {
      _palette.MoveDown();
      _listView.SelectedItem = _palette.SelectedIndex;
      key.Handled = true;
      return true;
    }

    // Arrow up → navigate list
    if (key == Key.CursorUp) {
      _palette.MoveUp();
      _listView.SelectedItem = _palette.SelectedIndex;
      key.Handled = true;
      return true;
    }

    // Enter → execute selected command and hide
    if (key == Key.Enter) {
      PaletteCommand? cmd = _palette.GetSelected();
      Visible = false;
      cmd?.Execute();
      key.Handled = true;
      return true;
    }

    return base.OnKeyDown(key);
  }

  private void UpdateList()
  {
    List<string> items = _palette.FilteredCommands
        .Select(c => $"[{c.Category}] {c.Name}  {c.Shortcut}")
        .ToList();
    _listView.SetSource(new ObservableCollection<string>(items));
    _listView.SelectedItem = _palette.SelectedIndex;
  }
}
