using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.GUI.Widgets;

namespace Leviathan.GUI.Views;

/// <summary>
/// Main application window. Hosts menu bar, status bar, and the active view.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppState _state = new();
    private HexViewControl? _hexView;
    private TextViewControl? _textView;
    private CsvViewControl? _csvView;
    private DispatcherTimer? _indexingTimer;
    private FindBar? _findBar;
    private GotoBar? _gotoBar;
    private CommandPaletteOverlay? _commandPalette;

    public MainWindow()
    {
        InitializeComponent();

        // Apply persisted settings
        _state.BytesPerRowSetting = _state.Settings.BytesPerRow;
        _state.WordWrap = _state.Settings.WordWrap;

        WireMenuEvents();
        BuildMruList();
        UpdateViewVisibility();
        UpdateWordWrapCheck();
        UpdateBprChecks();
        UpdateEncodingChecks();
        UpdateViewModeChecks();
        UpdateStatusBar();

        // Start background indexing timer
        _indexingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _indexingTimer.Tick += (_, _) => UpdateIndexingStatus();
        _indexingTimer.Start();

        // Handle window closing for unsaved changes guard
        Closing += OnWindowClosing;

        // Handle CLI argument
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            _state.OpenFile(args[1]);
            OnFileOpened();
        }

        // Drag-and-drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        // Keyboard shortcuts not bound to menu items
        KeyDown += OnGlobalKeyDown;
    }

    private void WireMenuEvents()
    {
        MenuOpen.Click += async (_, _) => await ShowOpenDialog();
        MenuSave.Click += (_, _) => SaveFile();
        MenuSaveAs.Click += async (_, _) => await ShowSaveAsDialog();
        MenuExit.Click += (_, _) => Close();

        MenuViewHex.Click += (_, _) => SwitchView(ViewMode.Hex);
        MenuViewText.Click += (_, _) => SwitchView(ViewMode.Text);
        MenuViewCsv.Click += (_, _) => SwitchView(ViewMode.Csv);

        MenuWordWrap.Click += (_, _) => ToggleWordWrap();

        MenuEncodingUtf8.Click += (_, _) => SwitchEncoding(TextEncoding.Utf8);
        MenuEncodingUtf16Le.Click += (_, _) => SwitchEncoding(TextEncoding.Utf16Le);
        MenuEncodingWin1252.Click += (_, _) => SwitchEncoding(TextEncoding.Windows1252);

        MenuBprAuto.Click += (_, _) => SetBytesPerRow(0);
        MenuBpr8.Click += (_, _) => SetBytesPerRow(8);
        MenuBpr16.Click += (_, _) => SetBytesPerRow(16);
        MenuBpr24.Click += (_, _) => SetBytesPerRow(24);
        MenuBpr32.Click += (_, _) => SetBytesPerRow(32);
        MenuBpr48.Click += (_, _) => SetBytesPerRow(48);
        MenuBpr64.Click += (_, _) => SetBytesPerRow(64);

        MenuAbout.Click += (_, _) => ShowAboutDialog();
        MenuKeyboardShortcuts.Click += (_, _) => ShowKeyboardShortcuts();

        MenuFind.Click += (_, _) => ShowFindBar();
        MenuFindNext.Click += (_, _) => FindNext();
        MenuFindPrev.Click += (_, _) => FindPrevious();
        MenuGoto.Click += (_, _) => ShowGotoBar();
        MenuCsvSettings.Click += async (_, _) => await ShowCsvSettingsDialog();
        MenuCopy.Click += (_, _) => DoCopy();
        MenuPaste.Click += (_, _) => DoPaste();
        MenuSelectAll.Click += (_, _) => DoSelectAll();
        MenuDeleteRows.Click += (_, _) => DoDeleteCsvRows();
    }

    // ─── File operations ───

    private async Task ShowOpenDialog()
    {
        if (!await GuardUnsavedChanges()) return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open File",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.All]
            });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _state.OpenFile(path);
            OnFileOpened();
        }
    }

    private void SaveFile()
    {
        if (_state.Document is null) return;

        if (_state.CurrentFilePath is { } path)
        {
            if (!_state.TrySave(path, out string? error))
                ShowErrorDialog("Save Error", error ?? "Unknown error");
            else
                UpdateStatusBar();
        }
        else
        {
            _ = ShowSaveAsDialog();
        }
    }

    private async Task ShowSaveAsDialog()
    {
        if (_state.Document is null) return;

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save As",
                SuggestedFileName = _state.CurrentFilePath is not null
                    ? Path.GetFileName(_state.CurrentFilePath)
                    : "untitled"
            });

        if (file?.TryGetLocalPath() is { } path)
        {
            if (!_state.TrySave(path, out string? error))
                ShowErrorDialog("Save Error", error ?? "Unknown error");
            else
                UpdateStatusBar();
        }
    }

    /// <summary>
    /// Guards against unsaved changes. Returns true if it's safe to proceed.
    /// </summary>
    private async Task<bool> GuardUnsavedChanges()
    {
        if (!_state.IsModified) return true;

        Window dialog = new()
        {
            Title = "Unsaved Changes",
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        int result = -1; // -1 = cancel, 0 = don't save, 1 = save

        StackPanel panel = new()
        {
            Margin = new Thickness(16),
            Spacing = 12
        };
        panel.Children.Add(new TextBlock { Text = "The file has unsaved changes. What would you like to do?" });

        StackPanel buttons = new()
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        Button saveBtn = new() { Content = "Save" };
        saveBtn.Click += (_, _) => { result = 1; dialog.Close(); };
        Button dontSaveBtn = new() { Content = "Don't Save" };
        dontSaveBtn.Click += (_, _) => { result = 0; dialog.Close(); };
        Button cancelBtn = new() { Content = "Cancel" };
        cancelBtn.Click += (_, _) => { result = -1; dialog.Close(); };

        buttons.Children.Add(saveBtn);
        buttons.Children.Add(dontSaveBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(this);

        if (result == 1)
        {
            SaveFile();
            return !_state.IsModified; // Only proceed if save succeeded
        }

        return result == 0;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_state.IsModified)
        {
            e.Cancel = true;
            if (await GuardUnsavedChanges())
            {
                _state.CancelSearch();
                _state.CsvRowIndexer?.Dispose();
                _state.Indexer?.Dispose();
                _state.Document?.Dispose();
                Closing -= OnWindowClosing;
                Close();
            }
        }
        else
        {
            _state.CancelSearch();
            _state.CsvRowIndexer?.Dispose();
            _state.Indexer?.Dispose();
            _state.Document?.Dispose();
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragDrop API migration in progress in Avalonia 11.x
        if (e.Data is Avalonia.Input.IDataObject dataObj)
        {
            IEnumerable<IStorageItem>? files = dataObj.GetFiles();
#pragma warning restore CS0618
            if (files is not null)
            {
                foreach (IStorageItem item in files)
                {
                    if (item is IStorageFile file && file.TryGetLocalPath() is { } path)
                    {
                        _state.OpenFile(path);
                        OnFileOpened();
                        return;
                    }
                }
            }
        }
    }

    // ─── View management ───

    private void OnFileOpened()
    {
        EnsureViewControlsCreated();

        // Auto-detect CSV by file extension
        if (_state.CurrentFilePath is { } fp)
        {
            string ext = Path.GetExtension(fp).ToLowerInvariant();
            if (ext is ".csv" or ".tsv" or ".tab")
            {
                _state.InitCsvView();
                _state.ActiveView = ViewMode.Csv;
            }
        }

        UpdateViewVisibility();
        BuildMruList();
        UpdateTitle();
        UpdateStatusBar();
    }

    private void EnsureViewControlsCreated()
    {
        if (_hexView is not null) return;

        _hexView = new HexViewControl(_state);
        _textView = new TextViewControl(_state);
        _csvView = new CsvViewControl(_state);

        _hexView.StateChanged = UpdateStatusBar;
        _textView.StateChanged = UpdateStatusBar;
        _csvView.StateChanged = UpdateStatusBar;
        _csvView.OnRecordDetail = ShowCsvRecordDetail;

        // Create scrollbarsand compose each view + scrollbar in a Grid
        ContentArea.Children.Add(CreateViewWithScrollBar(_hexView, sb =>
        {
            _hexView.ScrollBar = sb;
            sb.ValueChanged += _hexView.OnScrollBarValueChanged;
        }));
        ContentArea.Children.Add(CreateViewWithScrollBar(_textView, sb =>
        {
            _textView.ScrollBar = sb;
            sb.ValueChanged += _textView.OnScrollBarValueChanged;
        }));
        ContentArea.Children.Add(CreateViewWithScrollBar(_csvView, sb =>
        {
            _csvView.ScrollBar = sb;
            sb.ValueChanged += _csvView.OnScrollBarValueChanged;
        }));
    }

    private static Grid CreateViewWithScrollBar(Control view, Action<Avalonia.Controls.Primitives.ScrollBar> configure)
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        Grid.SetColumn(view, 0);
        grid.Children.Add(view);

        Avalonia.Controls.Primitives.ScrollBar sb = new()
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Minimum = 0,
            Width = 14
        };
        Grid.SetColumn(sb, 1);
        grid.Children.Add(sb);
        configure(sb);
        return grid;
    }

    private void SwitchView(ViewMode mode)
    {
        if (_state.Document is null) return;

        if (mode == ViewMode.Csv && _state.CsvRowIndexer is null)
            _state.InitCsvView();

        _state.ActiveView = mode;
        EnsureViewControlsCreated();
        UpdateViewVisibility();
        UpdateViewModeChecks();
        UpdateStatusBar();

        MenuCsvSettings.IsVisible = mode == ViewMode.Csv;
    }

    private void UpdateViewVisibility()
    {
        bool hasFile = _state.Document is not null;
        WelcomeView.IsVisible = !hasFile;

        if (_hexView is not null)
        {
            // Toggle the Grid wrappers (parent of each view control)
            ((Control)_hexView.Parent!).IsVisible = hasFile && _state.ActiveView == ViewMode.Hex;
            ((Control)_textView!.Parent!).IsVisible = hasFile && _state.ActiveView == ViewMode.Text;
            ((Control)_csvView!.Parent!).IsVisible = hasFile && _state.ActiveView == ViewMode.Csv;

            if (hasFile)
            {
                Control activeView = _state.ActiveView switch
                {
                    ViewMode.Hex => _hexView,
                    ViewMode.Text => _textView!,
                    ViewMode.Csv => _csvView!,
                    _ => _hexView
                };
                activeView.Focus();
                activeView.InvalidateVisual();
            }
        }
    }

    // ─── View options ───

    private void ToggleWordWrap()
    {
        _state.WordWrap = !_state.WordWrap;
        _state.Settings.WordWrap = _state.WordWrap;
        _state.Settings.Save();
        UpdateWordWrapCheck();
        _textView?.InvalidateVisual();
    }

    private void UpdateWordWrapCheck()
    {
        MenuWordWrap.Icon = _state.WordWrap
            ? new TextBlock { Text = "✓", FontSize = 12 }
            : null;
    }

    private void UpdateViewModeChecks()
    {
        MenuViewHex.Icon = _state.ActiveView == ViewMode.Hex
            ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuViewText.Icon = _state.ActiveView == ViewMode.Text
            ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuViewCsv.Icon = _state.ActiveView == ViewMode.Csv
            ? new TextBlock { Text = "●", FontSize = 10 } : null;
    }

    private void UpdateEncodingChecks()
    {
        TextEncoding current = _state.Decoder.Encoding;
        MenuEncodingUtf8.Icon = current == TextEncoding.Utf8
            ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuEncodingUtf16Le.Icon = current == TextEncoding.Utf16Le
            ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuEncodingWin1252.Icon = current == TextEncoding.Windows1252
            ? new TextBlock { Text = "●", FontSize = 10 } : null;
    }

    private void UpdateBprChecks()
    {
        int current = _state.BytesPerRowSetting;
        MenuBprAuto.Icon = current == 0 ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuBpr8.Icon = current == 8 ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuBpr16.Icon = current == 16 ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuBpr24.Icon = current == 24 ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuBpr32.Icon = current == 32 ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuBpr48.Icon = current == 48 ? new TextBlock { Text = "●", FontSize = 10 } : null;
        MenuBpr64.Icon = current == 64 ? new TextBlock { Text = "●", FontSize = 10 } : null;
    }

    private void SwitchEncoding(TextEncoding encoding)
    {
        _state.SwitchEncoding(encoding);
        _textView?.InvalidateVisual();
        _hexView?.InvalidateVisual();
        UpdateEncodingChecks();
        UpdateStatusBar();
    }

    private void SetBytesPerRow(int value)
    {
        _state.BytesPerRowSetting = value;
        _state.Settings.BytesPerRow = value;
        _state.Settings.Save();
        UpdateBprChecks();
        _hexView?.InvalidateVisual();
    }

    // ─── Status bar ───

    private void UpdateTitle()
    {
        string title = "Leviathan";
        if (_state.CurrentFilePath is { } path)
        {
            title = $"{Path.GetFileName(path)}{(_state.IsModified ? " •" : "")} — Leviathan";
        }
        Title = title;
    }

    private void UpdateStatusBar()
    {
        UpdateTitle();

        if (_state.Document is null)
        {
            StatusLeft.Text = " Leviathan";
            StatusRight.Text = "";
            return;
        }

        string fileName = _state.CurrentFilePath is not null
            ? Path.GetFileName(_state.CurrentFilePath)
            : "(untitled)";
        string modified = _state.IsModified ? " [modified]" : "";
        StatusLeft.Text = $" {fileName}{modified}";

        string viewMode = _state.ActiveView switch
        {
            ViewMode.Hex => "Hex",
            ViewMode.Text => "Text",
            ViewMode.Csv => "CSV",
            _ => ""
        };

        string encoding = _state.Decoder.Encoding switch
        {
            TextEncoding.Utf8 => "UTF-8",
            TextEncoding.Utf16Le => "UTF-16 LE",
            TextEncoding.Windows1252 => "Win-1252",
            _ => ""
        };

        string size = FormatFileSize(_state.FileLength);
        string offset = $"0x{_state.CurrentCursorOffset:X}";
        string lines = _state.EstimatedTotalLines > 0 ? $"{_state.EstimatedTotalLines:N0} lines" : "";

        if (_state.ActiveView == ViewMode.Csv && _state.CsvRowIndex is not null)
        {
            long totalCsvRows = _state.CsvRowIndex.TotalRowCount;
            string sepName = _state.CsvDialect.Separator switch
            {
                (byte)',' => "Comma",
                (byte)'\t' => "Tab",
                (byte)'|' => "Pipe",
                (byte)';' => "Semicolon",
                _ => $"'{(char)_state.CsvDialect.Separator}'"
            };
            StatusRight.Text = $"CSV  |  Row {_state.CsvCursorRow + 1}/{totalCsvRows}  |  Col {_state.CsvCursorCol + 1}/{_state.CsvColumnCount}  |  Sep: {sepName}  |  {size}";
            return;
        }

        StatusRight.Text = $"{viewMode}  |  {encoding}  |  {size}  |  Offset: {offset}  |  {lines}";
    }

    private void UpdateIndexingStatus()
    {
        if (_state.Indexer?.Index is { } lineIndex)
        {
            long total = lineIndex.TotalLineCount;
            if (total > 0)
                _state.EstimatedTotalLines = total;
        }
        UpdateStatusBar();
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ─── MRU list ───

    private void BuildMruList()
    {
        // ── File menu MRU items ──
        // Remove any previous dynamic MRU items (between first Separator and MruSeparator)
        List<Control> toRemove = [];
        bool inMruZone = false;
        foreach (Control child in FileMenu.Items.Cast<Control>())
        {
            if (child == MruSeparator) break;
            if (inMruZone) toRemove.Add(child);
            if (child is Separator && child != MruSeparator && !inMruZone) inMruZone = true;
        }
        foreach (Control c in toRemove)
            FileMenu.Items.Remove(c);

        List<string> recent = _state.Settings.RecentFiles;

        if (recent.Count == 0)
        {
            MruSeparator.IsVisible = false;
        }
        else
        {
            MruSeparator.IsVisible = true;
            // Insert MRU items before MruSeparator
            int insertIdx = FileMenu.Items.IndexOf(MruSeparator);
            for (int i = 0; i < Math.Min(9, recent.Count); i++)
            {
                string path = recent[i];
                string fileName = Path.GetFileName(path);
                int number = i + 1;
                MenuItem mruItem = new() { Header = $"_{number} {fileName}" };
                string capturedPath = path;
                mruItem.Click += async (_, _) =>
                {
                    if (!await GuardUnsavedChanges()) return;
                    if (File.Exists(capturedPath))
                    {
                        _state.OpenFile(capturedPath);
                        OnFileOpened();
                    }
                };
                FileMenu.Items.Insert(insertIdx + i, mruItem);
            }
        }

        // ── Welcome view MRU list ──
        MruListPanel.Children.Clear();
        if (recent.Count == 0) return;

        MruListPanel.Children.Add(new TextBlock
        {
            Text = "Recent Files:",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 4)
        });

        ListBox mruListBox = new()
        {
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            MaxHeight = 300
        };

        for (int i = 0; i < Math.Min(9, recent.Count); i++)
        {
            string path = recent[i];
            string fileName = Path.GetFileName(path);
            ListBoxItem item = new()
            {
                Content = $"[{i + 1}]  {fileName}",
                Tag = path,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 13,
                Padding = new Thickness(8, 4)
            };
            ToolTip.SetTip(item, path);
            mruListBox.Items.Add(item);
        }

        mruListBox.DoubleTapped += async (_, _) =>
        {
            if (mruListBox.SelectedItem is ListBoxItem sel && sel.Tag is string filePath)
            {
                if (!await GuardUnsavedChanges()) return;
                if (File.Exists(filePath))
                {
                    _state.OpenFile(filePath);
                    OnFileOpened();
                }
            }
        };

        MruListPanel.Children.Add(mruListBox);
    }

    // ─── Keyboard shortcuts ───

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Global shortcuts that work regardless of focus
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.P:
                    ShowCommandPalette();
                    e.Handled = true;
                    return;
                case Key.Q:
                    Close();
                    e.Handled = true;
                    return;
            }
        }

        // F2 for CSV record detail
        if (e.Key == Key.F2 && _state.ActiveView == ViewMode.Csv)
        {
            ShowCsvRecordDetail();
            e.Handled = true;
            return;
        }

        // Digit shortcutsfor MRU on welcome screen
        if (_state.Document is null && e.KeyModifiers == KeyModifiers.None)
        {
            int digit = e.Key switch
            {
                Key.D1 => 1, Key.D2 => 2, Key.D3 => 3,
                Key.D4 => 4, Key.D5 => 5, Key.D6 => 6,
                Key.D7 => 7, Key.D8 => 8, Key.D9 => 9,
                _ => 0
            };

            if (digit > 0 && digit <= _state.Settings.RecentFiles.Count)
            {
                string path = _state.Settings.RecentFiles[digit - 1];
                if (File.Exists(path))
                {
                    _state.OpenFile(path);
                    OnFileOpened();
                    e.Handled = true;
                }
            }
        }
    }

    // ─── Find / Search ───

    private void ShowFindBar()
    {
        EnsureOverlaysCreated();
        _findBar!.ShowBar();
    }

    private void ShowGotoBar()
    {
        EnsureOverlaysCreated();
        _gotoBar!.ShowBar();
    }

    private void ShowCommandPalette()
    {
        EnsureOverlaysCreated();
        _commandPalette!.Show();
    }

    private void EnsureOverlaysCreated()
    {
        if (_findBar is not null) return;

        _findBar = new FindBar(_state, StartSearch, FindNext, FindPrevious);
        _findBar.IsVisible = false;

        _gotoBar = new GotoBar(_state,
            offset => _hexView?.GotoOffset(offset),
            line => _textView?.GotoLine(line));
        _gotoBar.IsVisible = false;

        _commandPalette = new CommandPaletteOverlay(_state,
            offset => _hexView?.GotoOffset(offset),
            line => _textView?.GotoLine(line));
        _commandPalette.IsVisible = false;
        RegisterCommands();

        ContentArea.Children.Add(_findBar);
        ContentArea.Children.Add(_gotoBar);
        ContentArea.Children.Add(_commandPalette);
    }

    private void RegisterCommands()
    {
        if (_commandPalette is null) return;

        _commandPalette.RegisterCommand("Open File", "Open a file (Ctrl+O)", () => _ = ShowOpenDialog());
        _commandPalette.RegisterCommand("Save", "Save the file (Ctrl+S)", SaveFile);
        _commandPalette.RegisterCommand(() => _state.ActiveView == ViewMode.Hex ? "● Hex View" : "  Hex View", "Switch to hex view (F5)", () => SwitchView(ViewMode.Hex));
        _commandPalette.RegisterCommand(() => _state.ActiveView == ViewMode.Text ? "● Text View" : "  Text View", "Switch to text view (F6)", () => SwitchView(ViewMode.Text));
        _commandPalette.RegisterCommand(() => _state.ActiveView == ViewMode.Csv ? "● CSV View" : "  CSV View", "Switch to CSV view (F7)", () => SwitchView(ViewMode.Csv));
        _commandPalette.RegisterCommand("Find", "Search in file (Ctrl+F)", ShowFindBar);
        _commandPalette.RegisterCommand("Find Next", "Go to next match (F3)", FindNext);
        _commandPalette.RegisterCommand("Find Previous", "Go to previous match (Shift+F3)", FindPrevious);
        _commandPalette.RegisterCommand("Go to", "Go to offset or line (Ctrl+G)", ShowGotoBar);
        _commandPalette.RegisterCommand(() => _state.WordWrap ? "✓ Line Wrap" : "  Line Wrap", "Toggle line wrapping", ToggleWordWrap);
        _commandPalette.RegisterCommand("Copy", "Copy selection (Ctrl+C)", DoCopy);
        _commandPalette.RegisterCommand("Paste", "Paste from clipboard (Ctrl+V)", DoPaste);
        _commandPalette.RegisterCommand("Select All", "Select entire file (Ctrl+A)", DoSelectAll);
        _commandPalette.RegisterCommand("About", "About Leviathan", ShowAboutDialog);
        _commandPalette.RegisterCommand("Keyboard Shortcuts", "Show key combinations (F1)", ShowKeyboardShortcuts);
    }

    private void StartSearch()
    {
        if (_state.Document is null || string.IsNullOrEmpty(_state.FindInput)) return;

        _state.CancelSearch();
        _state.SearchResults = [];
        _state.CurrentMatchIndex = -1;
        _state.IsSearching = true;
        _findBar?.UpdateMatchStatus();

        _state.Settings.AddFindHistory(_state.FindInput);

        CancellationTokenSource cts = new();
        _state.SearchCts = cts;

        byte[]? pattern = _state.FindHexMode
            ? SearchEngine.ParseHexPattern(_state.FindInput)
            : _state.Decoder.EncodeString(_state.FindInput);

        if (pattern is null || pattern.Length == 0)
        {
            _state.IsSearching = false;
            _state.SearchStatus = "Invalid pattern";
            _findBar?.UpdateMatchStatus();
            return;
        }

        bool caseSensitive = _state.FindHexMode || _state.FindCaseSensitive;
        Core.Document doc = _state.Document;

        _state.SearchTask = Task.Run(() =>
        {
            List<SearchResult> results = SearchEngine.FindAll(doc, pattern, caseSensitive, cts.Token).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                _state.SearchResults = results;
                _state.IsSearching = false;
                _state.CurrentMatchIndex = results.Count > 0 ? 0 : -1;
                _state.SearchStatus = results.Count > 0 ? $"{results.Count} matches" : "No matches";
                _findBar?.UpdateMatchStatus();

                if (results.Count > 0)
                    NavigateToMatch(0);

                _hexView?.InvalidateVisual();
                _textView?.InvalidateVisual();
            });
        }, cts.Token);
    }

    private void FindNext()
    {
        if (_state.SearchResults.Count == 0) return;
        _state.CurrentMatchIndex = (_state.CurrentMatchIndex + 1) % _state.SearchResults.Count;
        NavigateToMatch(_state.CurrentMatchIndex);
        _findBar?.UpdateMatchStatus();
    }

    private void FindPrevious()
    {
        if (_state.SearchResults.Count == 0) return;
        _state.CurrentMatchIndex = (_state.CurrentMatchIndex - 1 + _state.SearchResults.Count) % _state.SearchResults.Count;
        NavigateToMatch(_state.CurrentMatchIndex);
        _findBar?.UpdateMatchStatus();
    }

    private void NavigateToMatch(int matchIndex)
    {
        if (matchIndex < 0 || matchIndex >= _state.SearchResults.Count) return;
        long offset = _state.SearchResults[matchIndex].Offset;

        if (_state.ActiveView == ViewMode.Hex)
        {
            _hexView?.GotoOffset(offset);
        }
        else if (_state.ActiveView == ViewMode.Text)
        {
            _state.TextCursorOffset = offset;
            _state.TextSelectionAnchor = -1;
            // Reposition the text view so cursor is visible
            _state.TextTopOffset = Math.Max(0, offset - 4096);
            _textView?.InvalidateVisual();
        }
        else if (_state.ActiveView == ViewMode.Csv)
        {
            _state.HexCursorOffset = offset;
            _hexView?.GotoOffset(offset);
        }
    }

    // ─── Edit operations ───

    private async void DoCopy()
    {
        if (_state.Document is null) return;

        string? text = null;
        if (_state.ActiveView == ViewMode.Hex)
        {
            long selStart = _state.HexSelStart;
            long selEnd = _state.HexSelEnd;
            if (selStart >= 0 && selEnd >= selStart)
            {
                int len = (int)Math.Min(selEnd - selStart + 1, 65536);
                byte[] buf = new byte[len];
                _state.Document.Read(selStart, buf);
                text = string.Join(' ', buf.Select(b => b.ToString("X2")));
            }
        }
        else if (_state.ActiveView == ViewMode.Text)
        {
            long selStart = _state.TextSelStart;
            long selEnd = _state.TextSelEnd;
            if (selStart >= 0 && selEnd >= selStart)
            {
                int len = (int)Math.Min(selEnd - selStart + 1, 131072);
                byte[] buf = new byte[len];
                _state.Document.Read(selStart, buf);
                System.Text.Encoding enc = _state.Decoder.Encoding switch
                {
                    TextEncoding.Utf16Le => System.Text.Encoding.Unicode,
                    TextEncoding.Windows1252 => System.Text.Encoding.Latin1,
                    _ => System.Text.Encoding.UTF8
                };
                text = enc.GetString(buf);
            }
        }

        if (text is not null && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void DoPaste()
    {
        // TODO: implement clipboard paste
    }

    private void DoSelectAll()
    {
        if (_state.Document is null) return;

        if (_state.ActiveView == ViewMode.Hex)
        {
            _state.HexSelectionAnchor = 0;
            _state.HexCursorOffset = _state.FileLength - 1;
        }
        else if (_state.ActiveView == ViewMode.Text)
        {
            _state.TextSelectionAnchor = _state.BomLength;
            _state.TextCursorOffset = _state.FileLength;
        }

        _hexView?.InvalidateVisual();
        _textView?.InvalidateVisual();
    }

    private void DoDeleteCsvRows()
    {
        // TODO: implement CSV row deletion
    }

    private async void ShowCsvRecordDetail()
    {
        if (_state.Document is null || _state.CsvRowIndex is null) return;
        CsvRecordDetailDialog dialog = new(_state, _state.CsvCursorRow);
        await dialog.ShowDialog(this);
    }

    // ─── CSV dialogs ───

    private async Task ShowCsvSettingsDialog()
    {
        CsvSettingsDialog dialog = new(_state);
        await dialog.ShowDialog(this);
        if (dialog.Applied)
        {
            _csvView?.InvalidateVisual();
            UpdateStatusBar();
        }
    }

    // ─── Dialogs ───

    private void ShowAboutDialog()
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        string buildDate = "";
        foreach (System.Reflection.CustomAttributeData attr in typeof(MainWindow).Assembly.CustomAttributes)
        {
            if (attr.AttributeType == typeof(System.Reflection.AssemblyMetadataAttribute)
                && attr.ConstructorArguments.Count == 2
                && attr.ConstructorArguments[0].Value is string key
                && key == "BuildDateUtc"
                && attr.ConstructorArguments[1].Value is string val)
            {
                buildDate = val;
                break;
            }
        }

        Window aboutWindow = new()
        {
            Title = "About Leviathan",
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        StackPanel content = new()
        {
            Margin = new Thickness(24),
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock { Text = "Leviathan", FontSize = 22, FontWeight = FontWeight.Bold });
        content.Children.Add(new TextBlock { Text = "Large File Editor — Avalonia GUI", FontSize = 13, Opacity = 0.6 });
        content.Children.Add(new TextBlock { Text = $"Version {version}", FontSize = 12, Opacity = 0.5 });
        if (!string.IsNullOrEmpty(buildDate))
            content.Children.Add(new TextBlock { Text = $"Built {buildDate}", FontSize = 12, Opacity = 0.5 });

        Button closeBtn = new() { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        closeBtn.Click += (_, _) => aboutWindow.Close();
        content.Children.Add(closeBtn);

        aboutWindow.Content = content;
        aboutWindow.ShowDialog(this);
    }

    private void ShowKeyboardShortcuts()
    {
        Window helpWindow = new()
        {
            Title = "Keyboard Shortcuts",
            Width = 450,
            Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        string shortcuts = """
            Ctrl+O          Open file
            Ctrl+S          Save
            Ctrl+F          Find
            F3 / Shift+F3   Find Next / Previous
            Ctrl+G          Go to offset/line
            Ctrl+P          Command Palette
            F5              Hex view
            F6              Text view
            F7              CSV view
            F8              CSV settings
            Ctrl+C          Copy selection
            Ctrl+V          Paste
            Ctrl+A          Select all
            Home/End        Start/end of line
            Ctrl+Home/End   Start/end of file
            PgUp/PgDn       Page up/down
            """;

        ScrollViewer scroll = new()
        {
            Content = new TextBlock
            {
                Text = shortcuts,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 13,
                Margin = new Thickness(16)
            }
        };

        helpWindow.Content = scroll;
        helpWindow.ShowDialog(this);
    }

    private void ShowErrorDialog(string title, string message)
    {
        Window errorWindow = new()
        {
            Title = title,
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        Button okBtn = new() { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        okBtn.Click += (_, _) => errorWindow.Close();
        content.Children.Add(okBtn);
        errorWindow.Content = content;
        errorWindow.ShowDialog(this);
    }
}
