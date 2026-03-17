using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.GUI.Helpers;
using Leviathan.GUI.Widgets;

using System.Diagnostics;
using System.Reflection;

namespace Leviathan.GUI.Views;

/// <summary>
/// Main application window. Hosts menu bar, status bar, and the active view.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string OnlineHelpUrl = "https://github.com/lstefano71/Leviathan/blob/main/docs/help.md";
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
    private string? _lineEndingCachePath;
    private long _lineEndingCacheLength = -1;
    private string _lineEndingCache = "";
    private readonly BuildIdentity _buildIdentity;
    private long _lastIndexedLineCount = -1;
    private bool _lastLineIndexComplete = true;
    private long _lastIndexedRowCount = -1;
    private bool _lastRowIndexComplete = true;

    public MainWindow()
    {
        InitializeComponent();
        _buildIdentity = BuildIdentity.Create(typeof(MainWindow).Assembly);

        // Apply persisted settings
        _state.BytesPerRowSetting = _state.Settings.BytesPerRow;
        _state.WordWrap = _state.Settings.WordWrap;
        _state.IsReadOnly = _state.Settings.StartReadOnly;
        InitializeThemeAndFont();

        WireMenuEvents();
        WireViewTabs();
        WireStatusBarInteractions();
        WireWelcomeScreen();
        BuildFileMenuMru();
        BuildThemeMenu();
        UpdateViewVisibility();
        UpdateWordWrapCheck();
        UpdateGutterCheck();
        UpdateDecimalOffsetsCheck();
        UpdateBprChecks();
        UpdateReadOnlyChecks();
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
        if (args.Length > 1 && File.Exists(args[1])) {
            _state.OpenFile(args[1]);
            OnFileOpened();
        }

        // Drag-and-drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        SizeChanged += (_, _) => UpdateStatusBar();

        // Keyboard shortcuts not bound to menu items
        KeyDown += OnGlobalKeyDown;
        MainMenu.Closed += (_, _) => {
            if (_suppressNextMenuFocusRestore) {
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
        MenuGutter.Click += (_, _) => ToggleGutter();
        MenuDecimalOffsets.Click += (_, _) => ToggleDecimalOffsets();

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

        MenuFind.Click += (_, _) => {
            SuppressNextMenuFocusRestore();
            ShowFindBar();
        };
        MenuCommandPalette.Click += (_, _) => {
            SuppressNextMenuFocusRestore();
            ShowCommandPalette();
        };
        MenuFindNext.Click += (_, _) => FindNext();
        MenuFindPrev.Click += (_, _) => FindPrevious();
        MenuGoto.Click += (_, _) => {
            SuppressNextMenuFocusRestore();
            ShowGotoPalette();
        };
        MenuCsvSettings.Click += async (_, _) => {
            SuppressNextMenuFocusRestore();
            await ShowCsvSettingsDialog();
        };
        MenuCut.Click += (_, _) => DoCut();
        MenuCopy.Click += (_, _) => DoCopy();
        MenuPaste.Click += (_, _) => DoPaste();
        MenuSelectAll.Click += (_, _) => DoSelectAll();
        MenuDeleteRows.Click += (_, _) => DoDeleteCsvRows();
        MenuReadOnlyMode.Click += (_, _) => ToggleReadOnlyMode();
        MenuStartReadOnly.Click += (_, _) => ToggleStartReadOnly();
        MenuSelectFont.Click += (_, _) => ShowFontPicker();
        MenuFontSizeUp.Click += (_, _) => AdjustFontSize(+1);
        MenuFontSizeDown.Click += (_, _) => AdjustFontSize(-1);
    }

    private void WireStatusBarInteractions()
    {
        SbEncodingButton.Click += (_, _) => OpenEncodingStatusMenu();
        SbViewModeButton.Click += (_, _) => OpenViewModeStatusMenu();
        SbLinesButton.Click += (_, _) => {
            if (_state.ActiveView == ViewMode.Text)
                ToggleWordWrap();
            else if (_state.ActiveView == ViewMode.Hex)
                OpenBytesPerRowStatusMenu();
        };
        SbPositionButton.Click += (_, _) => {
            if (_state.ActiveView == ViewMode.Hex)
                ToggleDecimalOffsets();
        };
    }

    private void WireViewTabs()
    {
        TabHex.Click += (_, _) => SwitchView(ViewMode.Hex);
        TabText.Click += (_, _) => SwitchView(ViewMode.Text);
        TabCsv.Click += (_, _) => SwitchView(ViewMode.Csv);
        UpdateViewTabsSelection();
    }

    // ─── File operations ───

    private async Task ShowOpenDialog()
    {
        if (!await GuardUnsavedChanges()) return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions {
                Title = "Open File",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.All]
            });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) {
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
        _findBar?.IsVisible = false;
        _gotoBar?.IsVisible = false;
        _commandPalette?.IsVisible = false;

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

        if (_state.CurrentFilePath is { } path) {
            if (!_state.TrySave(path, out string? error))
                ShowErrorDialog("Save Error", error ?? "Unknown error");
            else
                UpdateStatusBar();
        } else {
            _ = ShowSaveAsDialog();
        }
    }

    private async Task ShowSaveAsDialog()
    {
        if (_state.Document is null) return;

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions {
                Title = "Save As",
                SuggestedFileName = _state.CurrentFilePath is not null
                    ? Path.GetFileName(_state.CurrentFilePath)
                    : "untitled"
            });

        if (file?.TryGetLocalPath() is { } path) {
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

        Window dialog = new() {
            Title = "Unsaved Changes",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        int result = -1; // -1 = cancel, 0 = don't save, 1 = save

        StackPanel panel = new() {
            Margin = new Thickness(20, 16, 20, 16),
            Spacing = 16
        };

        panel.Children.Add(new TextBlock {
            Text = $"Do you want to save the changes you made to \"{fileName}\"?",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });

        panel.Children.Add(new TextBlock {
            Text = "Your changes will be lost if you don't save them.",
            Foreground = Brushes.Gray,
            FontSize = 12
        });

        StackPanel buttons = new() {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        Button saveBtn = new() {
            Content = "  _Save  ",
            IsDefault = true,
            Classes = { "accent" }
        };
        saveBtn.Click += (_, _) => { result = 1; dialog.Close(); };

        Button dontSaveBtn = new() {
            Content = "  _Don't Save  ",
            Foreground = Brushes.IndianRed
        };
        dontSaveBtn.Click += (_, _) => { result = 0; dialog.Close(); };

        Button cancelBtn = new() {
            Content = "  Cancel  ",
            IsCancel = true
        };
        cancelBtn.Click += (_, _) => { result = -1; dialog.Close(); };

        buttons.Children.Add(saveBtn);
        buttons.Children.Add(dontSaveBtn);
        buttons.Children.Add(cancelBtn);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        dialog.Opened += (_, _) => {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => saveBtn.Focus(),
                Avalonia.Threading.DispatcherPriority.Input);
        };

        await dialog.ShowDialog(this);

        if (result == 1) {
            SaveFile();
            return !_state.IsModified; // Only proceed if save succeeded
        }

        return result == 0;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_state.IsModified) {
            e.Cancel = true;
            if (await GuardUnsavedChanges()) {
                _state.CancelSearch();
                _state.CsvRowIndexer?.Dispose();
                _state.Indexer?.Dispose();
                _state.Document?.Dispose();
                Closing -= OnWindowClosing;
                Close();
            }
        } else {
            _state.CancelSearch();
            _state.CsvRowIndexer?.Dispose();
            _state.Indexer?.Dispose();
            _state.Document?.Dispose();
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // DragDrop API migration in progress in Avalonia 11.x
        if (e.Data is Avalonia.Input.IDataObject dataObj) {
            IEnumerable<IStorageItem>? files = dataObj.GetFiles();
#pragma warning restore CS0618
            if (files is not null) {
                foreach (IStorageItem item in files) {
                    if (item is IStorageFile file && file.TryGetLocalPath() is { } path) {
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
        if (_state.CurrentFilePath is { } fp) {
            string ext = Path.GetExtension(fp).ToLowerInvariant();
            if (ext is ".csv" or ".tsv" or ".tab") {
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
        ContentArea.Children.Add(CreateViewWithScrollBar(_hexView, sb => {
            _hexView.ScrollBar = sb;
            sb.ValueChanged += _hexView.OnScrollBarValueChanged;
        }));
        ContentArea.Children.Add(CreateViewWithScrollBar(_textView, sb => {
            _textView.ScrollBar = sb;
            sb.ValueChanged += _textView.OnScrollBarValueChanged;
        }));

        // CSV view + detail panel composed in an outer Grid with splitter
        Grid csvInner = CreateViewWithScrollBar(_csvView, sb => {
            _csvView.ScrollBar = sb;
            sb.ValueChanged += _csvView.OnScrollBarValueChanged;
        });

        _csvDetailPanel = new CsvDetailPanel {
            CloseRequested = () => SetCsvDetailPanelVisible(false),
            IsVisible = false
        };

        GridSplitter splitter = new() {
            Width = 4,
            Background = Brushes.Transparent,
            IsVisible = false
        };
        _csvSplitter = splitter;

        Grid csvOuter = new() {
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
        Grid grid = new() {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        Grid.SetColumn(view, 0);
        grid.Children.Add(view);

        Avalonia.Controls.Primitives.ScrollBar sb = new() {
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

        EnsureViewControlsCreated();

        if (mode == ViewMode.Csv && _state.CsvRowIndexer is null)
            _state.InitCsvView();

        ViewMode previousMode = _state.ActiveView;
        long anchorOffset = CaptureViewAnchorOffset(previousMode);
        _state.ActiveView = mode;
        if (mode != previousMode)
            ApplyAnchorOffsetToView(mode, anchorOffset);

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

        foreach (ColorTheme theme in ColorTheme.BuiltInThemes) {
            string id = theme.Id;
            MenuItem item = new() {
                Header = (_state.ActiveTheme.Id == id ? "● " : "○ ") + theme.Name
            };
            item.Click += (_, _) => SwitchTheme(id);
            MenuTheme.Items.Add(item);
        }

        if (_state.UserThemes.Count > 0) {
            MenuTheme.Items.Add(new Separator());
            foreach (ColorTheme theme in _state.UserThemes) {
                string id = theme.Id;
                MenuItem item = new() {
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
        Window fontWindow = new() {
            Title = "Select Font",
            Width = 450,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        DockPanel panel = new();

        // Preview text
        TextBlock preview = new() {
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
        foreach (string name in monoFonts) {
            ListBoxItem item = new() {
                Content = name,
                Tag = name
            };
            fontList.Items.Add(item);
            if (string.Equals(name, currentFont, StringComparison.OrdinalIgnoreCase))
                fontList.SelectedItem = item;
        }

        fontList.SelectionChanged += (_, _) => {
            if (fontList.SelectedItem is ListBoxItem { Tag: string fontName }) {
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

        try {
            foreach (FontFamily family in FontManager.Current.SystemFonts) {
                string name = family.Name;
                if (knownMono.Contains(name)) {
                    result.Add(name);
                    continue;
                }

                // Heuristic: measure 'W' and 'i' — if same width, it's monospace
                try {
                    Typeface typeface = new(name);
                    FormattedText wide = new("W", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, 14, Brushes.White);
                    FormattedText narrow = new("i", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, 14, Brushes.White);
                    if (Math.Abs(wide.Width - narrow.Width) < 0.1)
                        result.Add(name);
                } catch {
                    // Skip fonts that can't be measured
                }
            }
        } catch {
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

        // Hide editor chrome on the welcome screen
        MainMenu.IsVisible = hasFile;
        ViewTabsBar.IsVisible = hasFile;
        StatusBar.IsVisible = hasFile;
        MenuCommandPalette.IsEnabled = hasFile;
        UpdateViewTabsSelection();

        if (!hasFile)
            PopulateWelcomeScreen();

        if (_hexView is not null) {
            // Toggle the Grid wrappers (parent of each view control)
            ((Control)_hexView.Parent!).IsVisible = hasFile && _state.ActiveView == ViewMode.Hex;
            ((Control)_textView!.Parent!).IsVisible = hasFile && _state.ActiveView == ViewMode.Text;
            ((Control)_csvView!.Parent!).IsVisible = hasFile && _state.ActiveView == ViewMode.Csv;

            if (hasFile) {
                Control activeView = _state.ActiveView switch {
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

        Control activeView = _state.ActiveView switch {
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

    private void ToggleGutter()
    {
        _state.GutterVisible = !_state.GutterVisible;
        UpdateGutterCheck();
        _hexView?.InvalidateVisual();
        _textView?.InvalidateVisual();
        _csvView?.InvalidateVisual();
    }

    private void UpdateGutterCheck()
    {
        MenuGutter.Icon = _state.GutterVisible
            ? new TextBlock { Text = "✓", FontSize = 12 }
            : null;
    }

    private void ToggleDecimalOffsets()
    {
        _state.HexOffsetDecimal = !_state.HexOffsetDecimal;
        UpdateDecimalOffsetsCheck();
        _hexView?.InvalidateVisual();
        UpdateStatusBar();
    }

    private void UpdateDecimalOffsetsCheck()
    {
        MenuDecimalOffsets.Icon = _state.HexOffsetDecimal
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
        UpdateViewTabsSelection();
    }

    private void UpdateViewTabsSelection()
    {
        bool hasFile = _state.Document is not null;
        TabHex.IsEnabled = hasFile && _state.ActiveView != ViewMode.Hex;
        TabText.IsEnabled = hasFile && _state.ActiveView != ViewMode.Text;
        TabCsv.IsEnabled = hasFile && _state.ActiveView != ViewMode.Csv;

        TabHex.FontWeight = _state.ActiveView == ViewMode.Hex ? FontWeight.SemiBold : FontWeight.Normal;
        TabText.FontWeight = _state.ActiveView == ViewMode.Text ? FontWeight.SemiBold : FontWeight.Normal;
        TabCsv.FontWeight = _state.ActiveView == ViewMode.Csv ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private long CaptureViewAnchorOffset(ViewMode mode)
    {
        Func<long, long>? csvRowOffsetProvider = _csvView is null ? null : _csvView.GetRowByteOffset;
        return ViewAnchorSync.CaptureSourceAnchorOffset(_state, mode, csvRowOffsetProvider);
    }

    private void ApplyAnchorOffsetToView(ViewMode mode, long anchorOffset)
    {
        if (_state.Document is null)
            return;

        long targetOffset = ViewAnchorSync.MapAnchorToTargetOffset(_state, mode, anchorOffset);

        switch (mode) {
            case ViewMode.Hex:
                _hexView?.SyncTopOffset(targetOffset);
                break;
            case ViewMode.Text:
                _textView?.SyncTopOffset(targetOffset);
                break;
            case ViewMode.Csv:
                _csvView?.SyncTopOffset(targetOffset);
                break;
        }
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

    private void ToggleReadOnlyMode()
    {
        _state.IsReadOnly = !_state.IsReadOnly;
        UpdateReadOnlyChecks();
        UpdateStatusBar();
    }

    private void ToggleStartReadOnly()
    {
        _state.Settings.StartReadOnly = !_state.Settings.StartReadOnly;
        _state.Settings.Save();
        UpdateReadOnlyChecks();
    }

    private void UpdateReadOnlyChecks()
    {
        MenuReadOnlyMode.Icon = _state.IsReadOnly
            ? new TextBlock { Text = "✓", FontSize = 12 }
            : null;
        MenuStartReadOnly.Icon = _state.Settings.StartReadOnly
            ? new TextBlock { Text = "✓", FontSize = 12 }
            : null;
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
        UpdateStatusBar();
    }

    private void OpenEncodingStatusMenu()
    {
        if (_state.Document is null || _state.ActiveView == ViewMode.Csv)
            return;

        TextEncoding current = _state.Decoder.Encoding;
        MenuItem utf8 = new() { Header = "UTF-8", Icon = current == TextEncoding.Utf8 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        utf8.Click += (_, _) => SwitchEncoding(TextEncoding.Utf8);
        MenuItem utf16 = new() { Header = "UTF-16 LE", Icon = current == TextEncoding.Utf16Le ? new TextBlock { Text = "●", FontSize = 10 } : null };
        utf16.Click += (_, _) => SwitchEncoding(TextEncoding.Utf16Le);
        MenuItem win1252 = new() { Header = "Windows-1252", Icon = current == TextEncoding.Windows1252 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        win1252.Click += (_, _) => SwitchEncoding(TextEncoding.Windows1252);
        OpenStatusBarMenu(SbEncodingButton, utf8, utf16, win1252);
    }

    private void OpenViewModeStatusMenu()
    {
        if (_state.Document is null)
            return;

        MenuItem hex = new() { Header = "Hex View", Icon = _state.ActiveView == ViewMode.Hex ? new TextBlock { Text = "●", FontSize = 10 } : null };
        hex.Click += (_, _) => SwitchView(ViewMode.Hex);
        MenuItem text = new() { Header = "Text View", Icon = _state.ActiveView == ViewMode.Text ? new TextBlock { Text = "●", FontSize = 10 } : null };
        text.Click += (_, _) => SwitchView(ViewMode.Text);
        MenuItem csv = new() { Header = "CSV View", Icon = _state.ActiveView == ViewMode.Csv ? new TextBlock { Text = "●", FontSize = 10 } : null };
        csv.Click += (_, _) => SwitchView(ViewMode.Csv);
        OpenStatusBarMenu(SbViewModeButton, hex, text, csv);
    }

    private void OpenBytesPerRowStatusMenu()
    {
        if (_state.Document is null || _state.ActiveView != ViewMode.Hex)
            return;

        int current = _state.BytesPerRowSetting;
        MenuItem auto = new() { Header = "Auto", Icon = current == 0 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        auto.Click += (_, _) => SetBytesPerRow(0);
        MenuItem b8 = new() { Header = "8", Icon = current == 8 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        b8.Click += (_, _) => SetBytesPerRow(8);
        MenuItem b16 = new() { Header = "16", Icon = current == 16 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        b16.Click += (_, _) => SetBytesPerRow(16);
        MenuItem b24 = new() { Header = "24", Icon = current == 24 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        b24.Click += (_, _) => SetBytesPerRow(24);
        MenuItem b32 = new() { Header = "32", Icon = current == 32 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        b32.Click += (_, _) => SetBytesPerRow(32);
        MenuItem b48 = new() { Header = "48", Icon = current == 48 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        b48.Click += (_, _) => SetBytesPerRow(48);
        MenuItem b64 = new() { Header = "64", Icon = current == 64 ? new TextBlock { Text = "●", FontSize = 10 } : null };
        b64.Click += (_, _) => SetBytesPerRow(64);
        OpenStatusBarMenu(SbLinesButton, auto, b8, b16, b24, b32, b48, b64);
    }

    private static void OpenStatusBarMenu(Control anchor, params MenuItem[] items)
    {
        ContextMenu menu = new() { ItemsSource = items };
        menu.Open(anchor);
    }

    // ─── Status bar ───

    private void UpdateTitle()
    {
        string readOnlyPrefix = _state.IsReadOnly ? "[RO] " : "";
        string title = $"{readOnlyPrefix}{_buildIdentity.BrandedTitle}";
        if (_state.CurrentFilePath is { } path) {
            string modifiedPrefix = _state.IsModified ? "• " : "";
            title = $"{readOnlyPrefix}{modifiedPrefix}{Path.GetFileName(path)} — {_buildIdentity.BrandedTitle}";
        }
        Title = title;
    }

    private void UpdateStatusBar()
    {
        UpdateTitle();

        if (_state.Document is null) {
            SbFile.Text = $" {_buildIdentity.BrandedTitle}";
            SbBackground.Text = "";
            SbViewMode.Text = "";
            SbEncoding.Text = "";
            SbPosition.Text = "";
            SbLines.Text = "";
            SbSize.Text = "";
            ToolTip.SetTip(SbFile, null);
            SbViewModeButton.IsEnabled = false;
            SbEncodingButton.IsEnabled = false;
            SbPositionButton.IsEnabled = false;
            SbLinesButton.IsEnabled = false;
            SbSizeButton.IsEnabled = false;
            return;
        }

        string fullPath = _state.CurrentFilePath ?? "(untitled)";
        string fileName = MiddleEllipsize(fullPath, ComputeStatusFileChars());
        string modified = _state.IsModified ? " [modified]" : "";
        string readOnly = _state.IsReadOnly ? " [RO]" : "";
        SbFile.Text = $" {fileName}{modified}{readOnly}";
        ToolTip.SetTip(SbFile, fullPath);

        string viewMode = _state.ActiveView switch {
            ViewMode.Hex => "Hex",
            ViewMode.Text => "Text",
            ViewMode.Csv => "CSV",
            _ => ""
        };

        string encoding = _state.Decoder.Encoding switch {
            TextEncoding.Utf8 => "UTF-8",
            TextEncoding.Utf16Le => "UTF-16 LE",
            TextEncoding.Windows1252 => "Win-1252",
            _ => ""
        };

        string size = FormatFileSize(_state.FileLength);
        string selection = GetSelectionStatus();

        SbViewMode.Text = viewMode;
        SbSize.Text = size;
        bool lineIndexing = !(_state.Indexer?.Index?.IsComplete ?? true);
        bool csvIndexing = !(_state.CsvRowIndex?.IsComplete ?? true);
        bool searching = _state.IsSearching;
        SbBackground.Text = BuildBackgroundStatus(lineIndexing, csvIndexing, searching, _state.IsReadOnly);
        SbViewModeButton.IsEnabled = true;
        SbEncodingButton.IsEnabled = _state.ActiveView != ViewMode.Csv;
        SbPositionButton.IsEnabled = _state.ActiveView == ViewMode.Hex;
        SbLinesButton.IsEnabled = _state.ActiveView is ViewMode.Text or ViewMode.Hex;
        SbSizeButton.IsEnabled = false;

        if (_state.ActiveView == ViewMode.Csv && _state.CsvRowIndex is not null) {
            long totalCsvRows = _state.CsvRowIndex.TotalRowCount;
            string sepName = GetSeparatorName(_state.CsvDialect.Separator);
            SbEncoding.Text = $"Sep: {sepName}";
            string totalRowsLabel = csvIndexing ? $"~{totalCsvRows:N0}" : $"{totalCsvRows:N0}";
            SbPosition.Text = $"R {_state.CsvCursorRow + 1:N0}/{totalRowsLabel}  C {_state.CsvCursorCol + 1}/{_state.CsvColumnCount}";
            SbLines.Text = selection.Length > 0
                ? selection
                : (csvIndexing ? "Indexing rows..." : "");
            return;
        }

        string offset = "-";
        if (_state.CurrentCursorOffset >= 0) {
            bool dec = _state.ActiveView == ViewMode.Hex && _state.HexOffsetDecimal;
            offset = dec ? _state.CurrentCursorOffset.ToString("N0") : $"0x{_state.CurrentCursorOffset:X}";
        }

        SbEncoding.Text = encoding;
        SbPosition.Text = $"Offset: {offset}";
        string linePrefix = lineIndexing ? "~" : "";
        string linesInfo = _state.EstimatedTotalLines > 0
            ? $"{linePrefix}{_state.EstimatedTotalLines:N0} lines"
            : (lineIndexing ? "~ lines" : "0 lines");
        if (_state.ActiveView == ViewMode.Hex)
            linesInfo = $"{linesInfo} · {_state.BytesPerRow} B/R";
        if (_state.ActiveView == ViewMode.Text) {
            string eol = GetLineEndingStatus();
            if (eol.Length > 0)
                linesInfo = $"{linesInfo} · {eol}";
        }
        SbLines.Text = selection.Length > 0
            ? selection
            : linesInfo;
    }

    private string BuildBackgroundStatus(bool lineIndexing, bool csvIndexing, bool searching, bool readOnly)
    {
        List<string> ops = [];
        if (readOnly)
            ops.Add("RO");
        if (lineIndexing || csvIndexing)
            ops.Add("Indexing");
        if (searching)
            ops.Add("Search");
        return ops.Count == 0 ? "" : $"BG: {string.Join("+", ops)}";
    }

    private int ComputeStatusFileChars()
    {
        double availableWidth = SbFile.Bounds.Width;
        if (availableWidth <= 1)
            availableWidth = Bounds.Width * 0.40;

        double avgCharWidth = Math.Max(5.5, SbFile.FontSize * 0.56);
        int maxChars = (int)Math.Floor(availableWidth / avgCharWidth);
        return Math.Max(12, maxChars);
    }

    private static string MiddleEllipsize(string text, int maxChars)
    {
        const string Ellipsis = "...";
        if (text.Length <= maxChars)
            return text;

        if (maxChars <= Ellipsis.Length)
            return Ellipsis[..maxChars];

        int keepChars = maxChars - Ellipsis.Length;
        int left = (keepChars + 1) / 2;
        int right = keepChars - left;
        return right == 0
            ? $"{text[..left]}{Ellipsis}"
            : $"{text[..left]}{Ellipsis}{text[^right..]}";
    }

    private string GetSelectionStatus()
    {
        return _state.ActiveView switch {
            ViewMode.Hex when _state.HexSelStart >= 0 && _state.HexSelEnd >= _state.HexSelStart
                => $"Sel: {_state.HexSelEnd - _state.HexSelStart + 1:N0} B",
            ViewMode.Text when _state.TextSelStart >= 0 && _state.TextSelEnd >= _state.TextSelStart
                => $"Sel: {_state.TextSelEnd - _state.TextSelStart + 1:N0} B",
            ViewMode.Csv when _state.CsvSelectionAnchorRow >= 0
                => $"Sel: {Math.Abs(_state.CsvCursorRow - _state.CsvSelectionAnchorRow) + 1:N0} rows",
            _ => ""
        };
    }

    private static string GetSeparatorName(byte separator)
    {
        return separator switch {
            (byte)',' => "Comma",
            (byte)'\t' => "Tab",
            (byte)'|' => "Pipe",
            (byte)';' => "Semicolon",
            _ => $"'{(char)separator}'"
        };
    }

    private string GetLineEndingStatus()
    {
        if (_state.Document is null) return "";

        string path = _state.CurrentFilePath ?? "";
        long length = _state.FileLength;
        if (_lineEndingCachePath == path && _lineEndingCacheLength == length)
            return _lineEndingCache;

        _lineEndingCachePath = path;
        _lineEndingCacheLength = length;
        _lineEndingCache = ComputeLineEndingStatus();
        return _lineEndingCache;
    }

    private string ComputeLineEndingStatus()
    {
        if (_state.Document is null) return "";

        int minChar = _state.Decoder.MinCharBytes;
        int readLen = (int)Math.Min(256 * 1024, _state.FileLength);
        if (readLen < minChar) return "";

        byte[] buf = new byte[readLen];
        _state.Document.Read(0, buf);

        bool sawCrLf = false;
        bool sawLf = false;

        for (int i = 0; i <= readLen - minChar; i += minChar) {
            if (!IsLfCodeUnit(buf, i, minChar))
                continue;

            bool isCrLf = minChar == 1
                ? (i > 0 && buf[i - 1] == 0x0D)
                : (i >= 2 && buf[i - 2] == 0x0D && buf[i - 1] == 0x00);

            if (isCrLf) sawCrLf = true;
            else sawLf = true;

            if (sawCrLf && sawLf)
                return "EOL: Mixed";
        }

        if (sawCrLf) return "EOL: CRLF";
        if (sawLf) return "EOL: LF";
        return "";
    }

    private static bool IsLfCodeUnit(ReadOnlySpan<byte> buffer, int index, int minChar)
    {
        return minChar == 2
            ? (index + 1 < buffer.Length && buffer[index] == 0x0A && buffer[index + 1] == 0x00)
            : buffer[index] == 0x0A;
    }

    private void UpdateIndexingStatus()
    {
        bool lineIndexChanged = false;
        bool rowIndexChanged = false;

        if (_state.Indexer?.Index is { } lineIndex) {
            long total = lineIndex.TotalLineCount;
            if (total > 0)
                _state.EstimatedTotalLines = total;

            bool complete = lineIndex.IsComplete;
            lineIndexChanged = total != _lastIndexedLineCount || complete != _lastLineIndexComplete;
            _lastIndexedLineCount = total;
            _lastLineIndexComplete = complete;
        } else if (_lastIndexedLineCount != -1 || !_lastLineIndexComplete) {
            _lastIndexedLineCount = -1;
            _lastLineIndexComplete = true;
            lineIndexChanged = true;
        }

        if (_state.CsvRowIndex is { } rowIndex) {
            long totalRows = rowIndex.TotalRowCount;
            bool complete = rowIndex.IsComplete;
            rowIndexChanged = totalRows != _lastIndexedRowCount || complete != _lastRowIndexComplete;
            _lastIndexedRowCount = totalRows;
            _lastRowIndexComplete = complete;
        } else if (_lastIndexedRowCount != -1 || !_lastRowIndexComplete) {
            _lastIndexedRowCount = -1;
            _lastRowIndexComplete = true;
            rowIndexChanged = true;
        }

        UpdateStatusBar();

        if (lineIndexChanged && _state.ActiveView == ViewMode.Text)
            _textView?.InvalidateVisual();

        if (rowIndexChanged && _state.ActiveView == ViewMode.Csv)
            _csvView?.InvalidateVisual();
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
        WelcomeScreen.HelpRequested = OpenOnlineHelp;
        WelcomeScreen.PinChanged = (path, pinned) => {
            if (pinned)
                _state.Settings.PinFile(path);
            else
                _state.Settings.UnpinFile(path);
            PopulateWelcomeScreen();
            BuildFileMenuMru();
        };
        WelcomeScreen.FileRemoved = path => {
            _state.Settings.RemoveFile(path);
            PopulateWelcomeScreen();
            BuildFileMenuMru();
        };

        WelcomeScreen.SetVersionInfo(_buildIdentity.AboutVersion, _buildIdentity.BuildDateUtc);
    }

    private void PopulateWelcomeScreen()
    {
        (List<string> pinned, List<string> recent) = GetCleanMruLists();
        WelcomeScreen.Populate(pinned, recent);
    }

    private (List<string> Pinned, List<string> Recent) GetCleanMruLists()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> pinned = [];
        foreach (string path in _state.Settings.PinnedFiles)
        {
            if (!seen.Add(path))
                continue;
            pinned.Add(path);
        }

        List<string> recent = [];
        foreach (string path in _state.Settings.RecentFiles)
        {
            if (!seen.Add(path))
                continue;
            recent.Add(path);
        }

        return (pinned, recent);
    }

    // ─── File menu MRU ───

    private void BuildFileMenuMru()
    {
        // Remove any previous dynamic MRU items (between first Separator and MruSeparator)
        List<Control> toRemove = [];
        bool inMruZone = false;
        foreach (Control child in FileMenu.Items.Cast<Control>()) {
            if (child == MruSeparator) break;
            if (inMruZone) toRemove.Add(child);
            if (child is Separator && child != MruSeparator && !inMruZone) inMruZone = true;
        }
        foreach (Control c in toRemove)
            FileMenu.Items.Remove(c);

        (List<string> pinned, List<string> recent) = GetCleanMruLists();
        List<string> allFiles = [.. pinned, .. recent];

        if (allFiles.Count == 0) {
            MruSeparator.IsVisible = false;
        } else {
            MruSeparator.IsVisible = true;
            int insertIdx = FileMenu.Items.IndexOf(MruSeparator);
            for (int i = 0; i < Math.Min(9, allFiles.Count); i++) {
                string path = allFiles[i];
                string fileName = Path.GetFileName(path);
                int number = i + 1;
                bool isPinned = pinned.Contains(path, StringComparer.OrdinalIgnoreCase);
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
        if (e.KeyModifiers == KeyModifiers.Control) {
            bool textInputFocused = IsTextInputFocused();
            switch (e.Key) {
                case Key.P:
                    if (_state.Document is not null) {
                        ShowCommandPalette();
                        e.Handled = true;
                    }
                    return;
                case Key.Q:
                    Close();
                    e.Handled = true;
                    return;
                case Key.W:
                    _ = CloseFileAsync();
                    e.Handled = true;
                    return;
                case Key.X when !textInputFocused:
                    DoCut();
                    e.Handled = true;
                    return;
                case Key.C when !textInputFocused:
                    DoCopy();
                    e.Handled = true;
                    return;
                case Key.V when !textInputFocused:
                    DoPaste();
                    e.Handled = true;
                    return;
                case Key.A when !textInputFocused:
                    DoSelectAll();
                    e.Handled = true;
                    return;
            }
        }

        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.G) {
            ToggleGutter();
            e.Handled = true;
            return;
        }

        // F2 for CSV record detail panel toggle
        if (e.Key == Key.F2 && _state.ActiveView == ViewMode.Csv) {
            ToggleCsvDetailPanel();
            e.Handled = true;
            return;
        }
    }

    private bool IsTextInputFocused()
    {
        IInputElement? focused = FocusManager?.GetFocusedElement();
        return focused is TextBox;
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
        if (_state.Document is null)
            return;

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

        _findBar = new FindBar(_state, StartSearch, FindNext, FindPrevious, FocusActiveViewAsync) {
            IsVisible = false
        };

        _gotoBar = new GotoBar(_state,
            offset => _hexView?.GotoOffset(offset),
            line => _textView?.GotoLine(line)) {
            IsVisible = false
        };

        _commandPalette = new CommandPaletteOverlay(_state,
            offset => _hexView?.GotoOffset(offset),
            (line, column) => _textView?.GotoLine(line, column),
            offset => _textView?.GotoOffset(offset),
            row => _csvView?.GotoRow(row),
            FocusActiveViewAsync) {
            IsVisible = false
        };
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
        _commandPalette.RegisterCommand("Save As...", "Save under a new name", () => _ = ShowSaveAsDialog());
        _commandPalette.RegisterCommand("Exit", "Close Leviathan (Alt+F4 / Ctrl+Q)", Close);
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
            () => _state.GutterVisible ? "☑ Show Gutter" : "☐ Show Gutter",
            "Toggle the row number / offset gutter (Ctrl+Shift+G)",
            ToggleGutter,
            searchName: "Show Gutter",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.HexOffsetDecimal ? "☑ Decimal Offsets" : "☐ Decimal Offsets",
            "Toggle decimal/hex offsets in Hex view",
            ToggleDecimalOffsets,
            searchName: "Decimal Offsets",
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
        _commandPalette.RegisterCommand(
            () => _state.BytesPerRowSetting == 0 ? "● Bytes/Row: Auto" : "○ Bytes/Row: Auto",
            "Auto-fit bytes per row in hex view",
            () => SetBytesPerRow(0),
            searchName: "Bytes Per Row Auto",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.BytesPerRowSetting == 8 ? "● Bytes/Row: 8" : "○ Bytes/Row: 8",
            "Set bytes per row to 8 in hex view",
            () => SetBytesPerRow(8),
            searchName: "Bytes Per Row 8",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.BytesPerRowSetting == 16 ? "● Bytes/Row: 16" : "○ Bytes/Row: 16",
            "Set bytes per row to 16 in hex view",
            () => SetBytesPerRow(16),
            searchName: "Bytes Per Row 16",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.BytesPerRowSetting == 24 ? "● Bytes/Row: 24" : "○ Bytes/Row: 24",
            "Set bytes per row to 24 in hex view",
            () => SetBytesPerRow(24),
            searchName: "Bytes Per Row 24",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.BytesPerRowSetting == 32 ? "● Bytes/Row: 32" : "○ Bytes/Row: 32",
            "Set bytes per row to 32 in hex view",
            () => SetBytesPerRow(32),
            searchName: "Bytes Per Row 32",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.BytesPerRowSetting == 48 ? "● Bytes/Row: 48" : "○ Bytes/Row: 48",
            "Set bytes per row to 48 in hex view",
            () => SetBytesPerRow(48),
            searchName: "Bytes Per Row 48",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.BytesPerRowSetting == 64 ? "● Bytes/Row: 64" : "○ Bytes/Row: 64",
            "Set bytes per row to 64 in hex view",
            () => SetBytesPerRow(64),
            searchName: "Bytes Per Row 64",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("CSV Settings...", "Configure CSV separator/quote/header (F8)", () => _ = ShowCsvSettingsDialog());
        _commandPalette.RegisterCommand("Cut", "Cut selection (Ctrl+X)", DoCut, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Copy", "Copy selection (Ctrl+C)", DoCopy, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Paste", "Paste from clipboard (Ctrl+V)", DoPaste, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Select All", "Select entire file (Ctrl+A)", DoSelectAll, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("Delete Row(s)", "Delete selected CSV rows", DoDeleteCsvRows, restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.IsReadOnly ? "☑ Read-only Mode" : "☐ Read-only Mode",
            "Toggle read-only editing lock",
            ToggleReadOnlyMode,
            searchName: "Read-only Mode",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand(
            () => _state.Settings.StartReadOnly ? "☑ Start in Read-only" : "☐ Start in Read-only",
            "Persist startup in read-only mode",
            ToggleStartReadOnly,
            searchName: "Start in Read-only",
            restoreFocusAfterExecute: true);
        _commandPalette.RegisterCommand("About", "About Leviathan", ShowAboutDialog);
        _commandPalette.RegisterCommand("Keyboard Shortcuts", "Show key combinations (F1)", ShowKeyboardShortcuts);
        _commandPalette.RegisterCommand("Online Help", "Open docs/help.md in browser", OpenOnlineHelp);

        // Theme commands
        foreach (ColorTheme theme in ColorTheme.BuiltInThemes) {
            string id = theme.Id;
            string name = theme.Name;
            _commandPalette.RegisterCommand(
                () => _state.ActiveTheme.Id == id ? $"● Theme: {name}" : $"○ Theme: {name}",
                $"Switch to {name} color theme",
                () => SwitchTheme(id),
                searchName: $"Theme {name}",
                restoreFocusAfterExecute: true);
        }

        foreach (ColorTheme theme in _state.UserThemes) {
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

        (List<string> pinned, List<string> recent) = GetCleanMruLists();
        foreach (string pinnedPath in pinned) {
            string capturedPath = pinnedPath;
            string fileName = Path.GetFileName(capturedPath);
            _commandPalette.RegisterCommand(
                () => $"📌 Open: {fileName}",
                capturedPath,
                () => _ = OpenRecentFileAsync(capturedPath),
                searchName: $"Open Pinned {fileName}");
        }

        foreach (string recentPath in recent.Take(20)) {
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
        if (isHex) {
            try { SearchEngine.ParseHexPattern(query); } catch (FormatException) {
                _state.IsSearching = false;
                _state.SearchStatus = "Invalid hex pattern";
                _findBar?.UpdateMatchStatus();
                return;
            }
        } else if (isRegex && !SearchEngine.IsValidRegex(query)) {
            _state.IsSearching = false;
            _state.SearchStatus = "Invalid regex";
            _findBar?.UpdateMatchStatus();
            return;
        }

        _state.SearchTask = Task.Run(() => {
            IEnumerable<SearchResult> source;
            if (isRegex) {
                source = SearchEngine.FindAllRegex(doc, decoder, query, caseSensitive, cts.Token);
            } else {
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

            foreach (SearchResult r in source) {
                batch.Add(r);
                if (batch.Count >= BatchSize) {
                    List<SearchResult> toPost = batch;
                    batch = new List<SearchResult>(BatchSize);
                    int batchTotal = totalSoFar + toPost.Count;
                    totalSoFar = batchTotal;
                    Dispatcher.UIThread.Post(() => {
                        _state.SearchResults.AddRange(toPost);
                        if (_state.CurrentMatchIndex < 0 && _state.SearchResults.Count > 0) {
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
            Dispatcher.UIThread.Post(() => {
                if (finalBatch.Count > 0)
                    _state.SearchResults.AddRange(finalBatch);

                _state.IsSearching = false;
                if (_state.CurrentMatchIndex < 0 && _state.SearchResults.Count > 0) {
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

        if (_state.ActiveView == ViewMode.Hex) {
            _hexView?.GotoOffset(offset);
        } else if (_state.ActiveView == ViewMode.Text) {
            _textView?.GotoOffset(offset);
        } else if (_state.ActiveView == ViewMode.Csv) {
            _csvView?.GotoOffset(offset);
        }
    }

    // ─── Edit operations ───

    private bool IsEditBlockedByReadOnly()
    {
        if (!_state.IsReadOnly)
            return false;

        UpdateStatusBar();
        return true;
    }

    private async void DoCopy()
    {
        await CopySelectionToClipboardAsync();
    }

    private async void DoCut()
    {
        if (IsEditBlockedByReadOnly())
            return;

        bool copied = await CopySelectionToClipboardAsync();
        if (copied)
            DeleteActiveSelection();
    }

    private async Task<bool> CopySelectionToClipboardAsync()
    {
        if (_state.Document is null || Clipboard is not { } clipboard)
            return false;

        string? text = null;
        if (_state.ActiveView == ViewMode.Hex) {
            long selStart = _state.HexSelStart;
            long selEnd = _state.HexSelEnd;
            if (selStart >= 0 && selEnd >= selStart) {
                int len = (int)Math.Min(selEnd - selStart + 1, 65536);
                byte[] buf = new byte[len];
                _state.Document.Read(selStart, buf);
                text = string.Join(' ', buf.Select(b => b.ToString("X2")));
            }
        } else if (_state.ActiveView == ViewMode.Text) {
            long selStart = _state.TextSelStart;
            long selEnd = _state.TextSelEnd;
            if (selStart >= 0 && selEnd >= selStart) {
                int len = (int)Math.Min(selEnd - selStart + 1, 131072);
                byte[] buf = new byte[len];
                _state.Document.Read(selStart, buf);
                System.Text.Encoding enc = _state.Decoder.Encoding switch {
                    TextEncoding.Utf16Le => System.Text.Encoding.Unicode,
                    TextEncoding.Windows1252 => System.Text.Encoding.Latin1,
                    _ => System.Text.Encoding.UTF8
                };
                text = enc.GetString(buf);
            }
        }

        if (text is null)
            return false;

        await clipboard.SetTextAsync(text);
        return true;
    }

    private bool DeleteActiveSelection()
    {
        if (_state.Document is null)
            return false;
        if (IsEditBlockedByReadOnly())
            return false;

        if (_state.ActiveView == ViewMode.Hex) {
            long selStart = _state.HexSelStart;
            long selEnd = _state.HexSelEnd;
            if (selStart < 0 || selEnd < selStart)
                return false;

            _state.Document.Delete(selStart, selEnd - selStart + 1);
            _state.HexSelectionAnchor = -1;
            _state.NibbleLow = false;
            _state.HexCursorOffset = _state.Document.Length > 0
                ? Math.Min(selStart, _state.Document.Length - 1)
                : 0;
        } else if (_state.ActiveView == ViewMode.Text) {
            long selStart = _state.TextSelStart;
            long selEnd = _state.TextSelEnd;
            if (selStart < 0 || selEnd < selStart)
                return false;

            _state.Document.Delete(selStart, selEnd - selStart + 1);
            _state.TextSelectionAnchor = -1;
            _state.TextCursorOffset = selStart;
        } else {
            return false;
        }

        _state.InvalidateSearchResults();
        _hexView?.InvalidateVisual();
        _textView?.InvalidateVisual();
        UpdateStatusBar();
        return true;
    }

    private async void DoPaste()
    {
        if (_state.Document is null || Clipboard is not IAsyncDataTransfer clipboard)
            return;
        if (_state.ActiveView == ViewMode.Csv)
            return;
        if (IsEditBlockedByReadOnly())
            return;

        string? clipboardText = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(clipboardText))
            return;

        DeleteActiveSelection();

        if (_state.ActiveView == ViewMode.Hex) {
            byte[] bytes = TryParseHexClipboard(clipboardText, out byte[] parsed)
                ? parsed
                : System.Text.Encoding.UTF8.GetBytes(clipboardText);
            if (bytes.Length == 0)
                return;

            long insertAt = Math.Max(0, _state.HexCursorOffset);
            insertAt = Math.Min(insertAt, _state.Document.Length);
            _state.Document.Insert(insertAt, bytes);
            _state.HexCursorOffset = insertAt + bytes.Length - 1;
            _state.HexSelectionAnchor = -1;
            _state.NibbleLow = false;
        } else {
            System.Text.Encoding enc = _state.Decoder.Encoding switch {
                TextEncoding.Utf16Le => System.Text.Encoding.Unicode,
                TextEncoding.Windows1252 => System.Text.Encoding.Latin1,
                _ => System.Text.Encoding.UTF8
            };
            byte[] encoded = enc.GetBytes(clipboardText);
            if (encoded.Length == 0)
                return;

            long insertAt = Math.Max(_state.BomLength, _state.TextCursorOffset);
            insertAt = Math.Min(insertAt, _state.Document.Length);
            _state.Document.Insert(insertAt, encoded);
            _state.TextCursorOffset = insertAt + encoded.Length;
            _state.TextSelectionAnchor = -1;
        }

        _state.InvalidateSearchResults();
        _hexView?.InvalidateVisual();
        _textView?.InvalidateVisual();
        UpdateStatusBar();
    }

    private static bool TryParseHexClipboard(string text, out byte[] bytes)
    {
        string compact = new(text.Where(static c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0 || (compact.Length % 2) != 0) {
            bytes = [];
            return false;
        }

        for (int i = 0; i < compact.Length; i++) {
            if (!Uri.IsHexDigit(compact[i])) {
                bytes = [];
                return false;
            }
        }

        bytes = new byte[compact.Length / 2];
        for (int i = 0; i < bytes.Length; i++) {
            bytes[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
        }
        return true;
    }

    private void DoSelectAll()
    {
        if (_state.Document is null) return;

        if (_state.ActiveView == ViewMode.Hex) {
            _state.HexSelectionAnchor = 0;
            _state.HexCursorOffset = _state.FileLength - 1;
        } else if (_state.ActiveView == ViewMode.Text) {
            _state.TextSelectionAnchor = _state.BomLength;
            _state.TextCursorOffset = _state.FileLength;
        }

        _hexView?.InvalidateVisual();
        _textView?.InvalidateVisual();
    }

    private void DoDeleteCsvRows()
    {
        if (IsEditBlockedByReadOnly())
            return;

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
        if (visible) {
            _csvOuterGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto,300");
            Grid.SetColumn(_csvSplitter, 1);
            Grid.SetColumn(_csvDetailPanel, 2);
            _csvDetailPanel.UpdateRecord(_state, _csvView!);
        } else {
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
        if (dialog.Applied) {
            _csvView?.InvalidateVisual();
            UpdateStatusBar();
        }
        FocusActiveViewAsync();
    }

    // ─── Dialogs ───

    private void ShowAboutDialog()
    {
        string buildDate = _buildIdentity.BuildDateUtc ?? "";

        Window aboutWindow = new() {
            Title = "About Leviathan",
            Width = 400,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        StackPanel content = new() {
            Margin = new Thickness(24),
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock { Text = "Leviathan", FontSize = 22, FontWeight = FontWeight.Bold });
        content.Children.Add(new TextBlock { Text = "Large File Editor — Avalonia GUI", FontSize = 13, Opacity = 0.6 });
        content.Children.Add(new TextBlock { Text = $"Version {_buildIdentity.AboutVersion}", FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(buildDate))
            content.Children.Add(new TextBlock { Text = $"Built {buildDate}", FontSize = 12, Opacity = 0.5 });

        Button closeBtn = new() { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        closeBtn.Click += (_, _) => aboutWindow.Close();
        content.Children.Add(closeBtn);

        aboutWindow.Content = content;
        aboutWindow.ShowDialog(this);
    }

    private readonly record struct BuildIdentity(string FileVersion, string AboutVersion, string BrandedTitle, string? BuildDateUtc)
    {
        public static BuildIdentity Create(Assembly assembly)
        {
            string version = ThisAssembly.AssemblyFileVersion;
            if (string.IsNullOrWhiteSpace(version))
                version = assembly.GetName().Version?.ToString() ?? "0.0.0";

            string commit = ThisAssembly.GitCommitId;
            if (string.IsNullOrWhiteSpace(commit))
                commit = "unknown";
            if (commit.Length > 12)
                commit = commit[..12];

            string aboutVersion = $"{version} (sha {commit})";
            string title = $"Leviathan v{version}";
            string? buildDate = ReadAssemblyMetadata(assembly, "BuildDateUtc");
            return new BuildIdentity(version, aboutVersion, title, buildDate);
        }

        private static string? ReadAssemblyMetadata(Assembly assembly, string key)
        {
            foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>()) {
                if (string.Equals(metadata.Key, key, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(metadata.Value))
                    return metadata.Value;
            }
            return null;
        }
    }

    private void OpenOnlineHelp()
    {
        try {
            ProcessStartInfo startInfo = new() {
                FileName = OnlineHelpUrl,
                UseShellExecute = true
            };
            Process.Start(startInfo);
        } catch (Exception ex) {
            ShowErrorDialog("Open Help Failed", ex.Message);
        }
    }

    private void ShowKeyboardShortcuts()
    {
        Window helpWindow = new() {
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
            Ctrl+X          Cut selection
            Ctrl+C          Copy selection
            Ctrl+V          Paste
            Ctrl+A          Select all
            Home/End        Start/end of line
            Ctrl+Home/End   Start/end of file
            PgUp/PgDn       Page up/down
            """;

        ScrollViewer scroll = new() {
            Content = new TextBlock {
                Text = shortcuts,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 13,
                Margin = new Thickness(16)
            }
        };

        Button openHelpButton = new() {
            Content = "Open Online Help ↗",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12)
        };
        openHelpButton.Click += (_, _) => OpenOnlineHelp();

        DockPanel layout = new();
        DockPanel.SetDock(openHelpButton, Dock.Bottom);
        layout.Children.Add(openHelpButton);
        layout.Children.Add(scroll);

        helpWindow.Content = layout;
        helpWindow.KeyDown += (_, e) => {
            if (e.Key == Key.Escape) {
                helpWindow.Close();
                e.Handled = true;
            }
        };
        helpWindow.ShowDialog(this);
    }

    private void ShowErrorDialog(string title, string message)
    {
        Window errorWindow = new() {
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
