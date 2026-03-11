using System.Collections.ObjectModel;
using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.TUI2;
using Leviathan.TUI2.Views;
using Leviathan.TUI2.Widgets;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// ─── Application state ───
AppState state = new();
LeviathanHexView hexView = new(state);
LeviathanTextView textView = new(state);
CommandPalette palette = new();

// Apply persisted settings
state.BytesPerRowSetting = state.Settings.BytesPerRow;
state.WordWrap = state.Settings.WordWrap;

// Open file from CLI argument
if (args.Length > 0 && File.Exists(args[0]))
    state.OpenFile(args[0]);

// ─── Terminal.Gui lifecycle ───
using IApplication app = Application.Create();
app.Init();

using MainWindow mainWindow = new(app, state, hexView, textView, palette);
app.Run(mainWindow);

// Clean up
state.CancelSearch();
state.Document?.Dispose();

// ═══════════════════════════════════════════════════════════════════
// MainWindow — the top-level window hosting everything
// ═══════════════════════════════════════════════════════════════════

internal sealed class MainWindow : Window
{
    private readonly IApplication _app;
    private readonly AppState _state;
    private readonly LeviathanHexView _hexView;
    private readonly LeviathanTextView _textView;
    private readonly CommandPalette _palette;
    private readonly StatusBar _statusBar;

    internal MainWindow(
        IApplication app,
        AppState state,
        LeviathanHexView hexView,
        LeviathanTextView textView,
        CommandPalette palette)
    {
        _app = app;
        _state = state;
        _hexView = hexView;
        _textView = textView;
        _palette = palette;

        Title = "Leviathan — Large File Editor";

        // ─── Menu bar ───
        MenuBar menuBar = BuildMenuBar();

        // ─── Content views ───
        _hexView.X = 0;
        _hexView.Y = Pos.Bottom(menuBar);
        _hexView.Width = Dim.Fill();
        _hexView.Height = Dim.Fill(1); // Leave 1 row for status bar
        _hexView.Visible = state.ActiveView == ViewMode.Hex;

        _textView.X = 0;
        _textView.Y = Pos.Bottom(menuBar);
        _textView.Width = Dim.Fill();
        _textView.Height = Dim.Fill(1);
        _textView.Visible = state.ActiveView == ViewMode.Text;

        // ─── Status bar ───
        _statusBar = new StatusBar([
            new Shortcut(Key.F5, "Hex", null),
            new Shortcut(Key.F6, "Text", null),
            new Shortcut(Key.O.WithCtrl, "Open", null),
            new Shortcut(Key.S.WithCtrl, "Save", null),
        ]);

        Add(menuBar, _hexView, _textView, _statusBar);

        // Wire state-changed events to update status bar
        _hexView.StateChanged += UpdateStatusBar;
        _textView.StateChanged += UpdateStatusBar;

        // Register command palette commands
        RegisterCommands();

        // ─── Application-level key bindings ───
        SetupAppKeyBindings();
    }

    private MenuBar BuildMenuBar()
    {
        return new MenuBar([
            new MenuBarItem("_File", [
                new MenuItem("_Open...", "Open a file", () => ShowOpenDialog(), Key.O.WithCtrl),
                new MenuItem("_Save", "Save the file", () => SaveFile(), Key.S.WithCtrl),
                new MenuItem("Save _As...", "Save to a new path", () => ShowSaveAsDialog()),
                new MenuItem("_Quit", "Exit Leviathan", () => GuardUnsavedChanges(() => _app.RequestStop()), Key.Q.WithCtrl),
            ]),
            new MenuBarItem("_View", [
                new MenuItem("_Hex View", "Switch to hex view", () => SwitchView(ViewMode.Hex), Key.F5),
                new MenuItem("_Text View", "Switch to text view", () => SwitchView(ViewMode.Text), Key.F6),
                new MenuItem("_Word Wrap", "Toggle word wrap", () => ToggleWordWrap()),
                new MenuItem("Encoding: _UTF-8", null, () => SwitchEncoding(TextEncoding.Utf8)),
                new MenuItem("Encoding: UTF-_16 LE", null, () => SwitchEncoding(TextEncoding.Utf16Le)),
                new MenuItem("Encoding: _Windows-1252", null, () => SwitchEncoding(TextEncoding.Windows1252)),
            ]),
            new MenuBarItem("_Navigate", [
                new MenuItem("_Go to Offset/Line...", "Jump to offset or line", () => ShowGotoDialog(), Key.G.WithCtrl),
            ]),
            new MenuBarItem("_Search", [
                new MenuItem("_Find...", "Search in file", () => ShowFindDialog(), Key.F.WithCtrl),
                new MenuItem("Find _Next", "Go to next match", () => FindNext(), Key.F3),
                new MenuItem("Find _Previous", "Go to previous match", () => FindPrevious(), Key.F3.WithShift),
            ]),
            new MenuBarItem("_Edit", [
                new MenuItem("_Copy", "Copy selection", () => DoCopy(), Key.C.WithCtrl),
                new MenuItem("_Paste", "Paste from clipboard", () => DoPaste(), Key.V.WithCtrl),
                new MenuItem("Select _All", "Select entire file", () => DoSelectAll(), Key.A.WithCtrl),
            ]),
        ]);
    }

