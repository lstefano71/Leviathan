using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.TUI2;
using Leviathan.TUI2.Views;
using Leviathan.TUI2.Widgets;

using System.Reflection;

using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// ─── Application state (no Terminal.Gui dependency) ───
AppState state = new();
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

// Disable the default Esc -> Quit binding
app.Keyboard.KeyBindings.Remove(app.Keyboard.QuitKey);

// Views must be created after app.Init() so Terminal.Gui's binding registries and
// scheme infrastructure are fully initialised before the views' constructors run.
LeviathanHexView hexView = new(state);
LeviathanTextView textView = new(state);
LeviathanCsvView csvView = new(state);

using MainWindow mainWindow = new(app, state, hexView, textView, csvView, palette);
app.Run(mainWindow);

// Clean up
state.CancelSearch();
state.CsvRowIndexer?.Dispose();
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
  private readonly LeviathanCsvView _csvView;
  private readonly CommandPalette _palette;
  private readonly StatusBar _statusBar;
  private readonly Label _statusFileLabel;
  private readonly Label _statusInfoLabel;
  private readonly FindBar _findBar;
  private readonly GotoBar _gotoBar;
  private readonly CommandPalettePopover _palettePopover;
  private readonly View _welcomeView;
  private readonly MenuBar _menuBar;
  private CsvSettingsBar? _csvSettingsBar;
  private MenuItem? _wordWrapItem;
  private CheckBox? _wordWrapCheckBox;
  private MenuItem? _encodingItem;
  private OptionSelector? _encodingSelector;
  private MenuItem? _bprItem;
  private OptionSelector? _bprSelector;
  private MenuItem? _csvSettingsItem;
  private MenuBarItem? _fileMenuBarItem;

  internal MainWindow(
      IApplication app,
      AppState state,
      LeviathanHexView hexView,
      LeviathanTextView textView,
      LeviathanCsvView csvView,
      CommandPalette palette)
  {
    _app = app;
    _state = state;
    _hexView = hexView;
    _textView = textView;
    _csvView = csvView;
    _palette = palette;

    Title = "Leviathan — Large File Editor";
    BorderStyle = LineStyle.None;

    // ─── Menu bar ───
    _menuBar = BuildMenuBar();
    MenuBar menuBar = _menuBar;

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

    _csvView.X = 0;
    _csvView.Y = Pos.Bottom(menuBar);
    _csvView.Width = Dim.Fill();
    _csvView.Height = Dim.Fill(1);
    _csvView.Visible = state.ActiveView == ViewMode.Csv;

    // ─── Status bar ───
    _statusFileLabel = new Label() {
      X = 0,
      Y = 0,
      Width = Dim.Percent(50),
      Text = " Leviathan",
    };

    _statusInfoLabel = new Label() {
      X = Pos.AnchorEnd(),
      Y = 0,
      Width = Dim.Auto(DimAutoStyle.Text),
      Text = "",
    };

    _statusBar = new StatusBar();
    _statusBar.Add(_statusFileLabel, _statusInfoLabel);

    // ─── Welcome view ───
    _welcomeView = BuildWelcomeView(menuBar);

    Add(menuBar, _welcomeView, _hexView, _textView, _csvView, _statusBar);

    // Show welcome if no file is open
    bool hasFile = state.Document is not null;
    _welcomeView.Visible = !hasFile;
    _hexView.Visible = hasFile && state.ActiveView == ViewMode.Hex;
    _textView.Visible = hasFile && state.ActiveView == ViewMode.Text;
    _csvView.Visible = hasFile && state.ActiveView == ViewMode.Csv;

    // View-specific menu items
    UpdateViewMenuVisibility(state.ActiveView);

    // Wire state-changed events to update status bar
    _hexView.StateChanged += UpdateStatusBar;
    _textView.StateChanged += UpdateStatusBar;
    _csvView.StateChanged += UpdateStatusBar;

    // Register command palette commands
    RegisterCommands();

    // ─── Popover overlays ───
    _findBar = new FindBar(state, StartSearch, FindNext, FindPrevious);
    _gotoBar = new GotoBar(state, o => _hexView.GotoOffset(o), l => _textView.GotoLine(l));
    _palettePopover = new CommandPalettePopover(palette, state,
        l => _textView.GotoLine(l), o => _hexView.GotoOffset(o));
    _csvSettingsBar = new CsvSettingsBar(state, () => {
      _csvView.SetNeedsDraw();
      UpdateStatusBar();
    });
    Initialized += RegisterPopovers;

    // ─── Application-level key bindings ───
    SetupAppKeyBindings();
  }

  /// <summary>Registers all popover overlays once the view is initialized.</summary>
  private void RegisterPopovers(object? sender, EventArgs args)
  {
    Initialized -= RegisterPopovers;
    if (_app.Popovers is not { } popovers)
      return;

    popovers.Register(_findBar);
    popovers.Register(_gotoBar);
    popovers.Register(_palettePopover);
    if (_csvSettingsBar is not null)
      popovers.Register(_csvSettingsBar);

    popovers.Hide(_findBar);
    popovers.Hide(_gotoBar);
    popovers.Hide(_palettePopover);
    if (_csvSettingsBar is not null)
      popovers.Hide(_csvSettingsBar);
  }

  private MenuBar BuildMenuBar()
  {
    // Word Wrap — CheckBox CommandView
    _wordWrapCheckBox = new CheckBox {
      Title = "_Word Wrap",
      CanFocus = false,
      Value = _state.WordWrap ? CheckState.Checked : CheckState.UnChecked
    };
    _wordWrapItem = new MenuItem {
      Title = "_Word Wrap",
      HelpText = "Toggle word wrap",
      CommandView = _wordWrapCheckBox
    };
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
    _encodingItem = new MenuItem {
      Title = "_Encoding",
      CommandView = _encodingSelector
    };
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
    _bprItem = new MenuItem {
      Title = "_Bytes/Row",
      CommandView = _bprSelector
    };
    _bprSelector.ValueChanged += (_, args) => {
      if (args.NewValue is int val)
        SetBytesPerRow(val);
    };

    _fileMenuBarItem = new MenuBarItem("_File", BuildFileMenuItems()!);

    return new MenuBar([
        _fileMenuBarItem,
            new MenuBarItem("_View", [
                new MenuItem("_Hex View", Key.F5, () => SwitchView(ViewMode.Hex)) { HelpText = "Switch to hex view" },
                new MenuItem("_Text View", Key.F6, () => SwitchView(ViewMode.Text)) { HelpText = "Switch to text view" },
                new MenuItem("_CSV View", Key.F7, () => SwitchView(ViewMode.Csv)) { HelpText = "Switch to CSV view" },
                _wordWrapItem,
                _encodingItem,
                _bprItem,
                (_csvSettingsItem = new MenuItem("CSV _Settings...", Key.F8, () => ShowCsvSettings()) { HelpText = "Configure CSV dialect", Visible = false }),
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
                new MenuItem("_Delete Row(s)", Key.Delete, () => DoDeleteCsvRows()) { HelpText = "Delete selected CSV row(s)" },
            ]),
            new MenuBarItem("_Help", [
                new MenuItem("_Keyboard Shortcuts", Key.F1, () => ShowKeyboardShortcuts()) { HelpText = "Show key combinations" },
                new MenuItem("_About", "About Leviathan", () => ShowAboutDialog()),
            ]),
        ]);
  }

  /// <summary>Builds the File menu items including MRU entries.</summary>
  private View[] BuildFileMenuItems()
  {
    List<View> items = [
      new MenuItem("_Open...", Key.O.WithCtrl, () => ShowOpenDialog()) { HelpText = "Open a file" },
      new MenuItem("_Save", Key.S.WithCtrl, () => SaveFile()) { HelpText = "Save the file" },
      new MenuItem("Save _As...", "Save to a new path", () => ShowSaveAsDialog()),
    ];

    List<string> recent = _state.Settings.RecentFiles;
    if (recent.Count > 0) {
      items.Add(new Line()); // separator
      for (int i = 0; i < Math.Min(9, recent.Count); i++) {
        string path = recent[i];
        string fileName = Path.GetFileName(path);
        string title = $"_{i + 1} {fileName}";
        items.Add(new MenuItem(title, path, () => _app.Invoke(() => GuardUnsavedChanges(() => { _state.OpenFile(path); ShowFileViews(); }))));
      }
    }

    items.Add(new Line()); // separator before Quit
    items.Add(new MenuItem("_Quit", Key.Q.WithCtrl, () => GuardUnsavedChanges(() => _app.RequestStop())) { HelpText = "Exit Leviathan" });

    return items.ToArray();
  }

  /// <summary>Rebuilds the File menu to reflect updated MRU entries.</summary>
  private void RefreshFileMenu()
  {
    if (_fileMenuBarItem is null) return;
    // Don't Dispose the old PopoverMenu — Terminal.Gui may still reference it
    // during its dismissal lifecycle. The old instance will be GC'd naturally.
    _fileMenuBarItem.PopoverMenu = new PopoverMenu(BuildFileMenuItems()!);
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
      } else if (e == Key.F7) {
        SwitchView(ViewMode.Csv);
        e.Handled = true;
      } else if (e == Key.F8 && _state.ActiveView == ViewMode.Csv) {
        ShowCsvSettings();
        e.Handled = true;
      } else if (e == Key.F2 && _state.ActiveView == ViewMode.Csv) {
        ShowCsvRecordDetail();
        e.Handled = true;
      } else if (e == Key.F1) {
        ShowKeyboardShortcuts();
        e.Handled = true;
      }

      // MRU digit keys (1-9) when welcome screen is visible
      if (_welcomeView.Visible && !e.Handled) {
        int digit = e.KeyCode switch {
          KeyCode.D1 => 0,
          KeyCode.D2 => 1,
          KeyCode.D3 => 2,
          KeyCode.D4 => 3,
          KeyCode.D5 => 4,
          KeyCode.D6 => 5,
          KeyCode.D7 => 6,
          KeyCode.D8 => 7,
          KeyCode.D9 => 8,
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

      // Forward unhandled Alt+letter to the menu bar so its HotKey bindings fire.
      // Terminal.Gui's HotKey dispatch (step D) should handle this automatically,
      // but develop.5185 does not reliably route Alt+letter to sibling MenuBar
      // when certain views (e.g. text view with scrollbar children) have focus.
      if (!e.Handled && e.IsAlt && !e.IsCtrl) {
        if (_menuBar.NewKeyDownEvent(e))
          e.Handled = true;
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
    _csvView.Visible = mode == ViewMode.Csv;
    UpdateViewMenuVisibility(mode);

    if (mode == ViewMode.Csv && _state.CsvRowIndexer is null)
      _state.InitCsvView();

    if (mode == ViewMode.Hex)
      _hexView.SetFocus();
    else if (mode == ViewMode.Text)
      _textView.SetFocus();
    else
      _csvView.SetFocus();

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
    if (_csvSettingsItem is not null)
      _csvSettingsItem.Visible = mode == ViewMode.Csv;
    // Encoding is visible in both modes
  }

  /// <summary>Hides the welcome screen and shows the file views.</summary>
  private void ShowFileViews()
  {
    _welcomeView.Visible = false;

    // Auto-detect CSV files and switch to CSV view
    if (_state.CurrentFilePath is not null)
    {
      string ext = Path.GetExtension(_state.CurrentFilePath).ToLowerInvariant();
      if (ext is ".csv" or ".tsv" or ".tab")
      {
        _state.ActiveView = ViewMode.Csv;
        _state.InitCsvView();
      }
    }

    _hexView.Visible = _state.ActiveView == ViewMode.Hex;
    _textView.Visible = _state.ActiveView == ViewMode.Text;
    _csvView.Visible = _state.ActiveView == ViewMode.Csv;
    UpdateViewMenuVisibility(_state.ActiveView);

    // Sync encoding selector to the auto-detected (or current) encoding
    if (_encodingSelector is not null)
      _encodingSelector.Value = (int)_state.Decoder.Encoding;

    UpdateTitle();
    UpdateStatusBar();
    _hexView.SetNeedsDraw();
    _textView.SetNeedsDraw();
    _csvView.SetNeedsDraw();
    RefreshFileMenu();

    if (_state.ActiveView == ViewMode.Csv)
      _csvView.SetFocus();
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
      openDlg.KeyDown += (_, e) => {
        if (e.KeyCode == KeyCode.Esc) { openDlg.RequestStop(); e.Handled = true; }
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
    saveDlg.KeyDown += (_, e) => {
      if (e.KeyCode == KeyCode.Esc) { saveDlg.RequestStop(); e.Handled = true; }
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

  private void ShowKeyboardShortcuts()
  {
    string[] lines = BuildKeyboardShortcutsTable().Split('\n', StringSplitOptions.None);

    Window shortcutsWindow = new() {
      Title = "Keyboard Shortcuts",
      Width = Dim.Percent(72),
      Height = Dim.Percent(78),
    };

    View content = new() {
      X = 1,
      Y = 1,
      Width = Dim.Fill(2),
      Height = Dim.Fill(2),
      CanFocus = false,
    };

    content.DrawingContent += (_, _) => {
      content.SetAttributeForRole(VisualRole.Normal);
      int viewportWidth = content.Viewport.Width;
      int viewportHeight = content.Viewport.Height;

      for (int row = 0; row < viewportHeight; row++) {
        content.Move(0, row);
        for (int column = 0; column < viewportWidth; column++)
          content.AddRune(' ');
      }

      int rowCount = Math.Min(lines.Length, viewportHeight);
      for (int row = 0; row < rowCount; row++) {
        string line = lines[row];
        int columnCount = Math.Min(line.Length, viewportWidth);
        content.Move(0, row);
        for (int column = 0; column < columnCount; column++)
          content.AddRune(line[column]);
      }
    };

    shortcutsWindow.KeyDown += (_, e) => {
      if (e.KeyCode is KeyCode.Esc or KeyCode.Enter || e == Key.F1) {
        shortcutsWindow.RequestStop();
        e.Handled = true;
      }
    };

    shortcutsWindow.Add(content);
    _app.Run(shortcutsWindow);
    shortcutsWindow.Dispose();
  }

  private static string BuildKeyboardShortcutsTable()
  {
    (string Section, string Shortcut, string Description)[] rows = [
      ("Global", "F1", "Help"),
      ("Global", "Ctrl+O", "Open file"),
      ("Global", "Ctrl+S", "Save"),
      ("Global", "Ctrl+Q", "Quit"),
      ("Global", "Ctrl+P", "Command palette"),
      ("Global", "Ctrl+G", "Go to offset/line"),
      ("Global", "Ctrl+F", "Find"),
      ("Global", "F3", "Find next"),
      ("Global", "Shift+F3", "Find previous"),
      ("Global", "F5 / F6", "Hex / Text view"),
      ("Navigation and editing", "Arrow keys", "Move cursor"),
      ("Navigation and editing", "Shift+Arrow keys", "Select while moving"),
      ("Navigation and editing", "PageUp/PageDown", "Scroll by page"),
      ("Navigation and editing", "Home/End", "Start/end of line or row"),
      ("Navigation and editing", "Ctrl+Home/End", "Start/end of file"),
      ("Navigation and editing", "Backspace/Delete", "Delete before/at cursor"),
      ("Navigation and editing", "Ctrl+C / Ctrl+V", "Copy / Paste"),
      ("Navigation and editing", "Ctrl+A", "Select all"),
      ("Navigation and editing", "Enter", "Insert newline (Text view)"),
      ("CSV view", "F7", "Switch to CSV view"),
      ("CSV view", "F8", "CSV settings (separator, quote, header)"),
      ("CSV view", "F2", "Record detail (drill-down)"),
      ("CSV view", "Tab / Shift+Tab", "Next / previous column"),
      ("CSV view", "Del", "Delete selected rows"),
    ];

    const string shortcutHeader = "Shortcut";
    const string descriptionHeader = "Description";

    int shortcutWidth = shortcutHeader.Length;
    int descriptionWidth = descriptionHeader.Length;

    foreach ((string _, string shortcut, string description) in rows) {
      if (shortcut.Length > shortcutWidth)
        shortcutWidth = shortcut.Length;
      if (description.Length > descriptionWidth)
        descriptionWidth = description.Length;
    }

    List<string> lines = [];
    string? currentSection = null;

    foreach ((string section, string shortcut, string description) in rows) {
      if (!string.Equals(currentSection, section, StringComparison.Ordinal)) {
        if (lines.Count > 0)
          lines.Add(string.Empty);

        lines.Add(section);
        lines.Add($"{shortcutHeader.PadRight(shortcutWidth)}  {descriptionHeader}");
        lines.Add($"{new string('-', shortcutWidth)}  {new string('-', descriptionWidth)}");
        currentSection = section;
      }

      lines.Add($"{shortcut.PadRight(shortcutWidth)}  {description}");
    }

    lines.Add(string.Empty);
    lines.Add("Press Enter, Esc, or F1 to close.");

    return string.Join('\n', lines);
  }

  private void ShowAboutDialog()
  {
    Assembly assembly = typeof(MainWindow).Assembly;
    string informationalVersion = ThisAssembly.AssemblyInformationalVersion;
    int metadataSep = informationalVersion.IndexOf('+');
    string versionText = metadataSep > 0 ? informationalVersion[..metadataSep] : informationalVersion;
    if (string.IsNullOrWhiteSpace(versionText))
      versionText = "unknown";
    string commitText = ThisAssembly.GitCommitId;
    if (commitText.Length > 12)
      commitText = commitText[..12];
    if (string.IsNullOrWhiteSpace(commitText))
      commitText = "unknown";
    string buildDateText = ReadAssemblyMetadata(assembly, "BuildDateUtc");
    string terminalGuiVersion = typeof(Window).Assembly.GetName().Version?.ToString() ?? "unknown";
    string message =
        "Leviathan TUI2\n" +
        "Large file editor\n" +
        $"\nVersion: {versionText}\n" +
        $"Commit: {commitText}\n" +
        $"Build date (UTC): {buildDateText}\n" +
        $"Terminal.Gui: {terminalGuiVersion}\n" +
        $".NET: {Environment.Version}\n" +
        "\nBuilt for fast navigation and editing of very large files.";
    MessageBox.Query(App!, "About Leviathan", message, "OK");
  }

  private static string ReadAssemblyMetadata(Assembly assembly, string key)
  {
    foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>()) {
      if (string.Equals(metadata.Key, key, StringComparison.Ordinal)
          && !string.IsNullOrWhiteSpace(metadata.Value))
        return metadata.Value!;
    }

    return "unknown";
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
    _state.SearchResults = [];
    _state.CurrentMatchIndex = -1;
    _state.SearchStatus = "Searching…";
    _state.IsSearching = true;
    UpdateStatusBar();

    CancellationTokenSource cts = new();
    _state.SearchCts = cts;
    CancellationToken token = cts.Token;
    Leviathan.Core.Document document = _state.Document;

    byte[]? pattern;
    if (_state.FindHexMode) {
      pattern = ParseHexPattern(query);
      if (pattern is null || pattern.Length == 0) {
        _state.SearchStatus = "Invalid hex pattern";
        _state.IsSearching = false;
        if (ReferenceEquals(_state.SearchCts, cts))
          _state.SearchCts = null;
        cts.Dispose();
        UpdateStatusBar();
        return;
      }
    } else {
      pattern = _state.Decoder.EncodeString(query);
    }

    _state.SearchTask = Task.Run(() => {
      try {
        List<SearchResult> results = [];
        bool caseSensitive = _state.FindHexMode || _state.FindCaseSensitive;
        foreach (SearchResult r in SearchEngine.FindAll(document, pattern, caseSensitive, token)) {
          if (token.IsCancellationRequested) break;
          results.Add(r);
        }

        if (token.IsCancellationRequested) return;

        // All shared state mutations + CTS cleanup happen on the UI thread
        // so the ReferenceEquals guard and the nulling are atomic.
        _app.Invoke(() => {
          if (!ReferenceEquals(_state.SearchCts, cts)) return;
          _state.SearchCts = null;

          _state.SearchResults = results;
          _state.CurrentMatchIndex = results.Count > 0 ? 0 : -1;
          _state.SearchStatus = results.Count > 0
              ? $"{results.Count} match{(results.Count > 1 ? "es" : "")}"
              : "No matches";
          _state.IsSearching = false;

          if (results.Count > 0)
            NavigateToMatch(0);

          UpdateStatusBar();
          _findBar.UpdateStatus();
          _hexView.SetNeedsDraw();
          _textView.SetNeedsDraw();
        });
      } catch (OperationCanceledException) {
        _app.Invoke(() => {
          if (!ReferenceEquals(_state.SearchCts, cts)) return;
          _state.SearchCts = null;

          _state.SearchStatus = "Search cancelled";
          _state.IsSearching = false;
          UpdateStatusBar();
          _findBar.UpdateStatus();
        });
      } finally {
        cts.Dispose();
      }
    }, token);
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
    string? text = _state.ActiveView switch {
      ViewMode.Hex => _hexView.CopySelection(),
      ViewMode.Csv => _csvView.CopySelection(),
      _ => _textView.CopySelection()
    };

    if (text is not null)
      _app.Clipboard?.TrySetClipboardData(text);
  }

  private void DoPaste()
  {
    if (_state.ActiveView == ViewMode.Csv) return; // CSV is read-only for paste
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
    else if (_state.ActiveView == ViewMode.Text)
      _textView.SelectAll();
    // CSV: no select-all for now (rows only)
  }

  private void DoDeleteCsvRows()
  {
    if (_state.ActiveView != ViewMode.Csv) return;
    _csvView.DeleteSelectedRows();
  }

  private void ShowCsvSettings()
  {
    if (_csvSettingsBar is null || _app.Popovers is null) return;
    _csvSettingsBar.Refresh();
    _app.Popovers.Show(_csvSettingsBar);
  }

  private void ShowCsvRecordDetail()
  {
    if (_state.ActiveView != ViewMode.Csv) return;
    (string Name, string Value)[] details = _csvView.ReadRecordDetails(_state.CsvCursorRow);
    if (details.Length == 0) return;

    CsvRecordDetailDialog dialog = new(_state.CsvCursorRow, details);
    _app.Run(dialog);
    dialog.Dispose();
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
      Attribute normalAttr = view.GetAttributeForRole(VisualRole.Normal);
      Color bg = normalAttr.Background;
      Attribute titleAttr = new(new Color(208, 135, 46), bg);
      Attribute hintAttr = new(new Color(100, 130, 160), bg);
      Attribute mruAttr = new(new Color(StandardColor.Yellow), bg);

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
        // Center the MRU block as a whole, left-align entries within it
        int maxEntryWidth = 0;
        int mruCount = Math.Min(9, recent.Count);
        for (int i = 0; i < mruCount; i++) {
          string entry = $"[{i + 1}]  {recent[i]}";
          if (entry.Length > maxEntryWidth)
            maxEntryWidth = entry.Length;
        }
        int indent = Math.Max(2, (vpW - maxEntryWidth) / 2);
        for (int i = 0; i < mruCount; i++) {
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
    _palette.RegisterCommand("View", "CSV View", "F7", () => SwitchView(ViewMode.Csv));
    _palette.RegisterCommand("View", "CSV Settings...", "F8", () => ShowCsvSettings());

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
    _palette.RegisterCommand("Help", "Keyboard Shortcuts", "F1", () => ShowKeyboardShortcuts());
    _palette.RegisterCommand("Help", "About", "", () => ShowAboutDialog());

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
          () => _app.Invoke(() => GuardUnsavedChanges(() => { _state.OpenFile(path); ShowFileViews(); })));
    }
  }

  // ─── Status bar & title ───

  private void UpdateStatusBar()
  {
    if (_state.Document is null) {
      Title = "Leviathan — Large File Editor";
      _statusFileLabel.Text = " Leviathan";
      _statusInfoLabel.Text = "";
      return;
    }

    UpdateTitle();
    SetNeedsDraw();
  }

  private void UpdateTitle()
  {
    if (_state.CurrentFilePath is null) {
      Title = "Leviathan — Large File Editor";
      _statusFileLabel.Text = " Leviathan";
      _statusInfoLabel.Text = "";
      return;
    }

    string fileName = Path.GetFileName(_state.CurrentFilePath);
    string modified = _state.IsModified ? "● " : "";
    string viewName = _state.ActiveView switch {
      ViewMode.Hex => "HEX",
      ViewMode.Csv => "CSV",
      _ => "TEXT"
    };
    string encoding = _state.Decoder.Encoding.ToString();
    long cursor = _state.CurrentCursorOffset;
    List<SearchResult> searchResults = _state.SearchResults;
    string searchInfo = searchResults.Count > 0
        ? $" | {_state.CurrentMatchIndex + 1}/{searchResults.Count} matches"
        : _state.IsSearching ? " | Searching…" : "";

    if (_state.ActiveView == ViewMode.Csv)
    {
      long totalRows = _state.CsvRowIndex?.TotalRowCount ?? 0;
      if (_state.CsvDialect.HasHeader && totalRows > 0) totalRows--;
      string csvInfo = $"Row {_state.CsvCursorRow + 1}/{totalRows} | Col {_state.CsvCursorCol + 1}/{_state.CsvColumnCount}";
      char sep = (char)_state.CsvDialect.Separator;
      string sepName = sep switch { ',' => "Comma", '\t' => "Tab", '|' => "Pipe", ';' => "Semicolon", _ => $"'{sep}'" };
      Title = $"{modified}{fileName} — {viewName} | {csvInfo} | Sep: {sepName} | {FormatFileSize(_state.FileLength)}";
      _statusFileLabel.Text = $" {modified}{fileName}";
      _statusInfoLabel.Text = $"{viewName} | {csvInfo} | Sep: {sepName} | {FormatFileSize(_state.FileLength)} ";
    }
    else
    {
      Title = $"{modified}{fileName} — {viewName} | {FormatFileSize(_state.FileLength)} | Offset: 0x{cursor:X} ({cursor}){searchInfo} | {encoding}";

      // Status bar — left: file name with dirty indicator
      _statusFileLabel.Text = $" {modified}{fileName}";

      // Status bar — right: view mode, encoding, offset, size, search
      _statusInfoLabel.Text = $"{viewName} | {encoding} | Offset: 0x{cursor:X} ({cursor}) | {FormatFileSize(_state.FileLength)}{searchInfo} ";
    }
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
