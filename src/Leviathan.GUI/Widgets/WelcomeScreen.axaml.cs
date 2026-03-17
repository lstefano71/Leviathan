using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// VS-style welcome screen with searchable pinned + recent file lists,
/// async metadata loading, and keyboard navigation.
/// </summary>
public sealed partial class WelcomeScreen : UserControl
{
    private const int MetadataTimeoutMs = 3_000;

    private readonly List<FileEntry> _allEntries = [];
    private readonly List<FileEntry> _filteredEntries = [];
    private CancellationTokenSource? _metadataCts;
    private int _selectedIndex = -1;
    private bool _isKeyboardNavigating;

    /// <summary>Raised when the user selects a file to open.</summary>
    public Action<string>? FileSelected;

    /// <summary>Raised when the user requests to open a new file via dialog.</summary>
    public Action? OpenFileRequested;

    /// <summary>Raised when the user pins or unpins a file.</summary>
    public Action<string, bool>? PinChanged;

    /// <summary>Raised when the user removes a file from the list.</summary>
    public Action<string>? FileRemoved;

    /// <summary>Raised when the user asks to open online help.</summary>
    public Action? HelpRequested;

    public WelcomeScreen()
    {
        InitializeComponent();

        SearchInput.TextChanged += (_, _) => ApplyFilter();
        SearchInput.KeyDown += OnSearchKeyDown;
        OpenFileButton.Click += (_, _) => OpenFileRequested?.Invoke();
        HelpLinkButton.Click += (_, _) => HelpRequested?.Invoke();

        // Tunnel handler to intercept PageUp/PageDown before the ScrollViewer consumes them
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Sets the version and build date shown in the right-hand info panel.
    /// </summary>
    public void SetVersionInfo(string version, string? buildDate)
    {
        InfoVersion.Text = $"Version {version}";
        InfoBuildDate.Text = string.IsNullOrEmpty(buildDate) ? "" : $"Built {buildDate}";
        InfoBuildDate.IsVisible = !string.IsNullOrEmpty(buildDate);
    }

    /// <summary>
    /// Populates the welcome screen with pinned and recent files, then starts
    /// background metadata loading for each entry.
    /// </summary>
    public void Populate(List<string> pinnedFiles, List<string> recentFiles)
    {
        CancelMetadataLoading();

        _allEntries.Clear();
        _filteredEntries.Clear();
        _selectedIndex = -1;

        foreach (string path in pinnedFiles)
            _allEntries.Add(new FileEntry(path, isPinned: true));

        foreach (string path in recentFiles)
            _allEntries.Add(new FileEntry(path, isPinned: false));

        ApplyFilter();
        StartMetadataLoading();

        Dispatcher.UIThread.Post(() => SearchInput.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>Cancels any in-flight metadata loading tasks.</summary>
    public void CancelMetadataLoading()
    {
        if (_metadataCts is not null) {
            _metadataCts.Cancel();
            _metadataCts.Dispose();
            _metadataCts = null;
        }
    }

    // ─── Metadata loading ───

    private void StartMetadataLoading()
    {
        CancellationTokenSource cts = new();
        _metadataCts = cts;

        foreach (FileEntry entry in _allEntries) {
            FileEntry captured = entry;
            _ = Task.Run(() => LoadFileMetadata(captured, cts.Token), cts.Token);
        }
    }

    private void LoadFileMetadata(FileEntry entry, CancellationToken ct)
    {
        try {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MetadataTimeoutMs);
            CancellationToken token = timeoutCts.Token;

            // Check cancellation before doing I/O
            token.ThrowIfCancellationRequested();

            FileInfo info = new(entry.FullPath);
            if (!info.Exists) {
                token.ThrowIfCancellationRequested();
                Dispatcher.UIThread.Post(() => UpdateEntryMetadata(entry, null, null, isUnavailable: true));
                return;
            }

            token.ThrowIfCancellationRequested();

            long size = info.Length;
            DateTime lastModified = info.LastWriteTime;

            string sizeText = FormatFileSize(size);
            string dateText = FormatDate(lastModified);

            token.ThrowIfCancellationRequested();
            Dispatcher.UIThread.Post(() => UpdateEntryMetadata(entry, sizeText, dateText, isUnavailable: false));
        } catch (OperationCanceledException) {
            // Cancelled — do nothing
        } catch {
            if (!ct.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => UpdateEntryMetadata(entry, null, null, isUnavailable: true));
        }
    }

    private void UpdateEntryMetadata(FileEntry entry, string? sizeText, string? dateText, bool isUnavailable)
    {
        entry.SizeText = sizeText;
        entry.DateText = dateText;
        entry.IsUnavailable = isUnavailable;

        UpdateEntryMetadataText(entry);
    }

    // ─── Filtering ───

    private void ApplyFilter()
    {
        string? query = SearchInput.Text;
        _filteredEntries.Clear();

        foreach (FileEntry entry in _allEntries) {
            if (string.IsNullOrEmpty(query) ||
                entry.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase)) {
                _filteredEntries.Add(entry);
            }
        }

        RebuildVisualList();
        SetSelectedIndex(_filteredEntries.Count > 0 ? 0 : -1);
    }

    // ─── Visual list ───

    private void RebuildVisualList()
    {
        FileListPanel.Children.Clear();

        bool hasPinned = false;
        bool pinnedHeaderAdded = false;
        bool recentHeaderAdded = false;

        // Check if we have pinned items (to show separator before recent section)
        foreach (FileEntry entry in _filteredEntries) {
            if (entry.IsPinned) hasPinned = true;
        }

        int digitIndex = 0;

        foreach (FileEntry entry in _filteredEntries) {
            // Add section headers
            if (entry.IsPinned && !pinnedHeaderAdded) {
                pinnedHeaderAdded = true;
                FileListPanel.Children.Add(CreateSectionHeader("📌  Pinned"));
            } else if (!entry.IsPinned && !recentHeaderAdded) {
                recentHeaderAdded = true;
                if (hasPinned)
                    FileListPanel.Children.Add(CreateSeparator());
                FileListPanel.Children.Add(CreateSectionHeader("Recent"));
            }

            digitIndex++;
            string? digitBadge = string.IsNullOrEmpty(SearchInput.Text) && digitIndex <= 9
                ? digitIndex.ToString()
                : null;

            Border row = CreateFileRow(entry, digitBadge);
            entry.VisualRow = row;
            FileListPanel.Children.Add(row);
        }

        if (_filteredEntries.Count == 0 && !string.IsNullOrEmpty(SearchInput.Text)) {
            FileListPanel.Children.Add(new TextBlock {
                Text = "No matching files",
                FontSize = 13,
                Opacity = 0.4,
                Margin = new Thickness(12, 16),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
    }

    private static TextBlock CreateSectionHeader(string text)
    {
        return new TextBlock {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.5,
            Margin = new Thickness(12, 12, 0, 4)
        };
    }

    private static Border CreateSeparator()
    {
        return new Border {
            Height = 1,
            Background = new SolidColorBrush(Colors.Gray, 0.2),
            Margin = new Thickness(12, 8)
        };
    }

    private Border CreateFileRow(FileEntry entry, string? digitBadge)
    {
        string fileName = Path.GetFileName(entry.FullPath);

        // File name line with optional digit badge and pin icon
        StackPanel nameLine = new() { Orientation = Orientation.Horizontal, Spacing = 6 };

        if (digitBadge is not null) {
            nameLine.Children.Add(new Border {
                Background = new SolidColorBrush(Colors.Gray, 0.15),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock {
                    Text = $"Alt+{digitBadge}",
                    FontSize = 11,
                    Opacity = 0.5,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace")
                }
            });
        }

        if (entry.IsPinned) {
            nameLine.Children.Add(new TextBlock {
                Text = "📌",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        nameLine.Children.Add(new TextBlock {
            Text = fileName,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold
        });

        Grid topLine = new() {
            ColumnDefinitions = new ColumnDefinitions("*,90,120")
        };
        topLine.Children.Add(nameLine);

        TextBlock sizeBlock = new() {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        Grid.SetColumn(sizeBlock, 1);
        topLine.Children.Add(sizeBlock);

        TextBlock dateBlock = new() {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, Courier New, monospace")
        };
        Grid.SetColumn(dateBlock, 2);
        topLine.Children.Add(dateBlock);

        TextBlock pathBlock = new() {
            Text = entry.FullPath,
            FontSize = 11,
            Opacity = 0.45,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        entry.SizeTextBlock = sizeBlock;
        entry.DateTextBlock = dateBlock;
        UpdateEntryMetadataText(entry);

        StackPanel content = new() { Spacing = 2 };
        content.Children.Add(topLine);
        content.Children.Add(pathBlock);

        Border row = new() {
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
            Tag = entry
        };

        row.PointerPressed += OnRowPointerPressed;
        row.PointerMoved += (_, e) => {
            // Real mouse movement clears keyboard navigation mode
            if (_isKeyboardNavigating) {
                _isKeyboardNavigating = false;
                return;
            }
            int idx = _filteredEntries.IndexOf(entry);
            if (idx >= 0) SetSelectedIndex(idx);
        };

        return row;
    }

    private static void UpdateEntryMetadataText(FileEntry entry)
    {
        if (entry.IsUnavailable) {
            entry.SizeTextBlock?.Text = "—";
            entry.DateTextBlock?.Text = "unavailable";
            return;
        }

        if (entry.SizeText is null && entry.DateText is null && !entry.IsUnavailable) {
            entry.SizeTextBlock?.Text = "…";
            entry.DateTextBlock?.Text = "loading…";
            return;
        }

        entry.SizeTextBlock?.Text = entry.SizeText ?? "—";
        entry.DateTextBlock?.Text = entry.DateText ?? "";
    }

    // ─── Selection ───

    private static readonly IBrush SelectedBrush =
        new SolidColorBrush(Color.FromArgb(40, 100, 160, 255));

    private void SetSelectedIndex(int index)
    {
        // Deselect old
        if (_selectedIndex >= 0 && _selectedIndex < _filteredEntries.Count) {
            FileEntry old = _filteredEntries[_selectedIndex];
            old.VisualRow?.Background = Brushes.Transparent;
        }

        _selectedIndex = index;

        // Select new
        if (_selectedIndex >= 0 && _selectedIndex < _filteredEntries.Count) {
            FileEntry current = _filteredEntries[_selectedIndex];
            if (current.VisualRow is not null) {
                current.VisualRow.Background = SelectedBrush;
                current.VisualRow.BringIntoView();
            }
        }
    }

    private void ExecuteSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _filteredEntries.Count)
            return;

        FileEntry entry = _filteredEntries[_selectedIndex];
        CancelMetadataLoading();
        FileSelected?.Invoke(entry.FullPath);
    }

    // ─── Keyboard ───

    /// <summary>
    /// Tunnel handler that intercepts PageUp/PageDown before the ScrollViewer consumes them.
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key) {
            case Key.PageDown:
                _isKeyboardNavigating = true;
                if (_filteredEntries.Count > 0)
                    SetSelectedIndex(Math.Min(_selectedIndex + 10, _filteredEntries.Count - 1));
                e.Handled = true;
                break;

            case Key.PageUp:
                _isKeyboardNavigating = true;
                if (_filteredEntries.Count > 0)
                    SetSelectedIndex(Math.Max(_selectedIndex - 10, 0));
                e.Handled = true;
                break;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key) {
            case Key.Down:
                _isKeyboardNavigating = true;
                if (_filteredEntries.Count > 0)
                    SetSelectedIndex(Math.Min(_selectedIndex + 1, _filteredEntries.Count - 1));
                e.Handled = true;
                break;

            case Key.Up:
                _isKeyboardNavigating = true;
                if (_filteredEntries.Count > 0)
                    SetSelectedIndex(Math.Max(_selectedIndex - 1, 0));
                e.Handled = true;
                break;

            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;

            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchInput.Text)) {
                    SearchInput.Text = "";
                    e.Handled = true;
                }
                break;
        }

        // Alt+digit shortcuts for quick file access
        if (e.KeyModifiers == KeyModifiers.Alt) {
            int digit = e.Key switch {
                Key.D1 => 1,
                Key.D2 => 2,
                Key.D3 => 3,
                Key.D4 => 4,
                Key.D5 => 5,
                Key.D6 => 6,
                Key.D7 => 7,
                Key.D8 => 8,
                Key.D9 => 9,
                _ => 0
            };

            if (digit > 0 && digit <= _allEntries.Count) {
                CancelMetadataLoading();
                FileSelected?.Invoke(_allEntries[digit - 1].FullPath);
                e.Handled = true;
            }
        }
    }

    // ─── Mouse ───

    private void OnRowPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Border row || row.Tag is not FileEntry entry)
            return;

        Avalonia.Input.PointerPointProperties props = e.GetCurrentPoint(row).Properties;

        if (props.IsLeftButtonPressed) {
            // Double-click opens, single click selects
            if (e.ClickCount >= 2) {
                CancelMetadataLoading();
                FileSelected?.Invoke(entry.FullPath);
                e.Handled = true;
            } else {
                int idx = _filteredEntries.IndexOf(entry);
                if (idx >= 0) SetSelectedIndex(idx);
            }
        } else if (props.IsRightButtonPressed) {
            ShowContextMenu(row, entry);
            e.Handled = true;
        }
    }

    private void ShowContextMenu(Border row, FileEntry entry)
    {
        ContextMenu menu = new();

        if (entry.IsPinned) {
            MenuItem unpin = new() { Header = "Unpin" };
            unpin.Click += (_, _) => PinChanged?.Invoke(entry.FullPath, false);
            menu.Items.Add(unpin);
        } else {
            MenuItem pin = new() { Header = "Pin" };
            pin.Click += (_, _) => PinChanged?.Invoke(entry.FullPath, true);
            menu.Items.Add(pin);
        }

        MenuItem remove = new() { Header = "Remove from list" };
        remove.Click += (_, _) => FileRemoved?.Invoke(entry.FullPath);
        menu.Items.Add(remove);

        row.ContextMenu = menu;
        menu.Open(row);
    }

    // ─── Formatting helpers ───

    private static string FormatFileSize(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        const long TB = GB * 1024;

        return bytes switch {
            >= TB => $"{bytes / (double)TB:F1} TB",
            >= GB => $"{bytes / (double)GB:F1} GB",
            >= MB => $"{bytes / (double)MB:F1} MB",
            >= KB => $"{bytes / (double)KB:F1} KB",
            _ => $"{bytes} B"
        };
    }

    private static string FormatDate(DateTime date)
    {
        TimeSpan age = DateTime.Now - date;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        if (age.TotalDays < 7) return $"{(int)age.TotalDays}d ago";
        return date.ToString("yyyy-MM-dd");
    }

    // ─── Entry model ───

    /// <summary>
    /// Mutable model for a single file entry in the welcome screen.
    /// Updated from background tasks via Dispatcher.
    /// </summary>
    internal sealed class FileEntry
    {
        public string FullPath { get; }
        public bool IsPinned { get; }
        public string? SizeText { get; set; }
        public string? DateText { get; set; }
        public bool IsUnavailable { get; set; }

        /// <summary>Reference to the size TextBlock for in-place updates.</summary>
        public TextBlock? SizeTextBlock { get; set; }

        /// <summary>Reference to the modified-date TextBlock for in-place updates.</summary>
        public TextBlock? DateTextBlock { get; set; }

        /// <summary>Reference to the visual row Border for selection highlighting.</summary>
        public Border? VisualRow { get; set; }

        public FileEntry(string fullPath, bool isPinned)
        {
            FullPath = fullPath;
            IsPinned = isPinned;
        }
    }
}