    private void SetupAppKeyBindings()
    {
        // F5/F6 view switching
        AddCommand(Command.Refresh, () => { SwitchView(ViewMode.Hex); return true; });
        KeyBindings.Add(Key.F5, Command.Refresh);

        // Command palette
        KeyDown += (_, e) =>
        {
            if (e.IsCtrl && e.NoCtrl.KeyCode == Terminal.Gui.Drivers.KeyCode.P)
            {
                ShowCommandPalette();
                e.Handled = true;
            }
            else if (e.KeyCode == Terminal.Gui.Drivers.KeyCode.F6)
            {
                SwitchView(ViewMode.Text);
                e.Handled = true;
            }
        };
    }

    // ─── View switching ───

    private void SwitchView(ViewMode mode)
    {
        _state.ActiveView = mode;
        _hexView.Visible = mode == ViewMode.Hex;
        _textView.Visible = mode == ViewMode.Text;

        if (mode == ViewMode.Hex)
            _hexView.SetFocus();
        else
            _textView.SetFocus();

        UpdateStatusBar();
        UpdateTitle();
    }

    // ─── File operations ───

    private void ShowOpenDialog()
    {
        GuardUnsavedChanges(() =>
        {
            OpenDialog openDlg = new()
            {
                Title = "Open File",
                OpenMode = OpenMode.File,
            };
            _app.Run(openDlg);

            if (!openDlg.Canceled && openDlg.FilePaths.Count > 0)
            {
                string path = openDlg.FilePaths[0];
                if (File.Exists(path))
                {
                    _state.OpenFile(path);
                    UpdateTitle();
                    UpdateStatusBar();
                    _hexView.SetNeedsDraw();
                    _textView.SetNeedsDraw();
                }
            }
            openDlg.Dispose();
        });
    }

    private void SaveFile()
    {
        if (_state.Document is null || _state.CurrentFilePath is null)
        {
            ShowSaveAsDialog();
            return;
        }

        if (_state.TrySave(_state.CurrentFilePath, out string? error))
        {
            UpdateTitle();
            UpdateStatusBar();
        }
        else
        {
            ShowSaveErrorDialog(error ?? "Unknown error");
        }
    }

    private void ShowSaveAsDialog()
    {
        if (_state.Document is null) return;

        SaveDialog saveDlg = new()
        {
            Title = "Save As",
        };
        _app.Run(saveDlg);

        if (!saveDlg.Canceled && saveDlg.FileName is not null)
        {
            if (_state.TrySave(saveDlg.FileName, out string? error))
            {
                UpdateTitle();
                UpdateStatusBar();
            }
            else
            {
                ShowSaveErrorDialog(error ?? "Unknown error");
            }
        }
        saveDlg.Dispose();
    }

    private void ShowSaveErrorDialog(string message)
    {
        MessageBox.ErrorQuery(_app, "Save Error", message, "OK");
    }

    private void GuardUnsavedChanges(Action action)
    {
        if (!_state.IsModified)
        {
            action();
            return;
        }

        int? result = MessageBox.Query(
            _app,
            "Unsaved Changes",
            "You have unsaved changes. Save before proceeding?",
            "Save", "Don't Save", "Cancel");

        switch (result)
        {
            case 0: // Save
                SaveFile();
                if (!_state.IsModified) // Save succeeded
                    action();
                break;
            case 1: // Don't Save
                action();
                break;
            // 2 = Cancel → do nothing
        }
    }

    // ─── Go to dialog ───

