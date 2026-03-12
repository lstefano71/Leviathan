using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.TUI2;
using Leviathan.TUI2.Views;
using Leviathan.TUI2.Widgets;

using Terminal.Gui.App;
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
  private readonly FindBar _findBar;
  private readonly GotoBar _gotoBar;
  private readonly CommandPalettePopover _palettePopover;

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

    // ─── Popover overlays ───
    _findBar = new FindBar(state, StartSearch, FindNext, FindPrevious);
    _gotoBar = new GotoBar(state, o => _hexView.GotoOffset(o), l => _textView.GotoLine(l));
    _palettePopover = new CommandPalettePopover(palette);
    Initialized += RegisterPopovers;

    // ─── Application-level key bindings ───
    SetupAppKeyBindings();
  }

  /// <summary>Registers all popover overlays once the view is initialized.</summary>
  private void RegisterPopovers(object? sender, EventArgs args)
  {
    Initialized -= RegisterPopovers;
    _app.Popovers.Register(_findBar);
    _app.Popovers.Register(_gotoBar);
    _app.Popovers.Register(_palettePopover);
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
                new MenuItem("_Go to Offset/Line...", "Jump to offset or line", () => _gotoBar.ShowBar(), Key.G.WithCtrl),
            ]),
            new MenuBarItem("_Search", [
                new MenuItem("_Find...", "Search in file", () => _findBar.ShowBar(), Key.F.WithCtrl),
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

    KeyDown += (_, e) => {
      if (e == Key.P.WithCtrl) {
        _palettePopover.ShowPalette();
        e.Handled = true;
      } else if (e == Key.F6) {
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
    GuardUnsavedChanges(() => {
      OpenDialog openDlg = new() {
        Title = "Open File",
        OpenMode = OpenMode.File,
      };
      _app.Run(openDlg);

      if (!openDlg.Canceled && openDlg.FilePaths.Count > 0) {
        string path = openDlg.FilePaths[0];
        if (File.Exists(path)) {
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
    if (_state.Document is null || _state.CurrentFilePath is null) {
      ShowSaveAsDialog();
      return;
    }

    if (_state.TrySave(_state.CurrentFilePath, out string? error)) {
      UpdateTitle();
      UpdateStatusBar();
    } else {
      ShowSaveErrorDialog(error ?? "Unknown error");
    }
  }

  private void ShowSaveAsDialog()
  {
    if (_state.Document is null) return;

    SaveDialog saveDlg = new() {
      Title = "Save As",
    };
    _app.Run(saveDlg);

    if (!saveDlg.Canceled && saveDlg.FileName is not null) {
      if (_state.TrySave(saveDlg.FileName, out string? error)) {
        UpdateTitle();
        UpdateStatusBar();
      } else {
        ShowSaveErrorDialog(error ?? "Unknown error");
      }
    }
    saveDlg.Dispose();
  }

  private void ShowSaveErrorDialog(string message)
  {
    MessageBox.ErrorQuery(App!, "Save Error", message, "OK");
  }

  private void GuardUnsavedChanges(Action action)
  {
    if (!_state.IsModified) {
      action();
      return;
    }

    int result = MessageBox.Query(
        App!,
        "Unsaved Changes",
        "You have unsaved changes. Save before proceeding?",
        "Save", "Don't Save", "Cancel") ?? -1;

    switch (result) {
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

  // ─── Search ───

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
    if (_state.FindHexMode) {
      pattern = ParseHexPattern(query);
      if (pattern is null || pattern.Length == 0) {
        _state.SearchStatus = "Invalid hex pattern";
        _state.IsSearching = false;
        UpdateStatusBar();
        return;
      }
    } else {
      pattern = System.Text.Encoding.UTF8.GetBytes(query);
    }

    Task.Run(() => {
      try {
        List<SearchResult> results = [];
        foreach (SearchResult r in SearchEngine.FindAll(_state.Document, pattern)) {
          if (cts.Token.IsCancellationRequested) break;
          results.Add(r);
        }
        if (!cts.Token.IsCancellationRequested) {
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
      } catch (OperationCanceledException) {
        _state.SearchStatus = "Search cancelled";
        _state.IsSearching = false;
      }

      // Schedule UI update on main thread
      Application.Invoke(() => {
        UpdateStatusBar();
        _findBar.UpdateStatus();
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
      Clipboard.TrySetClipboardData(text);
  }

  private void DoPaste()
  {
    if (Clipboard.TryGetClipboardData(out string? text) && text is not null) {
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

  private void RegisterCommands()
  {
    _palette.RegisterCommand("File", "Open File", "Ctrl+O", () => ShowOpenDialog());
    _palette.RegisterCommand("File", "Save", "Ctrl+S", () => SaveFile());
    _palette.RegisterCommand("File", "Save As...", "", () => ShowSaveAsDialog());
    _palette.RegisterCommand("File", "Quit", "Ctrl+Q", () => GuardUnsavedChanges(() => _app.RequestStop()));
    _palette.RegisterCommand("View", "Hex View", "F5", () => SwitchView(ViewMode.Hex));
    _palette.RegisterCommand("View", "Text View", "F6", () => SwitchView(ViewMode.Text));
    _palette.RegisterCommand("View", "Toggle Word Wrap", "", () => ToggleWordWrap());
    _palette.RegisterCommand("Navigate", "Go to Offset/Line", "Ctrl+G", () => _gotoBar.ShowBar());
    _palette.RegisterCommand("Search", "Find", "Ctrl+F", () => _findBar.ShowBar());
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
    if (_state.Document is null) {
      Title = "Leviathan — Large File Editor";
      return;
    }

    UpdateTitle();
    SetNeedsDraw();
  }

  private void UpdateTitle()
  {
    if (_state.CurrentFilePath is null) {
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

  private static byte[]? ParseHexPattern(string text)
  {
    List<byte> result = [];
    int nibbleCount = 0;
    int current = 0;

    foreach (char c in text) {
      if (c is ' ' or '\t' or '\r' or '\n' or '-' or ':') {
        if (nibbleCount == 1) return null;
        continue;
      }

      int digit = c switch {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
      };

      if (digit < 0) return null;

      current = (current << 4) | digit;
      nibbleCount++;

      if (nibbleCount == 2) {
        result.Add((byte)current);
        current = 0;
        nibbleCount = 0;
      }
    }

    if (nibbleCount != 0) return null;
    return result.ToArray();
  }
}
