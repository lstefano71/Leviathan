using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Compact VS Code-style find bar: [text] [Aa] [Hx] 3/15 [◀] [▶] [✕]
/// Single-row, anchored top-right. Enter searches forward, Esc dismisses.
/// </summary>
internal sealed class FindBar : PopoverImpl
{
  private readonly AppState _state;
  private readonly TextField _queryField;
  private readonly Button _caseButton;
  private readonly Button _hexButton;
  private readonly Label _statusLabel;
  private readonly Button _prevButton;
  private readonly Button _nextButton;
  private readonly Button _closeButton;
  private readonly Action<string> _startSearch;
  private readonly Action _findNext;
  private readonly Action _findPrev;

  internal FindBar(AppState state, Action<string> startSearch, Action findNext, Action findPrev)
  {
    _state = state;
    _startSearch = startSearch;
    _findNext = findNext;
    _findPrev = findPrev;

    Attribute barBg = new(new Color(StandardColor.White), new Color(40, 40, 60));

    // Container: single-row bar at top-right
    View bar = new() {
      X = Pos.AnchorEnd(72),
      Y = 0,
      Width = 72,
      Height = 1,
    };
    bar.DrawingContent += (_, _) => {
      bar.SetAttribute(barBg);
      for (int c = 0; c < 72; c++) {
        bar.Move(c, 0);
        bar.AddRune(' ');
      }
    };

    _queryField = new TextField() {
      X = 0,
      Y = 0,
      Width = 30,
      Text = state.FindInput ?? "",
    };

    _caseButton = new Button() {
      X = Pos.Right(_queryField),
      Y = 0,
      Width = 4,
      Text = "Aa",
      NoDecorations = true,
      NoPadding = true,
      CanFocus = false,
    };
    _caseButton.Accepting += (_, _) => {
      _state.FindCaseSensitive = !_state.FindCaseSensitive;
      UpdateToggleColors();
      RerunSearch();
    };

    _hexButton = new Button() {
      X = Pos.Right(_caseButton) + 1,
      Y = 0,
      Width = 4,
      Text = "Hx",
      NoDecorations = true,
      NoPadding = true,
      CanFocus = false,
    };
    _hexButton.Accepting += (_, _) => {
      _state.FindHexMode = !_state.FindHexMode;
      UpdateToggleColors();
      RerunSearch();
    };

    _statusLabel = new Label() {
      X = Pos.Right(_hexButton) + 1,
      Y = 0,
      Width = 12,
      Text = "",
    };

    _prevButton = new Button() {
      X = Pos.Right(_statusLabel),
      Y = 0,
      Width = 3,
      Text = "◀",
      NoDecorations = true,
      NoPadding = true,
      CanFocus = false,
    };
    _prevButton.Accepting += (_, _) => {
      _findPrev();
      UpdateStatus();
    };

    _nextButton = new Button() {
      X = Pos.Right(_prevButton) + 1,
      Y = 0,
      Width = 3,
      Text = "▶",
      NoDecorations = true,
      NoPadding = true,
      CanFocus = false,
    };
    _nextButton.Accepting += (_, _) => {
      _findNext();
      UpdateStatus();
    };

    _closeButton = new Button() {
      X = Pos.Right(_nextButton) + 1,
      Y = 0,
      Width = 3,
      Text = "✕",
      NoDecorations = true,
      NoPadding = true,
      CanFocus = false,
    };
    _closeButton.Accepting += (_, _) => {
      Visible = false;
    };

    bar.Add(_queryField, _caseButton, _hexButton, _statusLabel, _prevButton, _nextButton, _closeButton);
    Add(bar);
  }

  /// <summary>Shows the find bar and focuses the query field.</summary>
  internal void ShowBar()
  {
    _queryField.Text = _state.FindInput ?? "";
    UpdateToggleColors();
    UpdateStatus();
    App?.Popovers?.Show(this);
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
      RunSearch();
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

  private void RunSearch()
  {
    string query = _queryField.Text?.Trim() ?? "";
    if (string.IsNullOrEmpty(query)) return;
    _state.FindInput = query;
    _state.Settings.AddFindHistory(query);
    _startSearch(query);
  }

  private void RerunSearch()
  {
    string query = _queryField.Text?.Trim() ?? "";
    if (!string.IsNullOrEmpty(query)) {
      _state.FindInput = query;
      _startSearch(query);
    }
  }

  private void UpdateToggleColors()
  {
    Attribute activeAttr = new(new Color(StandardColor.Black), new Color(208, 135, 46));
    Attribute inactiveAttr = new(new Color(StandardColor.White), new Color(60, 60, 80));

    _caseButton.SetAttribute(_state.FindCaseSensitive ? activeAttr : inactiveAttr);
    _hexButton.SetAttribute(_state.FindHexMode ? activeAttr : inactiveAttr);
  }
}
