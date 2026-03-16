using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Leviathan.Core.Text;

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

    public MainWindow()
    {
        InitializeComponent();

        // Apply persisted settings
        _state.BytesPerRowSetting = _state.Settings.BytesPerRow;
        _state.WordWrap = _state.Settings.WordWrap;

        WireMenuEvents();
        BuildMruList();
        UpdateViewVisibility();
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

        ContentArea.Children.Add(_hexView);
        ContentArea.Children.Add(_textView);
        ContentArea.Children.Add(_csvView);
    }

    private void SwitchView(ViewMode mode)
    {
        if (_state.Document is null) return;

        if (mode == ViewMode.Csv && _state.CsvRowIndexer is null)
            _state.InitCsvView();

        _state.ActiveView = mode;
        EnsureViewControlsCreated();
        UpdateViewVisibility();
        UpdateStatusBar();

        MenuCsvSettings.IsVisible = mode == ViewMode.Csv;
    }

    private void UpdateViewVisibility()
    {
        bool hasFile = _state.Document is not null;
        WelcomeView.IsVisible = !hasFile;

        if (_hexView is not null)
        {
            _hexView.IsVisible = hasFile && _state.ActiveView == ViewMode.Hex;
            _textView!.IsVisible = hasFile && _state.ActiveView == ViewMode.Text;
            _csvView!.IsVisible = hasFile && _state.ActiveView == ViewMode.Csv;

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
        _textView?.InvalidateVisual();
    }

    private void SwitchEncoding(TextEncoding encoding)
    {
        _state.SwitchEncoding(encoding);
        _textView?.InvalidateVisual();
        _hexView?.InvalidateVisual();
        UpdateStatusBar();
    }

    private void SetBytesPerRow(int value)
    {
        _state.BytesPerRowSetting = value;
        _state.Settings.BytesPerRow = value;
        _state.Settings.Save();
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
        MruListPanel.Children.Clear();
        List<string> recent = _state.Settings.RecentFiles;

        if (recent.Count == 0)
        {
            MruSeparator.IsVisible = false;
            return;
        }

        MruSeparator.IsVisible = true;

        MruListPanel.Children.Add(new TextBlock
        {
            Text = "Recent Files:",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.6
        });

        for (int i = 0; i < Math.Min(9, recent.Count); i++)
        {
            string path = recent[i];
            string fileName = Path.GetFileName(path);
            int index = i + 1;

            Button btn = new()
            {
                Content = $"{index}. {fileName}",
                Tag = path,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(12, 4),
                Background = Brushes.Transparent
            };
            btn.Click += async (s, _) =>
            {
                if (s is Button b && b.Tag is string filePath)
                {
                    if (!await GuardUnsavedChanges()) return;
                    if (File.Exists(filePath))
                    {
                        _state.OpenFile(filePath);
                        OnFileOpened();
                    }
                }
            };
            MruListPanel.Children.Add(btn);
        }
    }

    // ─── Keyboard shortcuts ───

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Digit shortcuts for MRU on welcome screen
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
