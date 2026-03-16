using Avalonia.Controls;
using Leviathan.Core.Csv;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Dialog for configuring CSV dialect (separator, quote character, header toggle).
/// </summary>
public sealed partial class CsvSettingsDialog : Window
{
    private readonly AppState _state;

    /// <summary>True if the user clicked Apply.</summary>
    public bool Applied { get; private set; }

    public CsvSettingsDialog(AppState state)
    {
        _state = state;
        InitializeComponent();

        // Set current values
        SetSeparatorSelection(state.CsvDialect.Separator);
        SetQuoteSelection(state.CsvDialect.Quote);
        HasHeaderCheck.IsChecked = state.CsvDialect.HasHeader;

        ApplyButton.Click += OnApply;
        CancelButton.Click += (_, _) => Close();
    }

    public CsvSettingsDialog() : this(new AppState()) { }

    private void OnApply(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        byte separator = GetSelectedTag(SeparatorCombo);
        byte quote = GetSelectedTag(QuoteCombo);
        bool hasHeader = HasHeaderCheck.IsChecked == true;

        _state.CsvDialect = new CsvDialect(separator, quote, quote, hasHeader);

        // Re-initialize CSV view with new dialect
        _state.InitCsvView();

        // Save per-file settings
        if (_state.CurrentFilePath is not null)
        {
            _state.Settings.SetCsvFileSettings(_state.CurrentFilePath, new CsvFileSettings
            {
                Separator = separator,
                Quote = quote,
                Escape = quote,
                HasHeader = hasHeader
            });
        }

        Applied = true;
        Close();
    }

    private void SetSeparatorSelection(byte separator)
    {
        int index = separator switch
        {
            (byte)',' => 0,
            (byte)'\t' => 1,
            (byte)';' => 2,
            (byte)'|' => 3,
            _ => 0
        };
        SeparatorCombo.SelectedIndex = index;
    }

    private void SetQuoteSelection(byte quote)
    {
        int index = quote switch
        {
            (byte)'"' => 0,
            (byte)'\'' => 1,
            0 => 2,
            _ => 0
        };
        QuoteCombo.SelectedIndex = index;
    }

    private static byte GetSelectedTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && byte.TryParse(tagStr, out byte val))
            return val;
        return 0;
    }
}
