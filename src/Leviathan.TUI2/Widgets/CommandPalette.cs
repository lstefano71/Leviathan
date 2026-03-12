namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Command palette entry: name, shortcut label, and action to execute.
/// Supports dynamic names via <see cref="NameFunc"/> for check/radio indicators.
/// </summary>
internal sealed class PaletteCommand
{
  /// <summary>Category grouping label (e.g. "View", "File").</summary>
  internal string Category { get; }

  /// <summary>Function that returns the display name (evaluated on each render for dynamic indicators).</summary>
  internal Func<string> NameFunc { get; }

  /// <summary>Current display name.</summary>
  internal string Name => NameFunc();

  /// <summary>Keyboard shortcut label (e.g. "Ctrl+O").</summary>
  internal string Shortcut { get; }

  /// <summary>Action to execute when the command is selected.</summary>
  internal Action Execute { get; }

  internal PaletteCommand(string category, string name, string shortcut, Action execute)
      : this(category, () => name, shortcut, execute) { }

  internal PaletteCommand(string category, Func<string> nameFunc, string shortcut, Action execute)
  {
    Category = category;
    NameFunc = nameFunc;
    Shortcut = shortcut;
    Execute = execute;
  }
}

/// <summary>
/// Command palette state and logic for the VS Code-style Ctrl+P overlay.
/// </summary>
internal sealed class CommandPalette
{
  private readonly List<PaletteCommand> _allCommands = [];
  private List<PaletteCommand> _filtered = [];
  private string _query = "";
  private int _selectedIndex;

  internal string Query {
    get => _query;
    set {
      _query = value;
      FilterCommands();
    }
  }

  internal IReadOnlyList<PaletteCommand> FilteredCommands => _filtered;
  internal int SelectedIndex => _selectedIndex;

  internal void RegisterCommand(string category, string name, string shortcut, Action execute)
  {
    _allCommands.Add(new PaletteCommand(category, name, shortcut, execute));
    FilterCommands();
  }

  internal void RegisterCommand(string category, Func<string> nameFunc, string shortcut, Action execute)
  {
    _allCommands.Add(new PaletteCommand(category, nameFunc, shortcut, execute));
    FilterCommands();
  }

  internal void MoveUp()
  {
    if (_selectedIndex > 0) _selectedIndex--;
  }

  internal void MoveDown()
  {
    if (_selectedIndex < _filtered.Count - 1) _selectedIndex++;
  }

  internal PaletteCommand? GetSelected()
  {
    if (_selectedIndex >= 0 && _selectedIndex < _filtered.Count)
      return _filtered[_selectedIndex];
    return null;
  }

  internal void Reset()
  {
    _query = "";
    _selectedIndex = 0;
    FilterCommands();
  }

  private void FilterCommands()
  {
    if (string.IsNullOrWhiteSpace(_query)) {
      _filtered = new List<PaletteCommand>(_allCommands);
    } else {
      string q = _query.Trim();
      _filtered = _allCommands
          .Where(c => FuzzyMatch(c, q))
          .ToList();
    }
    if (_filtered.Count == 0) {
      _selectedIndex = -1;
      return;
    }

    _selectedIndex = Math.Clamp(_selectedIndex, 0, _filtered.Count - 1);
  }

  private static bool FuzzyMatch(PaletteCommand cmd, string query)
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
