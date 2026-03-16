using Avalonia.Controls;
using Avalonia.Input;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// VS Code-style command palette overlay (Ctrl+P).
/// Supports fuzzy command search and ":" prefix for goto mode.
/// </summary>
public sealed partial class CommandPaletteOverlay : UserControl
{
    private readonly AppState _state;
    private readonly List<CommandEntry> _allCommands = [];
    private readonly Action<long> _gotoOffset;
    private readonly Action<long> _gotoLine;

    public CommandPaletteOverlay(AppState state, Action<long> gotoOffset, Action<long> gotoLine)
    {
        _state = state;
        _gotoOffset = gotoOffset;
        _gotoLine = gotoLine;

        InitializeComponent();

        PaletteInput.KeyDown += OnInputKeyDown;
        PaletteInput.TextChanged += OnInputChanged;
        CommandList.DoubleTapped += OnCommandSelected;
    }

    public CommandPaletteOverlay() : this(new AppState(), _ => { }, _ => { }) { }

    /// <summary>Registers a command in the palette.</summary>
    public void RegisterCommand(string name, string description, Action execute)
    {
        _allCommands.Add(new CommandEntry(name, description, execute));
    }

    /// <summary>Shows the palette and focuses the input.</summary>
    public void Show()
    {
        IsVisible = true;
        PaletteInput.Text = "";
        PaletteInput.Focus();
        FilterCommands("");
    }

    /// <summary>Shows the palette in goto mode.</summary>
    public void ShowGoto()
    {
        IsVisible = true;
        PaletteInput.Text = ":";
        PaletteInput.Focus();
        PaletteInput.CaretIndex = 1;
    }

    /// <summary>Hides the palette.</summary>
    public void Hide()
    {
        IsVisible = false;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Down:
                if (CommandList.ItemCount > 0)
                    CommandList.SelectedIndex = Math.Min(CommandList.SelectedIndex + 1, CommandList.ItemCount - 1);
                e.Handled = true;
                break;
            case Key.Up:
                if (CommandList.ItemCount > 0)
                    CommandList.SelectedIndex = Math.Max(CommandList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
        }
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        string input = PaletteInput.Text ?? "";

        if (input.StartsWith(':'))
        {
            // Goto mode — show hint
            CommandList.ItemsSource = new[] { "Type a line number or 0x<hex offset> then press Enter" };
        }
        else
        {
            FilterCommands(input);
        }
    }

    private void FilterCommands(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            CommandList.ItemsSource = _allCommands.Select(c => $"{c.Name} — {c.Description}").ToArray();
        }
        else
        {
            string lowerQuery = query.ToLowerInvariant();
            string[] filtered = _allCommands
                .Where(c => c.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)
                         || c.Description.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name} — {c.Description}")
                .ToArray();
            CommandList.ItemsSource = filtered;
        }

        if (CommandList.ItemCount > 0)
            CommandList.SelectedIndex = 0;
    }

    private void ExecuteSelected()
    {
        string input = PaletteInput.Text ?? "";

        // Goto mode
        if (input.StartsWith(':'))
        {
            string target = input[1..].Trim();
            if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(target[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexOffset))
            {
                _gotoOffset(hexOffset);
                Hide();
                return;
            }

            if (long.TryParse(target, out long lineNum) && lineNum > 0)
            {
                if (_state.ActiveView == ViewMode.Text)
                    _gotoLine(lineNum);
                else
                    _gotoOffset(lineNum);
                Hide();
                return;
            }
        }

        // Command mode
        if (CommandList.SelectedIndex >= 0 && CommandList.SelectedIndex < _allCommands.Count)
        {
            _allCommands[CommandList.SelectedIndex].Execute();
            Hide();
        }
    }

    private void OnCommandSelected(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ExecuteSelected();
    }

    private sealed record CommandEntry(string Name, string Description, Action Execute);
}
