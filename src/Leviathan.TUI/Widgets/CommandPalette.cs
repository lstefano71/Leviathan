namespace Leviathan.TUI.Widgets;

/// <summary>
/// Command palette entry: name, shortcut label, and action to execute.
/// </summary>
internal sealed record Command(string Category, string Name, string Shortcut, Action Execute);

/// <summary>
/// Command palette state and logic for the VS Code-style Ctrl+P overlay.
/// </summary>
internal sealed class CommandPalette
{
    private readonly AppState _state;
    private readonly List<Command> _allCommands = [];
    private List<Command> _filtered = [];
    private string _query = "";
    private int _selectedIndex;

    internal CommandPalette(AppState state)
    {
        _state = state;
    }

    internal string Query {
        get => _query;
        set {
            _query = value;
            FilterCommands();
        }
    }

    internal IReadOnlyList<Command> FilteredCommands => _filtered;
    internal int SelectedIndex => _selectedIndex;

    internal void RegisterCommand(string category, string name, string shortcut, Action execute)
    {
        _allCommands.Add(new Command(category, name, shortcut, execute));
        FilterCommands();
    }

    internal void Open()
    {
        _query = "";
        _selectedIndex = 0;
        FilterCommands();
        _state.ShowCommandPalette = true;
    }

    internal void Close()
    {
        _state.ShowCommandPalette = false;
    }

    internal void MoveUp()
    {
        if (_selectedIndex > 0) _selectedIndex--;
    }

    internal void MoveDown()
    {
        if (_selectedIndex < _filtered.Count - 1) _selectedIndex++;
    }

    internal void Execute()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _filtered.Count) {
            Command cmd = _filtered[_selectedIndex];
            Close();
            cmd.Execute();
        }
    }

    private void FilterCommands()
    {
        if (string.IsNullOrWhiteSpace(_query)) {
            _filtered = new List<Command>(_allCommands);
        } else {
            string q = _query.Trim();
            _filtered = _allCommands
                .Where(c => FuzzyMatch(c, q))
                .ToList();
        }
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _filtered.Count - 1));
    }

    private static bool FuzzyMatch(Command cmd, string query)
    {
        string full = $"{cmd.Category} {cmd.Name}";
        int qi = 0;
        foreach (char c in full) {
            if (qi < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
                qi++;
        }
        return qi == query.Length;
    }
}