    private void ShowGotoDialog()
    {
        if (_state.Document is null) return;

        Dialog gotoDialog = new() { Title = "Go to Offset/Line", Width = 50, Height = 8 };

        Label label = new()
        {
            Text = _state.ActiveView == ViewMode.Hex
                ? "Hex offset (e.g. 0x1A3F or 6719):"
                : "Line number:",
            X = 1,
            Y = 1,
        };

        TextField inputField = new()
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Text = "",
        };

        gotoDialog.Add(label, inputField);
        gotoDialog.AddButton(new Button() { Text = "_Cancel" });
        gotoDialog.AddButton(new Button() { Text = "_Go", IsDefault = true });

        _app.Run(gotoDialog);

        if (!gotoDialog.Canceled)
        {
            string input = inputField.Text?.Trim() ?? "";
            if (_state.ActiveView == ViewMode.Hex)
            {
                if (TryParseOffset(input, out long offset))
                    _hexView.GotoOffset(offset);
            }
            else
            {
                if (long.TryParse(input, out long lineNum))
                    _textView.GotoLine(lineNum);
            }
        }
        gotoDialog.Dispose();
    }

    // ─── Find dialog ───

    private void ShowFindDialog()
    {
        if (_state.Document is null) return;

        Dialog findDialog = new()
        {
            Title = "Find",
            Width = 60,
            Height = 10,
        };

        Label modeLabel = new()
        {
            Text = _state.FindHexMode ? "Mode: HEX" : "Mode: TEXT",
            X = 1,
            Y = 1,
        };

        CheckBox hexModeCheck = new()
        {
            Text = "_Hex mode",
            X = 1,
            Y = 2,
        };
        hexModeCheck.Value = _state.FindHexMode ? CheckState.Checked : CheckState.UnChecked;
        hexModeCheck.ValueChanged += (_, e) =>
        {
            _state.FindHexMode = e.NewValue == CheckState.Checked;
            modeLabel.Text = _state.FindHexMode ? "Mode: HEX" : "Mode: TEXT";
        };

        CheckBox caseSensitiveCheck = new()
        {
            Text = "_Case sensitive",
            X = Pos.Right(hexModeCheck) + 2,
            Y = 2,
        };
        caseSensitiveCheck.Value = _state.FindCaseSensitive ? CheckState.Checked : CheckState.UnChecked;
        caseSensitiveCheck.ValueChanged += (_, e) =>
        {
            _state.FindCaseSensitive = e.NewValue == CheckState.Checked;
        };

        Label queryLabel = new()
        {
            Text = "Search for:",
            X = 1,
            Y = 3,
        };

        TextField queryField = new()
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Text = _state.FindInput,
        };

        findDialog.Add(modeLabel, hexModeCheck, caseSensitiveCheck, queryLabel, queryField);
        findDialog.AddButton(new Button() { Text = "_Cancel" });
        findDialog.AddButton(new Button() { Text = "_Find", IsDefault = true });

        _app.Run(findDialog);

        if (!findDialog.Canceled)
        {
            string query = queryField.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(query))
            {
                _state.FindInput = query;
                _state.Settings.AddFindHistory(query);
                StartSearch(query);
            }
        }
        findDialog.Dispose();
    }

    private void StartSearch(string query)
    {
        if (_state.Document is null) return;

        _state.CancelSearch();
        _state.SearchResults.Clear();
        _state.CurrentMatchIndex = -1;
        _state.SearchStatus = "Searching…";
        _state.IsSearching = true;
        UpdateStatusBar();

        CancellationTokenSource cts = new();
        _state.SearchCts = cts;

        byte[]? pattern;
        if (_state.FindHexMode)
        {
            pattern = ParseHexPattern(query);
            if (pattern is null || pattern.Length == 0)
            {
                _state.SearchStatus = "Invalid hex pattern";
                _state.IsSearching = false;
                UpdateStatusBar();
                return;
            }
        }
        else
        {
            pattern = System.Text.Encoding.UTF8.GetBytes(query);
        }

        Document doc = _state.Document;
        Task.Run(() =>
        {
            try
            {
                List<SearchResult> results = SearchEngine.FindAll(doc, pattern).ToList();
                if (!cts.Token.IsCancellationRequested)
                {
                    _state.SearchResults.Clear();
                    _state.SearchResults.AddRange(results);
                    _state.CurrentMatchIndex = results.Count > 0 ? 0 : -1;
                    _state.SearchStatus = results.Count > 0
                        ? $"{results.Count} match{(results.Count > 1 ? "es" : "")}"
                        : "No matches";
                    _state.IsSearching = false;

                    // Navigate to first match
                    if (results.Count > 0)
                        NavigateToMatch(0);
                }
            }
            catch (OperationCanceledException)
            {
                _state.SearchStatus = "Search cancelled";
                _state.IsSearching = false;
            }

            // Schedule UI update on main thread
            _app.Invoke(() =>
            {
                UpdateStatusBar();
                _hexView.SetNeedsDraw();
                _textView.SetNeedsDraw();
            });
        }, cts.Token);
    }

    private void FindNext()
    {
        if (_state.SearchResults.Count == 0) return;
        int next = (_state.CurrentMatchIndex + 1) % _state.SearchResults.Count;
        NavigateToMatch(next);
    }

    private void FindPrevious()
    {
        if (_state.SearchResults.Count == 0) return;
        int prev = (_state.CurrentMatchIndex - 1 + _state.SearchResults.Count) % _state.SearchResults.Count;
        NavigateToMatch(prev);
    }

    private void NavigateToMatch(int index)
    {
        if (index < 0 || index >= _state.SearchResults.Count) return;
        _state.CurrentMatchIndex = index;
        SearchResult match = _state.SearchResults[index];

        if (_state.ActiveView == ViewMode.Hex)
            _hexView.GotoOffset(match.Offset);
        else
            _textView.GotoOffset(match.Offset);

        UpdateStatusBar();
    }

    // ─── Copy/Paste ───

    private void DoCopy()
    {
        string? text = _state.ActiveView == ViewMode.Hex
            ? _hexView.CopySelection()
            : _textView.CopySelection();

        if (text is not null)
            _app.Clipboard?.TrySetClipboardData(text);
    }

    private void DoPaste()
    {
        if (_app.Clipboard is { } clipboard && clipboard.TryGetClipboardData(out string? text) && text is not null)
        {
            if (_state.ActiveView == ViewMode.Hex)
                _hexView.Paste(text);
            else
                _textView.Paste(text);
        }
    }

    private void DoSelectAll()
    {
        if (_state.ActiveView == ViewMode.Hex)
            _hexView.SelectAll();
        else
            _textView.SelectAll();
    }

    // ─── Word wrap ───

    private void ToggleWordWrap()
    {
        _state.WordWrap = !_state.WordWrap;
        _state.Settings.WordWrap = _state.WordWrap;
        _state.Settings.Save();
        _textView.SetNeedsDraw();
        UpdateStatusBar();
    }

    // ─── Encoding ───

    private void SwitchEncoding(TextEncoding encoding)
    {
        _state.SwitchEncoding(encoding);
        _hexView.SetNeedsDraw();
        _textView.SetNeedsDraw();
        UpdateStatusBar();
    }

    // ─── Command palette ───

    private void ShowCommandPalette()
    {
        _palette.Reset();

        Dialog paletteDialog = new()
        {
            Title = "Command Palette",
            Width = Dim.Percent(60),
            Height = Dim.Percent(50),
        };

        TextField queryField = new()
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Text = "",
        };

        ListView listView = new()
        {
            X = 0,
            Y = Pos.Bottom(queryField),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        UpdatePaletteList(listView);

        queryField.HasFocusChanged += (_, __) =>
        {
            // Keep focus on queryField
        };

        queryField.KeyDown += (_, e) =>
        {
            if (e.KeyCode == KeyCode.CursorDown)
            {
                _palette.MoveDown();
                listView.SelectedItem = _palette.SelectedIndex;
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.CursorUp)
            {
                _palette.MoveUp();
                listView.SelectedItem = _palette.SelectedIndex;
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Enter)
            {
                PaletteCommand? cmd = _palette.GetSelected();
                paletteDialog.RequestStop();
                cmd?.Execute();
                e.Handled = true;
            }
            else if (e.KeyCode == KeyCode.Esc)
            {
                paletteDialog.RequestStop();
                e.Handled = true;
            }
        };

        // Update filtering as user types - check on each key event
        queryField.KeyUp += (_, _) =>
        {
            string newQuery = queryField.Text?.ToString() ?? "";
            if (newQuery != _palette.Query)
            {
                _palette.Query = newQuery;
                UpdatePaletteList(listView);
            }
        };

        paletteDialog.Add(queryField, listView);
        _app.Run(paletteDialog);
        paletteDialog.Dispose();
    }

    private void UpdatePaletteList(ListView listView)
    {
        ObservableCollection<string> items = new(
            _palette.FilteredCommands
            .Select(c => $"[{c.Category}] {c.Name}  {c.Shortcut}"));
        listView.SetSource(items);
        listView.SelectedItem = _palette.SelectedIndex;
    }

    private void RegisterCommands()
    {
        _palette.RegisterCommand("File", "Open File", "Ctrl+O", () => ShowOpenDialog());
        _palette.RegisterCommand("File", "Save", "Ctrl+S", () => SaveFile());
        _palette.RegisterCommand("File", "Save As...", "", () => ShowSaveAsDialog());
        _palette.RegisterCommand("File", "Quit", "Ctrl+Q", () => GuardUnsavedChanges(() => _app.RequestStop()));
        _palette.RegisterCommand("View", "Hex View", "F5", () => SwitchView(ViewMode.Hex));
        _palette.RegisterCommand("View", "Text View", "F6", () => SwitchView(ViewMode.Text));
        _palette.RegisterCommand("View", "Toggle Word Wrap", "", () => ToggleWordWrap());
        _palette.RegisterCommand("Navigate", "Go to Offset/Line", "Ctrl+G", () => ShowGotoDialog());
        _palette.RegisterCommand("Search", "Find", "Ctrl+F", () => ShowFindDialog());
        _palette.RegisterCommand("Search", "Find Next", "F3", () => FindNext());
        _palette.RegisterCommand("Search", "Find Previous", "Shift+F3", () => FindPrevious());
        _palette.RegisterCommand("Edit", "Copy", "Ctrl+C", () => DoCopy());
        _palette.RegisterCommand("Edit", "Paste", "Ctrl+V", () => DoPaste());
        _palette.RegisterCommand("Edit", "Select All", "Ctrl+A", () => DoSelectAll());
        _palette.RegisterCommand("Encoding", "UTF-8", "", () => SwitchEncoding(TextEncoding.Utf8));
        _palette.RegisterCommand("Encoding", "UTF-16 LE", "", () => SwitchEncoding(TextEncoding.Utf16Le));
        _palette.RegisterCommand("Encoding", "Windows-1252", "", () => SwitchEncoding(TextEncoding.Windows1252));
    }

    // ─── Status bar & title ───

    private void UpdateStatusBar()
    {
        if (_state.Document is null)
        {
            Title = "Leviathan — Large File Editor";
            return;
        }

        UpdateTitle();
        SetNeedsDraw();
    }

    private void UpdateTitle()
    {
        if (_state.CurrentFilePath is null)
        {
            Title = "Leviathan — Large File Editor";
            return;
        }

        string fileName = Path.GetFileName(_state.CurrentFilePath);
        string modified = _state.IsModified ? "● " : "";
        string viewName = _state.ActiveView == ViewMode.Hex ? "HEX" : "TEXT";
        string encoding = _state.Decoder.Encoding.ToString();
        long cursor = _state.CurrentCursorOffset;
        string searchInfo = _state.SearchResults.Count > 0
            ? $" | {_state.CurrentMatchIndex + 1}/{_state.SearchResults.Count} matches"
            : _state.IsSearching ? " | Searching…" : "";

        Title = $"{modified}{fileName} — {viewName} | {FormatFileSize(_state.FileLength)} | Offset: 0x{cursor:X} ({cursor}){searchInfo} | {encoding}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ─── Helpers ───

    private static bool TryParseOffset(string input, out long offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        input = input.Trim();
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(input[2..], System.Globalization.NumberStyles.HexNumber, null, out offset);
        }

        // Try hex if it contains hex chars
        if (input.Any(c => c is >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            return long.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out offset);

        return long.TryParse(input, out offset);
    }

    private static byte[]? ParseHexPattern(string text)
    {
        List<byte> result = [];
        int nibbleCount = 0;
        int current = 0;

        foreach (char c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '-' or ':')
            {
                if (nibbleCount == 1) return null;
                continue;
            }

            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };

            if (digit < 0) return null;

            current = (current << 4) | digit;
            nibbleCount++;

            if (nibbleCount == 2)
            {
                result.Add((byte)current);
                current = 0;
                nibbleCount = 0;
            }
        }

        if (nibbleCount != 0) return null;
        return result.ToArray();
    }
}
