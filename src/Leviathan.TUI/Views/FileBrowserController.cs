namespace Leviathan.TUI.Views;

/// <summary>
/// File browser state and logic for the TUI file-open dialog.
/// Pure logic — no hex1b dependencies. Produces strings for the TUI to render.
/// </summary>
internal sealed class FileBrowserController
{
    private readonly AppState _state;
    private string _currentDirectory;
    private List<FileEntry> _allEntries = [];
    private List<FileEntry> _filteredEntries = [];
    private int _selectedIndex;
    private int _scrollOffset;
    private string _filter = "";

    internal FileBrowserController(AppState state)
    {
        _state = state;
        _currentDirectory = Directory.GetCurrentDirectory();
    }

    internal string CurrentDirectory => _currentDirectory;
    internal IReadOnlyList<FileEntry> FilteredEntries => _filteredEntries;
    internal int SelectedIndex => _selectedIndex;
    internal int ScrollOffset => _scrollOffset;

    internal string Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            ApplyFilter();
        }
    }

    /// <summary>
    /// Opens the browser at the specified directory (or cwd if null).
    /// </summary>
    internal void Open(string? startDirectory = null)
    {
        if (startDirectory is not null && Directory.Exists(startDirectory))
            _currentDirectory = startDirectory;
        else if (_state.CurrentFilePath is not null)
        {
            string? dir = Path.GetDirectoryName(_state.CurrentFilePath);
            if (dir is not null && Directory.Exists(dir))
                _currentDirectory = dir;
        }

        _filter = "";
        _selectedIndex = 0;
        _scrollOffset = 0;
        ScanDirectory();
    }

    /// <summary>
    /// Navigates into a subdirectory.
    /// </summary>
    internal void EnterDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            _currentDirectory = Path.GetFullPath(path);
            _filter = "";
            _selectedIndex = 0;
            _scrollOffset = 0;
            ScanDirectory();
        }
    }

    /// <summary>
    /// Navigates to parent directory.
    /// </summary>
    internal void GoUp()
    {
        string? parent = Directory.GetParent(_currentDirectory)?.FullName;
        if (parent is not null)
            EnterDirectory(parent);
    }

    internal void MoveUp(int visibleRows)
    {
        if (_selectedIndex > 0)
        {
            _selectedIndex--;
            EnsureVisible(visibleRows);
        }
    }

    internal void MoveDown(int visibleRows)
    {
        if (_selectedIndex < _filteredEntries.Count - 1)
        {
            _selectedIndex++;
            EnsureVisible(visibleRows);
        }
    }

    internal void PageUp(int visibleRows)
    {
        _selectedIndex = Math.Max(0, _selectedIndex - visibleRows);
        EnsureVisible(visibleRows);
    }

    internal void PageDown(int visibleRows)
    {
        _selectedIndex = Math.Min(_filteredEntries.Count - 1, _selectedIndex + visibleRows);
        EnsureVisible(visibleRows);
    }

    /// <summary>
    /// Activates the selected entry. Returns the file path if a file was selected, null otherwise.
    /// </summary>
    internal string? Activate()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _filteredEntries.Count)
            return null;

        FileEntry entry = _filteredEntries[_selectedIndex];
        if (entry.IsDirectory)
        {
            EnterDirectory(entry.FullPath);
            return null;
        }

        return entry.FullPath;
    }

    /// <summary>
    /// Renders the file browser as ANSI-colored strings.
    /// </summary>
    internal string[] RenderRows(int terminalWidth, int terminalHeight)
    {
        // Header: 2 lines (path + separator), footer: 2 lines (filter + help)
        int headerLines = 2;
        int footerLines = 2;
        int listHeight = Math.Max(1, terminalHeight - headerLines - footerLines - 2);

        List<string> rows = [];

        // Header
        string dirDisplay = _currentDirectory.Length > terminalWidth - 6
            ? "…" + _currentDirectory[^(terminalWidth - 7)..]
            : _currentDirectory;
        rows.Add($"  📂 {dirDisplay}");
        rows.Add($"  {new string('─', Math.Min(terminalWidth - 4, 70))}");

        // Entries
        if (_filteredEntries.Count == 0)
        {
            rows.Add("  (empty)");
            for (int i = 1; i < listHeight; i++)
                rows.Add("");
        }
        else
        {
            for (int i = 0; i < listHeight; i++)
            {
                int entryIndex = _scrollOffset + i;
                if (entryIndex >= _filteredEntries.Count)
                {
                    rows.Add("");
                    continue;
                }

                FileEntry entry = _filteredEntries[entryIndex];
                bool selected = entryIndex == _selectedIndex;
                string prefix = selected ? " ▸ " : "   ";
                string icon = entry.IsDirectory ? "[DIR] " : "      ";
                string name = entry.Name;
                string size = entry.IsDirectory ? "" : FormatSize(entry.Size);

                int maxNameLen = terminalWidth - prefix.Length - icon.Length - size.Length - 4;
                if (name.Length > maxNameLen && maxNameLen > 3)
                    name = name[..(maxNameLen - 1)] + "…";

                string line = $"{prefix}{icon}{name}";
                if (size.Length > 0)
                    line = line.PadRight(terminalWidth - size.Length - 2) + size;

                rows.Add(line);
            }
        }

        // Footer
        string filterLine = _filter.Length > 0
            ? $"  Filter: {_filter}█"
            : "  Type to filter…";
        rows.Add($"  {new string('─', Math.Min(terminalWidth - 4, 70))}");
        rows.Add($"{filterLine}  │  Enter=open  Backspace=up  Esc=cancel  ({_filteredEntries.Count} items)");

        return rows.ToArray();
    }

    // ─── Internals ───

    private void ScanDirectory()
    {
        _allEntries.Clear();

        try
        {
            DirectoryInfo dir = new(_currentDirectory);

            foreach (DirectoryInfo sub in dir.EnumerateDirectories())
            {
                try
                {
                    _allEntries.Add(new FileEntry(sub.Name, sub.FullName, true, 0));
                }
                catch { }
            }

            foreach (FileInfo file in dir.EnumerateFiles())
            {
                try
                {
                    _allEntries.Add(new FileEntry(file.Name, file.FullName, false, file.Length));
                }
                catch { }
            }
        }
        catch { }

        // Sort: directories first (alphabetical), then files (alphabetical)
        _allEntries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrEmpty(_filter))
        {
            _filteredEntries = new List<FileEntry>(_allEntries);
        }
        else
        {
            _filteredEntries = _allEntries
                .Where(e => e.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _filteredEntries.Count - 1));
    }

    private void EnsureVisible(int visibleRows)
    {
        if (_selectedIndex < _scrollOffset)
            _scrollOffset = _selectedIndex;
        else if (_selectedIndex >= _scrollOffset + visibleRows)
            _scrollOffset = _selectedIndex - visibleRows + 1;
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}

/// <summary>
/// A single entry in the file browser listing.
/// </summary>
internal readonly record struct FileEntry(string Name, string FullPath, bool IsDirectory, long Size);
