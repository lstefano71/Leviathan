using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Non-modal find bar popover (VS Code-style). Stays visible while user navigates results.
/// F3/Shift+F3 cycle matches; Enter starts a new search; Esc dismisses.
/// </summary>
internal sealed class FindBar : PopoverImpl
{
  private readonly AppState _state;
  private readonly TextField _queryField;
  private readonly CheckBox _hexModeCheck;
  private readonly Label _statusLabel;
  private readonly Action<string> _startSearch;
  private readonly Action _findNext;
  private readonly Action _findPrev;

  internal FindBar(AppState state, Action<string> startSearch, Action findNext, Action findPrev)
  {
    _state = state;
    _startSearch = startSearch;
    _findNext = findNext;
    _findPrev = findPrev;

    Attribute barNormal = new(new Color(StandardColor.White), new Color(40, 40, 60));
    Attribute barHot = new(new Color(StandardColor.Yellow), new Color(40, 40, 60));
    

    FrameView bar = new() {
      Title = "Find",
      X = 0,
      Y = 0,
      Width = Dim.Fill(),
      Height = 4,
    };

    Label searchLabel = new() {
      Text = "Search: ",
      X = 0,
      Y = 0,
    };

    _queryField = new TextField() {
      X = Pos.Right(searchLabel),
      Y = 0,
      Width = 40,
      Text = state.FindInput ?? "",
    };

    _hexModeCheck = new CheckBox() {
      Text = "_Hex",
      X = Pos.Right(_queryField) + 1,
      Y = 0,
      Value = state.FindHexMode ? CheckState.Checked : CheckState.UnChecked,
    };

    _statusLabel = new Label() {
      X = Pos.Right(_hexModeCheck) + 2,
      Y = 0,
      Width = 30,
      Text = "",
    };

    Label hints = new() {
      X = 0,
      Y = 1,
      Text = "Enter:Search  F3:Next  Shift+F3:Prev  Esc:Close",
    };

    bar.Add(searchLabel, _queryField, _hexModeCheck, _statusLabel, hints);
    Add(bar);
  }

  /// <summary>Shows the find bar and focuses the query field.</summary>
  internal void ShowBar()
  {
    _queryField.Text = _state.FindInput ?? "";
    _hexModeCheck.Value = _state.FindHexMode ? CheckState.Checked : CheckState.UnChecked;
    UpdateStatus();
    App?.Popovers.Show(this);
    _queryField.SetFocus();
  }

  /// <summary>Updates the status label from current search state.</summary>
  internal void UpdateStatus()
  {
    if (_state.IsSearching)
      _statusLabel.Text = "Searching…";
    else if (_state.SearchResults.Count > 0)
      _statusLabel.Text = $"{_state.CurrentMatchIndex + 1}/{_state.SearchResults.Count}";
    else if (!string.IsNullOrEmpty(_state.SearchStatus))
      _statusLabel.Text = _state.SearchStatus;
    else
      _statusLabel.Text = "";
  }

  /// <inheritdoc/>
  protected override bool OnKeyDown(Key key)
  {
    if (!Visible)
      return base.OnKeyDown(key);

    // Ctrl+F when visible → hide
    if (key == Key.F.WithCtrl) {
      Visible = false;
      key.Handled = true;
      return true;
    }

    // Enter → start search with current query
    if (key == Key.Enter) {
      string query = _queryField.Text?.Trim() ?? "";
      if (!string.IsNullOrEmpty(query)) {
        _state.FindInput = query;
        _state.FindHexMode = _hexModeCheck.Value == CheckState.Checked;
        _state.Settings.AddFindHistory(query);
        _startSearch(query);
      }
      key.Handled = true;
      return true;
    }

    // F3 → find next
    if (key == Key.F3) {
      _findNext();
      UpdateStatus();
      key.Handled = true;
      return true;
    }

    // Shift+F3 → find previous
    if (key == Key.F3.WithShift) {
      _findPrev();
      UpdateStatus();
      key.Handled = true;
      return true;
    }

    return base.OnKeyDown(key);
  }
}
