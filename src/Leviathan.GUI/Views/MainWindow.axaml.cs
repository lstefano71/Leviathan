using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.GUI.Helpers;
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
    private CsvDetailPanel? _csvDetailPanel;
    private Grid? _csvOuterGrid;
    private GridSplitter? _csvSplitter;
    private DispatcherTimer? _indexingTimer;
    private FindBar? _findBar;
    private GotoBar? _gotoBar;
    private CommandPaletteOverlay? _commandPalette;
    private bool _suppressNextMenuFocusRestore;

    public MainWindow()
    {
        InitializeComponent();

        // Apply persisted settings
        _state.BytesPerRowSetting = _state.Settings.BytesPerRow;
        _state.WordWrap = _state.Settings.WordWrap;
        InitializeThemeAndFont();

        WireMenuEvents();
        WireWelcomeScreen();
        BuildFileMenuMru();
        BuildThemeMenu();
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
        MainMenu.Closed += (_, _) =>
        {
            if (_suppressNextMenuFocusRestore)
            {
                _suppressNextMenuFocusRestore = false;
                return;
            }

            FocusActiveViewAsync();
        };
    }

    private void WireMenuEvents()
    {
        MenuOpen.Click += async (_, _) => await ShowOpenDialog();
        MenuClose.Click += async (_, _) => await CloseFileAsync();
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

        MenuFind.Click += (_, _) =>
        {
            SuppressNextMenuFocusRestore();
            ShowFindBar();
        };
        MenuFindNext.Click += (_, _) => FindNext();
        MenuFindPrev.Click += (_, _) => FindPrevious();
        MenuGoto.Click += (_, _) =>
        {
            SuppressNextMenuFocusRestore();
            ShowGotoPalette();
        };
        MenuCsvSettings.Click += async (_, _) =>
        {
            SuppressNextMenuFocusRestore();
            await ShowCsvSettingsDialog();
        };
        MenuCopy.Click += (_, _) => DoCopy();
        MenuPaste.Click += (_, _) => DoPaste();
        MenuSelectAll.Click += (_, _) => DoSelectAll();
        MenuDeleteRows.Click += (_, _) => DoDeleteCsvRows();
        MenuSelectFont.Click += (_, _) => ShowFontPicker();
        MenuFontSizeUp.Click += (_, _) => AdjustFontSize(+1);
        MenuFontSizeDown.Click += (_, _) => AdjustFontSize(-1);
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

    private async Task CloseFileAsync()
    {
        if (_state.Document is null) return;
        if (!await GuardUnsavedChanges()) return;

        _state.CloseFile();

        // Hide any open overlays
        if (_findBar is not null) _findBar.IsVisible = false;
        if (_gotoBar is not null) _gotoBar.IsVisible = false;
        if (_commandPalette is not null) _commandPalette.IsVisible = false;

        // Close detail panel
        SetCsvDetailPanelVisible(false);

        UpdateViewVisibility();
        BuildFileMenuMru();
        UpdateTitle();
        UpdateStatusBar();
        RefreshCommandPaletteCommands();
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

        string fileName = _state.CurrentFilePath is not null
            ? System.IO.Path.GetFileName(_state.CurrentFilePath)
            : "Untitled";

        Window dialog = new()
        {
            Title = "Unsaved Changes",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        int result = -1; // -1 = cancel, 0 = don't save, 1 = save

        StackPanel panel = new()
        {
            Margin = new Thickness(20, 16, 20, 16),
            Spacing = 16
        };

        panel.Children.Add(new TextBlock
        {
            Text = $"Do you want to save the changes you made to \"{fileName}\"?",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Your changes will be lost if you don't save them.",
            Foreground = Brushes.Gray,
            FontSize = 12
        });

        StackPanel buttons = new()
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        Button saveBtn = new()
        {
            Content = "  _Save  ",
            IsDefault = true,
            Classes = { "accent" }
        };
        saveBtn.Click += (_, _) => { result = 1; dialog.Close(); };

        Button dontSaveBtn = new()
        {
            Content = "  _Don't Save  ",
            Foreground = Brushes.IndianRed
        };
        dontSaveBtn.Click += (_, _) => { result = 0; dialog.Close(); };

        Button cancelBtn = new()
        {
            Content = "  Cancel  ",
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => { result = -1; dialog.Close(); };

        buttons.Children.Add(saveBtn);
        buttons.Children.Add(dontSaveBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        dialog.Opened += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => saveBtn.Focus(),
                Avalonia.Threading.DispatcherPriority.Input);
        };

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
        BuildFileMenuMru();
        RefreshCommandPaletteCommands();
        UpdateTitle();
        UpdateStatusBar();
        UpdateViewModeChecks();
        UpdateEncodingChecks();
    }

    private void EnsureViewControlsCreated()
    {
        if (_hexView is not null) return;

        _hexView = new HexViewControl(_state);
        _textView = new TextViewControl(_state);
        _csvView = new CsvViewControl(_state);

        _hexView.StateChanged = UpdateStatusBar;
        _textView.StateChanged = UpdateStatusBar;
        _csvView.StateChanged = OnCsvStateChanged;
        _csvView.OnRecordDetail = ToggleCsvDetailPanel;
        _state.SearchInvalidated = () => _findBar?.UpdateMatchStatus();

        // Create scrollbars and compose each view + scrollbar in a Grid
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

        // CSV view + detail panel composed in an outer Grid with splitter
        Grid csvInner = CreateViewWithScrollBar(_csvView, sb =>
        {
            _csvView.ScrollBar = sb;
            sb.ValueChanged += _csvView.OnScrollBarValueChanged;
        });

        _csvDetailPanel = new CsvDetailPanel();
        _csvDetailPanel.CloseRequested = () => SetCsvDetailPanelVisible(false);
        _csvDetailPanel.IsVisible = false;

        GridSplitter splitter = new()
        {
            Width = 4,
            Background = Brushes.Transparent,
            IsVisible = false
        };
        _csvSplitter = splitter;

        Grid csvOuter = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*")
        };
        _csvOuterGrid = csvOuter;
        Grid.SetColumn(csvInner, 0);
        csvOuter.Children.Add(csvInner);
        csvOuter.Children.Add(splitter);
        csvOuter.Children.Add(_csvDetailPanel);

        ContentArea.Children.Add(csvOuter);
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

    // ─── Theme & Font ───

    /// <summary>Returns the persisted theme name for App.axaml.cs startup.</summary>
    internal string GetThemeName() => _state.Settings.ThemeName;

    private void InitializeThemeAndFont()
    {
        // Load user themes from themes/ directory
        string themesDir = Path.Combine(AppContext.BaseDirectory, "themes");
        _state.UserThemes = ColorTheme.LoadUserThemes(themesDir);

        // Resolve active theme
        ColorTheme theme = ColorTheme.FindById(_state.Settings.ThemeName, _state.UserThemes);
        _state.ActiveTheme = theme;

        // Resolve font
        _state.ContentFontSize = Math.Clamp(_state.Settings.FontSize, 8, 72);
        string fontFamily = _state.Settings.FontFamily;
        _state.ContentTypeface = new Typeface($"{fontFamily}, Consolas, Courier New, monospace");
    }

    private void SwitchTheme(string themeId)
    {
        ColorTheme theme = ColorTheme.FindById(themeId, _state.UserThemes);
        _state.ActiveTheme = theme;
        _state.Settings.ThemeName = theme.Id;
        _state.Settings.Save();

        // Switch Avalonia chrome variant
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme.BaseVariant;

        // Refresh custom view controls
        _hexView?.ApplyThemeAndFont();
        _textView?.ApplyThemeAndFont();
        _csvView?.ApplyThemeAndFont();

        BuildThemeMenu();
        RefreshCommandPaletteCommands();
    }

    private void BuildThemeMenu()
    {
        MenuTheme.Items.Clear();

        foreach (ColorTheme theme in ColorTheme.BuiltInThemes)
        {
            string id = theme.Id;
            MenuItem item = new()
            {
                Header = (_state.ActiveTheme.Id == id ? "● " : "○ ") + theme.Name
            };
            item.Click += (_, _) => SwitchTheme(id);
            MenuTheme.Items.Add(item);
        }

        if (_state.UserThemes.Count > 0)
        {
            MenuTheme.Items.Add(new Separator());
            foreach (ColorTheme theme in _state.UserThemes)
            {
                string id = theme.Id;
                MenuItem item = new()
                {
                    Header = (_state.ActiveTheme.Id == id ? "● " : "○ ") + theme.Name
                };
                item.Click += (_, _) => SwitchTheme(id);
                MenuTheme.Items.Add(item);
            }
        }
    }

    private void ShowFontPicker()
    {
        // Build a list of monospace font families
        List<string> monoFonts = GetMonospaceFonts();
        string currentFont = _state.Settings.FontFamily;

        // Create a simple selection window
        Window fontWindow = new()
        {
            Title = "Select Font",
            Width = 450,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        DockPanel panel = new();

        // Preview text
        TextBlock preview = new()
        {
            Text = "AaBbCcDdEe 0123456789 {}[]|\\",
            FontFamily = new FontFamily(currentFont),
            FontSize = _state.ContentFontSize,
            Margin = new Thickness(16, 8),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };
        DockPanel.SetDock(preview, Avalonia.Controls.Dock.Bottom);
        panel.Children.Add(preview);

        // Font list
        ListBox fontList = new();
        foreach (string name in monoFonts)
        {
            ListBoxItem item = new()
            {
                Content = name,
                Tag = name
            };
            fontList.Items.Add(item);
            if (string.Equals(name, currentFont, StringComparison.OrdinalIgnoreCase))
                fontList.SelectedItem = item;
        }

        fontList.SelectionChanged += (_, _) =>
        {
            if (fontList.SelectedItem is ListBoxItem { Tag: string fontName })
            {
                preview.FontFamily = new FontFamily(fontName);
                ApplyFont(fontName, _state.ContentFontSize);
            }
        };

        fontList.DoubleTapped += (_, _) => fontWindow.Close();

        panel.Children.Add(fontList);
        fontWindow.Content = panel;
        fontWindow.ShowDialog(this);
    }

    private void ApplyFont(string fontFamily, double fontSize)
    {
        fontSize = Math.Clamp(fontSize, 8, 72);
        _state.ContentTypeface = new Typeface($"{fontFamily}, Consolas, Courier New, monospace");
        _state.ContentFontSize = fontSize;
        _state.Settings.FontFamily = fontFamily;
        _state.Settings.FontSize = (int)fontSize;
        _state.Settings.Save();

        _hexView?.ApplyThemeAndFont();
        _textView?.ApplyThemeAndFont();
        _csvView?.ApplyThemeAndFont();

        RefreshCommandPaletteCommands();
    }

    private void AdjustFontSize(int delta)
    {
        double newSize = Math.Clamp(_state.ContentFontSize + delta, 8, 72);
        ApplyFont(_state.Settings.FontFamily, newSize);
    }

    /// <summary>
    /// Enumerates installed fonts and returns those that appear to be monospaced.
    /// Uses a heuristic: compares the width of 'W' and 'i' glyphs.
    /// </summary>
    private static List<string> GetMonospaceFonts()
    {
        // Well-known monospace fonts to always include at the top
        HashSet<string> knownMono = new(StringComparer.OrdinalIgnoreCase)
        {
            "Consolas", "Courier New", "Cascadia Code", "Cascadia Mono",
            "JetBrains Mono", "Fira Code", "Fira Mono", "Source Code Pro",
            "DejaVu Sans Mono", "Ubuntu Mono", "Hack", "Inconsolata",
            "Liberation Mono", "Menlo", "Monaco", "SF Mono", "IBM Plex Mono",
            "Roboto Mono", "Noto Sans Mono", "Droid Sans Mono",
            "Anonymous Pro", "Input Mono"
        };

        SortedSet<string> result = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (FontFamily family in FontManager.Current.SystemFonts)
            {
                string name = family.Name;
                if (knownMono.Contains(name))
                {
                    result.Add(name);
                    continue;
                }

                // Heuristic: measure 'W' and 'i' — if same width, it's monospace
                try
                {
                    Typeface typeface = new(name);
                    FormattedText wide = new("W", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, 14, Brushes.White);
                    FormattedText narrow = new("i", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, 14, Brushes.White);
                    if (Math.Abs(wide.Width - narrow.Width) < 0.1)
                        result.Add(name);
                }
                catch
                {
                    // Skip fonts that can't be measured
                }
            }
        }
        catch
        {
            // If font enumeration fails, return known fonts
        }

        // Ensure at least Consolas is present
        if (result.Count == 0)
            result.Add("Consolas");

        return [.. result];
    }

    private void UpdateViewVisibility()
    {
        bool hasFile = _state.Document is not null;
        WelcomeScreen.IsVisible = !hasFile;

        // Hide menu bar and status bar on the welcome screen
        MainMenu.IsVisible = hasFile;
        StatusBar.IsVisible = hasFile;

        if (!hasFile)
            PopulateWelcomeScreen();

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
                FocusActiveView();
                activeView.InvalidateVisual();
            }
        }
    }

    private void FocusActiveView()
    {
        if (_state.Document is null || _hexView is null || _textView is null || _csvView is null)
            return;

        Control activeView = _state.ActiveView switch
        {
            ViewMode.Hex => _hexView,
            ViewMode.Text => _textView,
            ViewMode.Csv => _csvView,
            _ => _hexView
        };

        if (activeView.IsVisible)
            activeView.Focus();
    }

    private void FocusActiveViewAsync()
    {
        Dispatcher.UIThread.Post(FocusActiveView, DispatcherPriority.Input);
    }

    private void SuppressNextMenuFocusRestore()
    {
        _suppressNextMenuFocusRestore = true;
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
        if (value > 0)
            _state.BytesPerRow = value;
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

    // ─── Welcome screen ───

    private void WireWelcomeScreen()
    {
        WelcomeScreen.FileSelected = path => _ = OpenRecentFileAsync(path);
        WelcomeScreen.OpenFileRequested = () => _ = ShowOpenDialog();
        WelcomeScreen.PinChanged = (path, pinned) =>
        {
            if (pinned)
                _state.Settings.PinFile(path);
            else
                _state.Settings.UnpinFile(path);
            PopulateWelcomeScreen();
            BuildFileMenuMru();
        };
        WelcomeScreen.FileRemoved = path =>
        {
            _state.Settings.RemoveFile(path);
            PopulateWelcomeScreen();
            BuildFileMenuMru();
        };

        // Populate version info in the right-hand panel
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        string? buildDate = null;
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
        WelcomeScreen.SetVersionInfo(version, buildDate);
    }

    private void PopulateWelcomeScreen()
    {
        WelcomeScreen.Populate(_state.Settings.PinnedFiles, _state.Settings.RecentFiles);
    }

    // ─── File menu MRU ───

    private void BuildFileMenuMru()
    {
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

        // Combine pinned + recent for the file menu
        List<string> allFiles = [.. _state.Settings.PinnedFiles, .. _state.Settings.RecentFiles];

        if (allFiles.Count == 0)
        {
            MruSeparator.IsVisible = false;
        }
        else
        {
            MruSeparator.IsVisible = true;
            int insertIdx = FileMenu.Items.IndexOf(MruSeparator);
            for (int i = 0; i < Math.Min(9, allFiles.Count); i++)
            {
                string path = allFiles[i];
                string fileName = Path.GetFileName(path);
                int number = i + 1;
                bool isPinned = _state.Settings.PinnedFiles.Contains(path);
                string prefix = isPinned ? "📌 " : "";
                MenuItem mruItem = new() { Header = $"_{number} {prefix}{fileName}", StaysOpenOnClick = false };
                string capturedPath = path;
                mruItem.Click += async (_, _) => await OpenRecentFileAsync(capturedPath, closeFileMenu: true);
                FileMenu.Items.Insert(insertIdx + i, mruItem);
            }
        }
    }

    private async Task OpenRecentFileAsync(string path, bool closeFileMenu = false)
    {
        if (closeFileMenu)
            MainMenu.Close();

        if (!await GuardUnsavedChanges())
            return;

        if (!File.Exists(path))
            return;

        _state.OpenFile(path);
        OnFileOpened();
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
                case Key.W:
                    _ = CloseFileAsync();
                    e.Handled = true;
                    return;
            }
        }

        // F2 for CSV record detail panel toggle
        if (e.Key == Key.F2 && _state.ActiveView == ViewMode.Csv)
        {
            ToggleCsvDetailPanel();
            e.Handled = true;
            return;
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
        RefreshCommandPaletteCommands();
        _commandPalette!.Show();
    }

    private void ShowGotoPalette()
    {
        EnsureOverlaysCreated();
        RefreshCommandPaletteCommands();
        _commandPalette!.ShowGoto();
    }

    private void EnsureOverlaysCreated()
    {
        if (_findBar is not null) return;

        _findBar = new FindBar(_state, StartSearch, FindNext, FindPrevious, FocusActiveViewAsync);
        _findBar.IsVisible = false;

        _gotoBar = new GotoBar(_state,
            offset => _hexView?.GotoOffset(offset),
            line => _textView?.GotoLine(line));
        _gotoBar.IsVisible = false;

        _commandPalette = new CommandPaletteOverlay(_state,
            offset => _hexView?.GotoOffset(offset),
            (line, column) => _textView?.GotoLine(line, column),
            offset => _textView?.GotoOffset(offset),
            row => _csvView?.GotoRow(row),
            FocusActiveViewAsync);
        _commandPalette.IsVisible = false;
        RefreshCommandPaletteCommands();

        ContentArea.Children.Add(_findBar);
        ContentArea.Children.Add(_gotoBar);
        ContentArea.Children.Add(_commandPalette);
    }

    private void RefreshCommandPaletteCommands()
    {
        if (_commandPalette is null) return;

        _commandPalette.ClearCommands();
        _commandPalette.RegisterCommand("Open File", "Open a file (Ctrl+O)", () => _ = ShowOpenDialog());
        _commandPalette.RegisterCommand("Close File", "Close current file (Ctrl+W)", () => _ = CloseFileAsync());
        _commandPalette.RegisterCommand("Save", "Save the file (Ctrl+S)", SaveFile, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.ActiveView == ViewMode.Hex ? "● Hex View" : "○ Hex View",
            "Switch to hex view (F5)",
            () => SwitchView(ViewMode.Hex),
            searchName: "Hex View",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.ActiveView == ViewMode.Text ? "● Text View" : "○ Text View",
            "Switch to text view (F6)",
            () => SwitchView(ViewMode.Text),
            searchName: "Text View",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.ActiveView == ViewMode.Csv ? "● CSV View" : "○ CSV View",
            "Switch to CSV view (F7)",
            () => SwitchView(ViewMode.Csv),
            searchName: "CSV View",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Find", "Search in file (Ctrl+F)", ShowFindBar);
        _commandPalette.RegisterCommand("Find Next", "Go to next match (F3)", FindNext, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Find Previous", "Go to previous match (Shift+F3)", FindPrevious, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Go to", "Jump to offset, line, or row (Ctrl+G)", ShowGotoPalette, closeOnExecute: false);
        _commandPalette.RegisterCommand(
            () => _state.WordWrap ? "☑ Line Wrap" : "☐ Line Wrap",
            "Wrap long lines in text view",
            ToggleWordWrap,
            searchName: "Line Wrap",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.Decoder.Encoding == TextEncoding.Utf8 ? "● Encoding: UTF-8" : "○ Encoding: UTF-8",
            "Switch to UTF-8 encoding",
            () => SwitchEncoding(TextEncoding.Utf8),
            searchName: "Encoding UTF-8",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.Decoder.Encoding == TextEncoding.Utf16Le ? "● Encoding: UTF-16 LE" : "○ Encoding: UTF-16 LE",
            "Switch to UTF-16 Little Endian encoding",
            () => SwitchEncoding(TextEncoding.Utf16Le),
            searchName: "Encoding UTF-16 LE",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.Decoder.Encoding == TextEncoding.Windows1252 ? "● Encoding: Windows-1252" : "○ Encoding: Windows-1252",
            "Switch to Windows-1252 (Latin-1) encoding",
            () => SwitchEncoding(TextEncoding.Windows1252),
            searchName: "Encoding Windows-1252",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Copy", "Copy selection (Ctrl+C)", DoCopy, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Paste", "Paste from clipboard (Ctrl+V)", DoPaste, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Select All", "Select entire file (Ctrl+A)", DoSelectAll, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("About", "About Leviathan", ShowAboutDialog);
        _commandPalette.RegisterCommand("Keyboard Shortcuts", "Show key combinations (F1)", ShowKeyboardShortcuts);

        // Theme commands
        foreach (ColorTheme theme in ColorTheme.BuiltInThemes)
        {
            string id = theme.Id;
            string name = theme.Name;
            _commandPalette.RegisterCommand(
                () => _state.ActiveTheme.Id == id ? $"● Theme: {name}" : $"○ Theme: {name}",
                $"Switch to {name} color theme",
                () => SwitchTheme(id),
                searchName: $"Theme {name}",
                restoreFocusAfterExecute: true);
        }

        foreach (ColorTheme theme in _state.UserThemes)
        {
            string id = theme.Id;
            string name = theme.Name;
            _commandPalette.RegisterCommand(
                () => _state.ActiveTheme.Id == id ? $"● Theme: {name}" : $"○ Theme: {name}",
                $"Switch to {name} color theme (user)",
                () => SwitchTheme(id),
                searchName: $"Theme {name}",
                restoreFocusAfterExecute: true);
        }

        // Font commands
        _commandPalette.RegisterCommand("Change Font", "Select content font family", ShowFontPicker);
        _commandPalette.RegisterCommand(
            () => $"Font Size: {_state.ContentFontSize:0}",
            "Current font size (use + and - to adjust)",
            () => { },
            searchName: "Font Size",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Font Size +", "Increase font size", () => AdjustFontSize(+1), restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Font Size -", "Decrease font size", () => AdjustFontSize(-1), restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.CsvDetailPanelVisible ? "☑ Record Detail Panel" : "☐ Record Detail Panel",
            "Toggle CSV record detail side panel (F2)",
            ToggleCsvDetailPanel,
            searchName: "Record Detail Panel",
            restoreFocusAfterExecute: true);

        foreach (string pinnedPath in _state.Settings.PinnedFiles)
        {
            string capturedPath = pinnedPath;
            string fileName = Path.GetFileName(capturedPath);
            _commandPalette.RegisterCommand(
                () => $"📌 Open: {fileName}",
                capturedPath,
                () => _ = OpenRecentFileAsync(capturedPath),
                searchName: $"Open Pinned {fileName}");
        }

        foreach (string recentPath in _state.Settings.RecentFiles.Take(20))
        {
            string capturedPath = recentPath;
            string fileName = Path.GetFileName(capturedPath);
            _commandPalette.RegisterCommand(
                () => $"Open Recent: {fileName}",
                capturedPath,
                () => _ = OpenRecentFileAsync(capturedPath),
                searchName: $"Open Recent {fileName}");
        }
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

        bool isRegex = _state.FindRegexMode && !_state.FindHexMode;
        bool isHex = _state.FindHexMode;
        bool caseSensitive = isHex || _state.FindCaseSensitive;
        bool wholeWord = _state.FindWholeWord;
        Core.Document doc = _state.Document;
        ITextDecoder decoder = _state.Decoder;
        string query = _state.FindInput;

        // Validate pattern before launching background task
        if (isHex)
        {
            try { SearchEngine.ParseHexPattern(query); }
            catch (FormatException)
            {
                _state.IsSearching = false;
                _state.SearchStatus = "Invalid hex pattern";
                _findBar?.UpdateMatchStatus();
                return;
            }
        }
        else if (isRegex && !SearchEngine.IsValidRegex(query))
        {
            _state.IsSearching = false;
            _state.SearchStatus = "Invalid regex";
            _findBar?.UpdateMatchStatus();
            return;
        }

        _state.SearchTask = Task.Run(() =>
        {
            IEnumerable<SearchResult> source;
            if (isRegex)
            {
                source = SearchEngine.FindAllRegex(doc, decoder, query, caseSensitive, cts.Token);
            }
            else
            {
                byte[] pattern = isHex
                    ? SearchEngine.ParseHexPattern(query)
                    : decoder.EncodeString(query);
                if (pattern.Length == 0) return;
                source = SearchEngine.FindAll(doc, pattern, caseSensitive, wholeWord, cts.Token);
            }

            // Stream results in batches for responsive UI
            const int BatchSize = 500;
            List<SearchResult> batch = new(BatchSize);
            int totalSoFar = 0;

            foreach (SearchResult r in source)
            {
                batch.Add(r);
                if (batch.Count >= BatchSize)
                {
                    List<SearchResult> toPost = batch;
                    batch = new List<SearchResult>(BatchSize);
                    int batchTotal = totalSoFar + toPost.Count;
                    totalSoFar = batchTotal;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _state.SearchResults.AddRange(toPost);
                        if (_state.CurrentMatchIndex < 0 && _state.SearchResults.Count > 0)
                        {
                            _state.CurrentMatchIndex = 0;
                            NavigateToMatch(0);
                        }
                        _findBar?.UpdateMatchStatus();
                        _hexView?.InvalidateVisual();
                        _textView?.InvalidateVisual();
                        _csvView?.InvalidateVisual();
                    });
                }
            }

            // Post final batch
            List<SearchResult> finalBatch = batch;
            Dispatcher.UIThread.Post(() =>
            {
                if (finalBatch.Count > 0)
                    _state.SearchResults.AddRange(finalBatch);

                _state.IsSearching = false;
                if (_state.CurrentMatchIndex < 0 && _state.SearchResults.Count > 0)
                {
                    _state.CurrentMatchIndex = 0;
                    NavigateToMatch(0);
                }
                _state.SearchStatus = _state.SearchResults.Count > 0
                    ? $"{_state.SearchResults.Count} matches"
                    : "No matches";
                _findBar?.UpdateMatchStatus();

                _hexView?.InvalidateVisual();
                _textView?.InvalidateVisual();
                _csvView?.InvalidateVisual();
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
            _textView?.GotoOffset(offset);
        }
        else if (_state.ActiveView == ViewMode.Csv)
        {
            _csvView?.GotoOffset(offset);
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

    private void ToggleCsvDetailPanel()
    {
        SetCsvDetailPanelVisible(!_state.CsvDetailPanelVisible);
    }

    private void SetCsvDetailPanelVisible(bool visible)
    {
        _state.CsvDetailPanelVisible = visible;
        if (_csvDetailPanel is null || _csvOuterGrid is null || _csvSplitter is null) return;

        _csvDetailPanel.IsVisible = visible;
        _csvSplitter.IsVisible = visible;

        // Toggle column definitions: "*" when hidden, "*,Auto,300" when visible
        if (visible)
        {
            _csvOuterGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto,300");
            Grid.SetColumn(_csvSplitter, 1);
            Grid.SetColumn(_csvDetailPanel, 2);
            _csvDetailPanel.UpdateRecord(_state, _csvView!);
        }
        else
        {
            Grid.SetColumn(_csvSplitter, 0);
            Grid.SetColumn(_csvDetailPanel, 0);
            _csvOuterGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _csvDetailPanel.ClearPanel();
        }

        FocusActiveViewAsync();
    }

    private void OnCsvStateChanged()
    {
        UpdateStatusBar();
        if (_state.CsvDetailPanelVisible && _csvDetailPanel is { IsVisible: true } && _csvView is not null)
            _csvDetailPanel.UpdateRecord(_state, _csvView);
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
        FocusActiveViewAsync();
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
        helpWindow.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                helpWindow.Close();
                e.Handled = true;
            }
        };
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
