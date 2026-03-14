using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Popover bar for configuring CSV dialect settings: separator, quote character,
/// and header row toggle. Anchored at top-right of the screen.
/// </summary>
internal sealed class CsvSettingsBar : PopoverImpl
{
  private readonly AppState _state;
  private readonly OptionSelector _separatorSelector;
  private readonly OptionSelector _quoteSelector;
  private readonly CheckBox _headerCheckBox;
  private readonly Action _onApply;

  private static readonly (string Label, byte Value)[] Separators =
  [
    ("Comma (,)", (byte)','),
    ("Tab (\\t)", (byte)'\t'),
    ("Pipe (|)", (byte)'|'),
    ("Semicolon (;)", (byte)';')
  ];

  private static readonly (string Label, byte Value)[] Quotes =
  [
    ("Double quote (\")", (byte)'"'),
    ("Single quote (')", (byte)'\''),
    ("None", 0)
  ];

  internal CsvSettingsBar(AppState state, Action onApply)
  {
    _state = state;
    _onApply = onApply;
    Visible = false;

    FrameView bar = new() {
      SchemeName = "Menu",
      Title = "CSV Settings",
      X = Pos.AnchorEnd(46),
      Y = 0,
      Width = 44,
      Height = 14
    };

    // Separator selector
    Label sepLabel = new() { Text = "Separator:", X = 1, Y = 0 };
    _separatorSelector = new OptionSelector
    {
      X = 1,
      Y = 1,
      Labels = Separators.Select(s => s.Label).ToArray(),
      Values = Enumerable.Range(0, Separators.Length).ToArray(),
      Value = GetSeparatorIndex(_state.CsvDialect.Separator),
      Orientation = Orientation.Vertical,
    };

    // Quote selector
    Label quoteLabel = new() { Text = "Quote:", X = 22, Y = 0 };
    _quoteSelector = new OptionSelector
    {
      X = 22,
      Y = 1,
      Labels = Quotes.Select(q => q.Label).ToArray(),
      Values = Enumerable.Range(0, Quotes.Length).ToArray(),
      Value = GetQuoteIndex(_state.CsvDialect.Quote),
      Orientation = Orientation.Vertical,
    };

    // Header checkbox
    _headerCheckBox = new CheckBox
    {
      Title = "First row is header",
      X = 1,
      Y = 6,
      Value = _state.CsvDialect.HasHeader ? CheckState.Checked : CheckState.UnChecked
    };

    // Apply button
    Button applyButton = new()
    {
      Title = "_Apply",
      X = 1,
      Y = 8,
      IsDefault = true
    };
    applyButton.Accepting += (_, _) => Apply();

    // Close button
    Button closeButton = new()
    {
      Title = "_Cancel",
      X = 12,
      Y = 8
    };
    closeButton.Accepting += (_, _) => Hide();

    bar.Add(sepLabel, _separatorSelector, quoteLabel, _quoteSelector, _headerCheckBox, applyButton, closeButton);
    Add(bar);
  }

  /// <summary>Updates the controls to reflect current state.</summary>
  internal void Refresh()
  {
    _separatorSelector.Value = GetSeparatorIndex(_state.CsvDialect.Separator);
    _quoteSelector.Value = GetQuoteIndex(_state.CsvDialect.Quote);
    _headerCheckBox.Value = _state.CsvDialect.HasHeader ? CheckState.Checked : CheckState.UnChecked;
  }

  private void Apply()
  {
    int sepIdx = _separatorSelector.Value ?? 0;
    int quoteIdx = _quoteSelector.Value ?? 0;
    byte sep = Separators[sepIdx].Value;
    byte quote = Quotes[quoteIdx].Value;
    bool hasHeader = _headerCheckBox.Value == CheckState.Checked;

    _state.CsvDialect = new Leviathan.Core.Csv.CsvDialect(sep, quote, quote, hasHeader);

    // Save per-file settings
    if (_state.CurrentFilePath is not null)
    {
      _state.Settings.SetCsvFileSettings(_state.CurrentFilePath, new CsvFileSettings {
        Separator = sep,
        Quote = quote,
        Escape = quote,
        HasHeader = hasHeader
      });
    }

    // Re-initialise the CSV view with new dialect
    _state.InitCsvView();

    Hide();
    _onApply();
  }

  private void Hide()
  {
    App?.Popovers?.Hide(this);
  }

  protected override bool OnKeyDown(Key key)
  {
    if (key == Key.Esc)
    {
      Hide();
      return true;
    }
    return base.OnKeyDown(key);
  }

  private static int GetSeparatorIndex(byte sep)
  {
    for (int i = 0; i < Separators.Length; i++)
      if (Separators[i].Value == sep) return i;
    return 0;
  }

  private static int GetQuoteIndex(byte quote)
  {
    for (int i = 0; i < Quotes.Length; i++)
      if (Quotes[i].Value == quote) return i;
    return 0;
  }
}
