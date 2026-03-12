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
#pragma warning disable IL2026, IL3050 // Terminal.Gui's Init uses reflection internally; assembly is trimmer-rooted
app.Init();
#pragma warning restore IL2026, IL3050

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
  private readonly View _welcomeView;
  private MenuItem? _wordWrapItem;
  private CheckBox? _wordWrapCheckBox;
  private MenuItem? _encodingItem;
  private OptionSelector? _encodingSelector;
  private MenuItem? _bprItem;
  private OptionSelector? _bprSelector;

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
    BorderStyle = LineStyle.None;

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

    // ─── Welcome view ───
    _welcomeView = BuildWelcomeView(menuBar);

    Add(menuBar, _welcomeView, _hexView, _textView, _statusBar);

    // Show welcome if no file is open
    bool hasFile = state.Document is not null;
    _welcomeView.Visible = !hasFile;
    _hexView.Visible = hasFile && state.ActiveView == ViewMode.Hex;
    _textView.Visible = hasFile && state.ActiveView == ViewMode.Text;

    // View-specific menu items
    UpdateViewMenuVisibility(state.ActiveView);

    // Wire state-changed events to update status bar
    _hexView.StateChanged += UpdateStatusBar;
    _textView.StateChanged += UpdateStatusBar;

    // Register command palette commands
    RegisterCommands();

    // ─── Popover overlays ───
    _findBar = new FindBar(state, StartSearch, FindNext, FindPrevious);
    _gotoBar = new GotoBar(state, o => _hexView.GotoOffset(o), l => _textView.GotoLine(l));
    _palettePopover = new CommandPalettePopover(palette, state,
        l => _textView.GotoLine(l), o => _hexView.GotoOffset(o));
    Initialized += RegisterPopovers;

    // ─── Application-level key bindings ───
    SetupAppKeyBindings();
  }

  /// <summary>Registers all popover overlays once the view is initialized.</summary>
  private void RegisterPopovers(object? sender, EventArgs args)
  {
    Initialized -= RegisterPopovers;
    _app.Popovers?.Register(_findBar);
    _app.Popovers?.Register(_gotoBar);
    _app.Popovers?.Register(_palettePopover);
  }

  private MenuBar BuildMenuBar()
  {
    // Word Wrap — CheckBox CommandView
    _wordWrapCheckBox = new CheckBox { Title = "_Word Wrap", CanFocus = false };
    _wordWrapCheckBox.Value = _state.WordWrap ? CheckState.Checked : CheckState.UnChecked;
    _wordWrapItem = new MenuItem { Title = "_Word Wrap", HelpText = "Toggle word wrap" };
    _wordWrapItem.CommandView = _wordWrapCheckBox;
    _wordWrapItem.Action += () => {
      if (_wordWrapCheckBox is not null)
        ApplyWordWrap(_wordWrapCheckBox.Value == CheckState.Checked);
    };

    // Encoding — OptionSelector CommandView
    _encodingSelector = new OptionSelector {
      Labels = ["UTF-8", "UTF-16 LE", "Windows-1252"],
      Values = [0, 1, 2],
      Value = (int)_state.Decoder.Encoding,
      CanFocus = true,
      Orientation = Orientation.Vertical,
    };
    _encodingItem = new MenuItem { Title = "_Encoding" };
    _encodingItem.CommandView = _encodingSelector;
    _encodingSelector.ValueChanged += (_, args) => {
      if (args.NewValue is int val)
        SwitchEncoding((TextEncoding)val);
    };

    // Bytes per Row — OptionSelector CommandView
    _bprSelector = new OptionSelector {
      Labels = ["Auto", "8", "16", "24", "32", "48", "64"],
      Values = [0, 8, 16, 24, 32, 48, 64],
      Value = _state.BytesPerRowSetting,
      CanFocus = true,
      Orientation = Orientation.Vertical,
    };
    _bprItem = new MenuItem { Title = "_Bytes/Row" };
    _bprItem.CommandView = _bprSelector;
    _bprSelector.ValueChanged += (_, args) => {
      if (args.NewValue is int val)
        SetBytesPerRow(val);
    };

    return new MenuBar([
        new MenuBarItem("_File", [
                new MenuItem("_Open...", Key.O.WithCtrl, () => ShowOpenDialog()) { HelpText = "Open a file" },
                new MenuItem("_Save", Key.S.WithCtrl, () => SaveFile()) { HelpText = "Save the file" },
                new MenuItem("Save _As...", "Save to a new path", () => ShowSaveAsDialog()),
                new MenuItem("_Quit", Key.Q.WithCtrl, () => GuardUnsavedChanges(() => _app.RequestStop())) { HelpText = "Exit Leviathan" },
            ]),
            new MenuBarItem("_View", [
                new MenuItem("_Hex View", Key.F5, () => SwitchView(ViewMode.Hex)) { HelpText = "Switch to hex view" },
                new MenuItem("_Text View", Key.F6, () => SwitchView(ViewMode.Text)) { HelpText = "Switch to text view" },
                _wordWrapItem,
                _encodingItem,
                _bprItem,
            ]),
            new MenuBarItem("_Navigate", [
                new MenuItem("_Go to Offset/Line...", Key.G.WithCtrl, () => _palettePopover.ShowGoto()) { HelpText = "Jump to offset or line" },
            ]),
            new MenuBarItem("_Search", [
                new MenuItem("_Find...", Key.F.WithCtrl, () => _findBar.ShowBar()) { HelpText = "Search in file" },
                new MenuItem("Find _Next", Key.F3, () => FindNext()) { HelpText = "Go to next match" },
                new MenuItem("Find _Previous", Key.F3.WithShift, () => FindPrevious()) { HelpText = "Go to previous match" },
            ]),
            new MenuBarItem("_Edit", [
                new MenuItem("_Copy", Key.C.WithCtrl, () => DoCopy()) { HelpText = "Copy selection" },
                new MenuItem("_Paste", Key.V.WithCtrl, () => DoPaste()) { HelpText = "Paste from clipboard" },
                new MenuItem("Select _All", Key.A.WithCtrl, () => DoSelectAll()) { HelpText = "Select entire file" },
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
      } else if (e == Key.G.WithCtrl) {
        _palettePopover.ShowGoto();
        e.Handled = true;
      } else if (e == Key.F6) {
        SwitchView(ViewMode.Text);
        e.Handled = true;
      }

      // MRU digit keys (1-9) when welcome screen is visible
      if (_welcomeView.Visible && !e.Handled) {
        int digit = e.KeyCode switch {
          KeyCode.D1 => 0, KeyCode.D2 => 1, KeyCode.D3 => 2,
          KeyCode.D4 => 3, KeyCode.D5 => 4, KeyCode.D6 => 5,
          KeyCode.D7 => 6, KeyCode.D8 => 7, KeyCode.D9 => 8,
          _ => -1
        };
        if (digit >= 0 && digit < _state.Settings.RecentFiles.Count) {
          string path = _state.Settings.RecentFiles[digit];
          if (File.Exists(path)) {
            _state.OpenFile(path);
            ShowFileViews();
          }
          e.Handled = true;
        }
      }
    };
  }

  // ─── View switching ───

  private void SwitchView(ViewMode mode)
  {
    _state.ActiveView = mode;
    _welcomeView.Visible = false;
    _hexView.Visible = mode == ViewMode.Hex;
    _textView.Visible = mode == ViewMode.Text;
    UpdateViewMenuVisibility(mode);

    if (mode == ViewMode.Hex)
      _hexView.SetFocus();
    else
      _textView.SetFocus();

    UpdateStatusBar();
    UpdateTitle();
  }

  /// <summary>Shows/hides menu items based on active view mode.</summary>
  private void UpdateViewMenuVisibility(ViewMode mode)
  {
    if (_wordWrapItem is not null)
      _wordWrapItem.Visible = mode == ViewMode.Text;
    if (_bprItem is not null)
      _bprItem.Visible = mode == ViewMode.Hex;
    // Encoding is visible in both modes
  }

  /// <summary>Hides the welcome screen and shows the file views.</summary>
  private void ShowFileViews()
  {
    _welcomeView.Visible = false;
    _hexView.Visible = _state.ActiveView == ViewMode.Hex;
    _textView.Visible = _state.ActiveView == ViewMode.Text;
    UpdateViewMenuVisibility(_state.ActiveView);
    UpdateTitle();
    UpdateStatusBar();
    _hexView.SetNeedsDraw();
    _textView.SetNeedsDraw();
  }

  // ─── File operations ───

  private void ShowOpenDialog()
  {
    GuardUnsavedChanges(() => {
      OpenDialog openDlg = new() {
        Title = "Open File",
        OpenMode = OpenMode.File,
        Width = Dim.Percent(85),
        Height = Dim.Percent(80),
      };
      _app.Run(openDlg);

      if (!openDlg.Canceled && openDlg.FilePaths.Count > 0) {
        string path = openDlg.FilePaths[0];
        if (File.Exists(path)) {
          _state.OpenFile(path);
          ShowFileViews();
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
      Width = Dim.Percent(85),
      Height = Dim.Percent(80),
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
        bool caseSensitive = _state.FindHexMode || _state.FindCaseSensitive;
        foreach (SearchResult r in SearchEngine.FindAll(_state.Document, pattern, caseSensitive)) {
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
      _app.Invoke(() => {
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
      _app.Clipboard?.TrySetClipboardData(text);
  }

  private void DoPaste()
  {
    if (_app.Clipboard is { } clipboard && clipboard.TryGetClipboardData(out string? text) && text is not null) {
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
    bool newState = !_state.WordWrap;
    ApplyWordWrap(newState);
    // Sync the CheckBox if toggled from palette/keybind
    if (_wordWrapCheckBox is not null)
      _wordWrapCheckBox.Value = newState ? CheckState.Checked : CheckState.UnChecked;
  }

  private void ApplyWordWrap(bool enabled)
  {
    _state.WordWrap = enabled;
    _state.Settings.WordWrap = enabled;
    _state.Settings.Save();
    _textView.SetNeedsDraw();
    UpdateStatusBar();
  }

  // ─── Encoding ───

  private void SwitchEncoding(TextEncoding encoding)
  {
    _state.SwitchEncoding(encoding);
    // Sync the OptionSelector if called from palette/keybind
    if (_encodingSelector is not null)
      _encodingSelector.Value = (int)encoding;
    _hexView.SetNeedsDraw();
    _textView.SetNeedsDraw();
    UpdateStatusBar();
  }

  // ─── Bytes per row ───

  private void SetBytesPerRow(int value)
  {
    _state.BytesPerRowSetting = value;
    _state.Settings.BytesPerRow = value;
    _state.Settings.Save();
    // Sync the OptionSelector if called from palette/keybind
    if (_bprSelector is not null)
      _bprSelector.Value = value;
    _hexView.SetNeedsDraw();
    UpdateStatusBar();
  }

  // ─── Welcome view ───

  private View BuildWelcomeView(MenuBar menuBar)
  {
    View view = new() {
      X = 0,
      Y = Pos.Bottom(menuBar),
      Width = Dim.Fill(),
      Height = Dim.Fill(1),
      CanFocus = true,
    };

    view.Initialized += (_, _) => {
      view.SetNeedsDraw();
    };

    view.DrawingContent += (_, _) => {
      view.SetAttributeForRole(VisualRole.Normal);
      int vpW = view.Viewport.Width;
      int vpH = view.Viewport.Height;

      // Clear
      for (int row = 0; row < vpH; row++) {
        view.Move(0, row);
        for (int c = 0; c < vpW; c++)
          view.AddRune(' ');
      }

      int y = 2;
      Attribute titleAttr = new(new Color(208, 135, 46), new Color(StandardColor.Black));
      Attribute normalAttr = new(new Color(StandardColor.White), new Color(StandardColor.Black));
      Attribute hintAttr = new(new Color(100, 130, 160), new Color(StandardColor.Black));
      Attribute mruAttr = new(new Color(StandardColor.Yellow), new Color(StandardColor.Black));

      void DrawCentered(string text, Attribute attr)
      {
        int x = Math.Max(0, (vpW - text.Length) / 2);
        view.Move(x, y);
        view.SetAttribute(attr);
        foreach (char c in text)
          view.AddRune(c);
        y++;
      }

      DrawCentered("Leviathan", titleAttr);
      DrawCentered("Large File Editor", titleAttr);
      y++;
      DrawCentered("Ctrl+O  Open file", hintAttr);
      DrawCentered("Ctrl+P  Command palette", hintAttr);
      y++;

      List<string> recent = _state.Settings.RecentFiles;
      if (recent.Count > 0) {
        DrawCentered("Recent files:", normalAttr);
        y++;
        // Left-align MRU entries at a fixed indent
        int indent = Math.Max(2, (vpW / 2) - 30);
        for (int i = 0; i < Math.Min(9, recent.Count); i++) {
          string entry = $"[{i + 1}]  {recent[i]}";
          view.Move(indent, y);
          view.SetAttribute(mruAttr);
          foreach (char c in entry)
            view.AddRune(c);
          y++;
        }
      }
    };

    return view;
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

    // Word wrap — dynamic check indicator
    _palette.RegisterCommand("View",
        () => _state.WordWrap ? "✓ Word Wrap" : "  Word Wrap",
        "", () => ToggleWordWrap());

    // Bytes per row — dynamic radio indicators
    foreach (int bpr in new[] { 0, 8, 16, 24, 32, 48, 64 }) {
      int val = bpr;
      string label = bpr == 0 ? "Auto" : $"{bpr}";
      _palette.RegisterCommand("Bytes/Row",
          () => _state.BytesPerRowSetting == val ? $"● {label}" : $"  {label}",
          "", () => SetBytesPerRow(val));
    }

    _palette.RegisterCommand("Navigate", "Go to Offset/Line", "Ctrl+G", () => _palettePopover.ShowGoto());
    _palette.RegisterCommand("Search", "Find", "Ctrl+F", () => _findBar.ShowBar());
    _palette.RegisterCommand("Search", "Find Next", "F3", () => FindNext());
    _palette.RegisterCommand("Search", "Find Previous", "Shift+F3", () => FindPrevious());
    _palette.RegisterCommand("Edit", "Copy", "Ctrl+C", () => DoCopy());
    _palette.RegisterCommand("Edit", "Paste", "Ctrl+V", () => DoPaste());
    _palette.RegisterCommand("Edit", "Select All", "Ctrl+A", () => DoSelectAll());

    // Encoding — dynamic radio indicators
    _palette.RegisterCommand("Encoding",
        () => _state.Decoder.Encoding == TextEncoding.Utf8 ? "● UTF-8" : "  UTF-8",
        "", () => SwitchEncoding(TextEncoding.Utf8));
    _palette.RegisterCommand("Encoding",
        () => _state.Decoder.Encoding == TextEncoding.Utf16Le ? "● UTF-16 LE" : "  UTF-16 LE",
        "", () => SwitchEncoding(TextEncoding.Utf16Le));
    _palette.RegisterCommand("Encoding",
        () => _state.Decoder.Encoding == TextEncoding.Windows1252 ? "● Windows-1252" : "  Windows-1252",
        "", () => SwitchEncoding(TextEncoding.Windows1252));

    // MRU entries in the command palette
    foreach (string recent in _state.Settings.RecentFiles) {
      string path = recent;
      _palette.RegisterCommand("File", $"Open: {Path.GetFileName(path)}", "", 
          () => GuardUnsavedChanges(() => { _state.OpenFile(path); ShowFileViews(); }));
    }
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
