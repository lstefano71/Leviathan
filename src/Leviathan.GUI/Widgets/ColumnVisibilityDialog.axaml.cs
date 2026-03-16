using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Modal dialog for toggling column visibility in the CSV view.
/// Lists all columns with checkboxes; supports search-as-you-type filtering.
/// </summary>
public sealed partial class ColumnVisibilityDialog : Window
{
    private readonly AppState _state;
    private readonly int _columnCount;
    private readonly string[] _columnNames;
    private readonly List<CheckBox> _checkBoxes = [];

    /// <summary>True if the user changed any column visibility.</summary>
    public bool Changed { get; private set; }

    public ColumnVisibilityDialog(AppState state)
    {
        _state = state;
        _columnCount = state.CsvColumnCount;
        string[] headers = state.CsvHeaderNames;
        _columnNames = new string[_columnCount];
        for (int i = 0; i < _columnCount; i++)
            _columnNames[i] = i < headers.Length && !string.IsNullOrEmpty(headers[i])
                ? headers[i]
                : $"Column {i + 1}";
        InitializeComponent();

        BuildColumnList(filter: null);

        SearchBox.TextChanged += (_, _) => BuildColumnList(SearchBox.Text);
        ShowAllButton.Click += OnShowAll;
        HideAllButton.Click += OnHideAll;
        CloseButton.Click += (_, _) => Close();

        Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
        };
    }

    public ColumnVisibilityDialog() : this(new AppState()) { }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void BuildColumnList(string? filter)
    {
        ColumnList.Items.Clear();
        _checkBoxes.Clear();

        HashSet<int> hidden = _state.CsvHiddenColumns;

        for (int i = 0; i < _columnCount; i++)
        {
            string name = _columnNames[i];
            if (!string.IsNullOrEmpty(filter) &&
                !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            int colIndex = i;
            CheckBox cb = new()
            {
                Content = name,
                IsChecked = !hidden.Contains(colIndex),
                Tag = colIndex,
                Margin = new Avalonia.Thickness(2, 1)
            };

            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true)
                    hidden.Remove(colIndex);
                else
                    hidden.Add(colIndex);
                Changed = true;
            };

            _checkBoxes.Add(cb);
            ColumnList.Items.Add(cb);
        }
    }

    private void OnShowAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _state.CsvHiddenColumns.Clear();
        Changed = true;
        foreach (CheckBox cb in _checkBoxes)
            cb.IsChecked = true;
    }

    private void OnHideAll(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        for (int i = 0; i < _columnCount; i++)
            _state.CsvHiddenColumns.Add(i);
        Changed = true;
        foreach (CheckBox cb in _checkBoxes)
            cb.IsChecked = false;
    }
}
