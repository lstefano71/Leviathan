using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;

using Leviathan.GUI.Helpers;

using System.Text;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Advanced editor for built-in and user themes.
/// </summary>
public sealed partial class ThemeEditorDialog : Window
{
    private static IReadOnlyList<ColorSlotDefinition> ColorSlots { get; } = [
        new(ThemeColorKeys.TextPrimary, "Text Primary"),
        new(ThemeColorKeys.TextSecondary, "Text Secondary"),
        new(ThemeColorKeys.TextMuted, "Text Muted"),
        new(ThemeColorKeys.Background, "Background"),
        new(ThemeColorKeys.SelectionHighlight, "Selection Highlight"),
        new(ThemeColorKeys.CursorHighlight, "Cursor Highlight"),
        new(ThemeColorKeys.GridLine, "Grid Line"),
        new(ThemeColorKeys.HeaderBackground, "Header Background"),
        new(ThemeColorKeys.HeaderText, "Header Text"),
        new(ThemeColorKeys.GutterBackground, "Gutter Background"),
        new(ThemeColorKeys.CursorBar, "Cursor Bar"),
        new(ThemeColorKeys.MatchHighlight, "Match Highlight"),
        new(ThemeColorKeys.ActiveMatchHighlight, "Active Match Highlight"),
        new(ThemeColorKeys.RowStripe, "Row Stripe"),
        new(ThemeColorKeys.ColumnStripe, "Column Stripe")
    ];

    private readonly Action<List<ColorTheme>> _updateUserThemes;
    private readonly ThemeEditorLivePreviewSession _livePreviewSession;
    private readonly ThemeEditorActiveThemeIdentity _activeThemeIdentity;
    private readonly string _themesDirectory;
    private readonly List<ThemeListEntry> _themeEntries = [];
    private readonly Dictionary<string, ColorRowControls> _colorRows = new(StringComparer.Ordinal);
    private EditableThemeModel? _currentModel;
    private ThemeListEntry? _selectedThemeEntry;
    private bool _isPopulatingUi;

    internal ThemeEditorDialog(
        string themesDirectory,
        IReadOnlyList<ColorTheme> userThemes,
        string activeThemeId,
        Action<ColorTheme, bool> applyTheme,
        Action<List<ColorTheme>> updateUserThemes)
    {
        ArgumentNullException.ThrowIfNull(userThemes);
        ArgumentNullException.ThrowIfNull(applyTheme);
        ArgumentNullException.ThrowIfNull(updateUserThemes);

        _themesDirectory = themesDirectory;
        _updateUserThemes = updateUserThemes;
        ColorTheme initialTheme = ColorTheme.FindById(activeThemeId, userThemes);
        _activeThemeIdentity = new ThemeEditorActiveThemeIdentity(initialTheme.Id);
        _livePreviewSession = new ThemeEditorLivePreviewSession(initialTheme, applyTheme);

        InitializeComponent();
        BuildColorRows();
        WireEvents();
        ReloadThemeEntries([.. userThemes], selectedThemeId: activeThemeId);
    }

    public ThemeEditorDialog()
        : this(
            Path.Combine(AppContext.BaseDirectory, "themes"),
            [],
            ColorTheme.Dark.Id,
            (_, _) => { },
            _ => { })
    {
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _livePreviewSession.RevertIfNeeded();
        base.OnClosing(e);
    }

    private void WireEvents()
    {
        ThemeListBox.SelectionChanged += (_, _) => OnThemeSelectionChanged();
        ThemeIdTextBox.TextChanged += (_, _) => OnThemeIdentityChanged();
        ThemeNameTextBox.TextChanged += (_, _) => OnThemeIdentityChanged();
        BaseVariantCombo.SelectionChanged += (_, _) => OnBaseVariantChanged();

        DuplicateButton.Click += async (_, _) => await DuplicateSelectedThemeAsync();
        RenameButton.Click += async (_, _) => await RenameSelectedThemeAsync();
        DeleteButton.Click += async (_, _) => await DeleteSelectedThemeAsync();

        ApplyButton.Click += (_, _) => ApplyCurrentThemePreview();
        SaveButton.Click += (_, _) => SaveCurrentTheme();
        CancelButton.Click += (_, _) => Close();
    }

    private void BuildColorRows()
    {
        foreach (ColorSlotDefinition slot in ColorSlots) {
            Grid rowGrid = new() {
                ColumnDefinitions = new ColumnDefinitions("170,52,180,Auto"),
                ColumnSpacing = 8
            };

            TextBlock label = new() {
                Text = slot.Label,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            rowGrid.Children.Add(label);
            Grid.SetColumn(label, 0);

            ColorPicker colorPicker = new() {
                Width = 44,
                Height = 24,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            colorPicker.PropertyChanged += (_, e) => OnColorPickerPropertyChanged(slot.Key, colorPicker, e);
            rowGrid.Children.Add(colorPicker);
            Grid.SetColumn(colorPicker, 1);

            TextBox valueTextBox = new();
            valueTextBox.TextChanged += (_, _) => OnColorTextChanged(slot.Key, valueTextBox.Text ?? string.Empty);
            rowGrid.Children.Add(valueTextBox);
            Grid.SetColumn(valueTextBox, 2);

            Button resetButton = new() {
                Content = "Reset",
                MinWidth = 68
            };
            resetButton.Click += (_, _) => ResetColor(slot.Key);
            rowGrid.Children.Add(resetButton);
            Grid.SetColumn(resetButton, 3);

            ColorRowsHost.Children.Add(rowGrid);
            _colorRows[slot.Key] = new ColorRowControls(slot.Key, valueTextBox, colorPicker);
        }
    }

    private void ReloadThemeEntries(List<ColorTheme> userThemes, string? selectedThemeId = null)
    {
        _themeEntries.Clear();
        foreach (ColorTheme builtInTheme in ColorTheme.BuiltInThemes) {
            _themeEntries.Add(new ThemeListEntry(builtInTheme, isBuiltIn: true));
        }

        foreach (ColorTheme userTheme in userThemes) {
            _themeEntries.Add(new ThemeListEntry(userTheme, isBuiltIn: false));
        }

        ThemeListBox.ItemsSource = null;
        ThemeListBox.ItemsSource = _themeEntries;
        _updateUserThemes(userThemes);

        string targetId = string.IsNullOrWhiteSpace(selectedThemeId) ? _activeThemeIdentity.CommittedThemeId : selectedThemeId;
        ThemeListEntry? targetEntry = _themeEntries.FirstOrDefault(entry =>
            string.Equals(entry.Theme.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (targetEntry is null && _themeEntries.Count > 0)
            targetEntry = _themeEntries[0];

        if (targetEntry is not null)
            ThemeListBox.SelectedItem = targetEntry;
    }

    private void OnThemeSelectionChanged()
    {
        if (ThemeListBox.SelectedItem is not ThemeListEntry selectedEntry)
            return;

        _selectedThemeEntry = selectedEntry;
        _currentModel = EditableThemeModel.FromColorTheme(selectedEntry.Theme);

        _isPopulatingUi = true;
        ThemeIdTextBox.Text = _currentModel.Id;
        ThemeNameTextBox.Text = _currentModel.Name;
        BaseVariantCombo.SelectedIndex = _currentModel.BaseVariant == ThemeVariant.Light ? 1 : 0;

        foreach (ColorSlotDefinition slot in ColorSlots) {
            if (_colorRows.TryGetValue(slot.Key, out ColorRowControls? row)) {
                string value = GetModelColorValue(slot.Key);
                row.ValueTextBox.Text = value;
                UpdateColorPreview(row, value);
            }
        }

        _isPopulatingUi = false;
        UpdateCompactPreview();
        TryApplyLivePreviewFromCurrentModel();
        UpdateActionStates();
        SetStatusMessage(string.Empty, isError: false);
    }

    private void OnThemeIdentityChanged()
    {
        if (_isPopulatingUi || _currentModel is null)
            return;

        _currentModel.Id = ThemeIdTextBox.Text ?? string.Empty;
        _currentModel.Name = ThemeNameTextBox.Text ?? string.Empty;
        TryApplyLivePreviewFromCurrentModel();
        UpdateActionStates();
    }

    private void OnBaseVariantChanged()
    {
        if (_isPopulatingUi || _currentModel is null)
            return;

        _currentModel.BaseVariant = BaseVariantCombo.SelectedIndex == 1 ? ThemeVariant.Light : ThemeVariant.Dark;
        UpdateCompactPreview();
        TryApplyLivePreviewFromCurrentModel();
        UpdateActionStates();
    }

    private void OnColorTextChanged(string colorKey, string value)
    {
        if (_isPopulatingUi || _currentModel is null)
            return;

        bool updated = _currentModel.TrySetColorValue(colorKey, value);
        if (!updated)
            return;

        if (_colorRows.TryGetValue(colorKey, out ColorRowControls? row))
            UpdateColorPreview(row, value);

        UpdateCompactPreview();
        TryApplyLivePreviewFromCurrentModel();
        UpdateActionStates();
    }

    private void OnColorPickerPropertyChanged(string colorKey, ColorPicker picker, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isPopulatingUi || _currentModel is null || e.Property != ColorPicker.ColorProperty)
            return;

        string colorText = ColorTheme.FormatColor(picker.Color);
        if (_colorRows.TryGetValue(colorKey, out ColorRowControls? row) &&
            !string.Equals(row.ValueTextBox.Text, colorText, StringComparison.OrdinalIgnoreCase))
            row.ValueTextBox.Text = colorText;
    }

    private void ResetColor(string colorKey)
    {
        if (_currentModel is null || !_colorRows.TryGetValue(colorKey, out ColorRowControls? row))
            return;

        _currentModel.ResetColor(colorKey);
        string value = GetModelColorValue(colorKey);
        row.ValueTextBox.Text = value;
        UpdateColorPreview(row, value);
        UpdateCompactPreview();
        TryApplyLivePreviewFromCurrentModel();
        UpdateActionStates();
    }

    private async Task DuplicateSelectedThemeAsync()
    {
        if (_selectedThemeEntry is null)
            return;

        ThemeFileOperationResult result;
        if (_selectedThemeEntry.IsBuiltIn) {
            EditableThemeModel duplicateModel = EditableThemeModel.FromColorTheme(_selectedThemeEntry.Theme);
            duplicateModel.Id = GetUniqueDuplicateId(_selectedThemeEntry.Theme.Id + "-copy");
            duplicateModel.Name = GetUniqueDuplicateName(_selectedThemeEntry.Theme.Name + " Copy");
            result = UserThemeFileOperations.SaveUserTheme(_themesDirectory, duplicateModel, currentUserThemeId: null);
        } else {
            result = UserThemeFileOperations.DuplicateUserTheme(_themesDirectory, _selectedThemeEntry.Theme.Id);
        }

        if (!result.Success || result.Theme is null) {
            SetStatusMessage(result.Message, isError: true);
            return;
        }

        List<ColorTheme> userThemes = ColorTheme.LoadUserThemes(_themesDirectory);
        ReloadThemeEntries(userThemes, result.Theme.Id);
        SetStatusMessage(result.Message, isError: false);
        await Task.CompletedTask;
    }

    private async Task RenameSelectedThemeAsync()
    {
        if (_selectedThemeEntry is null || _selectedThemeEntry.IsBuiltIn) {
            SetStatusMessage("Only user themes can be renamed.", isError: true);
            return;
        }

        bool renamingActiveTheme = _activeThemeIdentity.IsCommittedTheme(_selectedThemeEntry.Theme.Id);

        ThemeIdentityDialog dialog = new("Rename Theme", _selectedThemeEntry.Theme.Id, _selectedThemeEntry.Theme.Name);
        await dialog.ShowDialog(this);
        if (!dialog.Applied)
            return;

        ThemeFileOperationResult result = UserThemeFileOperations.RenameUserTheme(
            _themesDirectory,
            _selectedThemeEntry.Theme.Id,
            dialog.ThemeId,
            dialog.ThemeName);

        if (!result.Success || result.Theme is null) {
            SetStatusMessage(result.Message, isError: true);
            return;
        }

        List<ColorTheme> userThemes = ColorTheme.LoadUserThemes(_themesDirectory);
        ReloadThemeEntries(userThemes, result.Theme.Id);
        if (renamingActiveTheme) {
            _livePreviewSession.Commit(result.Theme, persistSelection: true);
            _activeThemeIdentity.Commit(result.Theme.Id);
        }
        SetStatusMessage(result.Message, isError: false);
    }

    private async Task DeleteSelectedThemeAsync()
    {
        if (_selectedThemeEntry is null || _selectedThemeEntry.IsBuiltIn) {
            SetStatusMessage("Only user themes can be deleted.", isError: true);
            return;
        }

        ThemeDeleteDialog dialog = new(_selectedThemeEntry.Theme.Name);
        await dialog.ShowDialog(this);
        if (!dialog.Confirmed)
            return;

        ThemeFileOperationResult result = UserThemeFileOperations.DeleteUserTheme(_themesDirectory, _selectedThemeEntry.Theme.Id);
        if (!result.Success) {
            SetStatusMessage(result.Message, isError: true);
            return;
        }

        List<ColorTheme> userThemes = ColorTheme.LoadUserThemes(_themesDirectory);
        string fallbackThemeId = _activeThemeIdentity.CommittedThemeId;
        if (_activeThemeIdentity.IsCommittedTheme(_selectedThemeEntry.Theme.Id)) {
            fallbackThemeId = ColorTheme.Dark.Id;
            ColorTheme fallbackTheme = ColorTheme.FindById(fallbackThemeId, userThemes);
            _livePreviewSession.Commit(fallbackTheme, persistSelection: true);
            _activeThemeIdentity.Commit(fallbackTheme.Id);
        }

        ReloadThemeEntries(userThemes, fallbackThemeId);
        SetStatusMessage(result.Message, isError: false);
    }

    private void ApplyCurrentThemePreview()
    {
        if (_currentModel is null)
            return;

        List<ThemeValidationIssue> applyIssues = _currentModel.Validate();
        if (applyIssues.Count > 0) {
            SetStatusMessage("Cannot apply theme until validation errors are fixed.", isError: true);
            UpdateActionStates();
            return;
        }

        ColorTheme? previewTheme = _currentModel.ToColorTheme();
        if (previewTheme is null) {
            SetStatusMessage("Cannot apply theme because color values are invalid.", isError: true);
            return;
        }

        _livePreviewSession.Commit(previewTheme, persistSelection: false);
        _activeThemeIdentity.Commit(previewTheme.Id);
        SetStatusMessage("Applied theme for this session.", isError: false);
        UpdateActionStates();
    }

    private void SaveCurrentTheme()
    {
        if (_currentModel is null || _selectedThemeEntry is null)
            return;

        List<ThemeValidationIssue> applyIssues = _currentModel.Validate();
        List<ThemeValidationIssue> saveIssues = BuildSaveValidationIssues();
        if (applyIssues.Count > 0 || saveIssues.Count > 0) {
            SetStatusMessage("Cannot save theme until validation errors are fixed.", isError: true);
            UpdateActionStates();
            return;
        }

        string? currentUserThemeId = _selectedThemeEntry.IsBuiltIn ? null : _selectedThemeEntry.Theme.Id;
        ThemeFileOperationResult result = UserThemeFileOperations.SaveUserTheme(_themesDirectory, _currentModel, currentUserThemeId);
        if (!result.Success || result.Theme is null) {
            SetStatusMessage(result.Message, isError: true);
            return;
        }

        List<ColorTheme> userThemes = ColorTheme.LoadUserThemes(_themesDirectory);
        ReloadThemeEntries(userThemes, result.Theme.Id);
        _livePreviewSession.Commit(result.Theme, persistSelection: true);
        _activeThemeIdentity.Commit(result.Theme.Id);
        SetStatusMessage(result.Message, isError: false);
        UpdateActionStates();
    }

    private void UpdateActionStates()
    {
        List<ThemeValidationIssue> applyIssues = _currentModel?.Validate() ?? [];
        List<ThemeValidationIssue> saveIssues = BuildSaveValidationIssues();

        ApplyButton.IsEnabled = _selectedThemeEntry is not null && applyIssues.Count == 0 && _livePreviewSession.HasUncommittedPreview;
        SaveButton.IsEnabled = _selectedThemeEntry is not null && applyIssues.Count == 0 && saveIssues.Count == 0;
        DuplicateButton.IsEnabled = _selectedThemeEntry is not null;
        RenameButton.IsEnabled = _selectedThemeEntry is { IsBuiltIn: false };
        DeleteButton.IsEnabled = _selectedThemeEntry is { IsBuiltIn: false };

        StringBuilder validationTextBuilder = new();
        AppendIssues(validationTextBuilder, applyIssues);
        AppendIssues(validationTextBuilder, saveIssues);
        ValidationTextBlock.Text = validationTextBuilder.ToString();
    }

    private List<ThemeValidationIssue> BuildSaveValidationIssues()
    {
        List<ThemeValidationIssue> issues = [];
        if (_currentModel is null || _selectedThemeEntry is null)
            return issues;

        if (IsBuiltInThemeId(_currentModel.Id)) {
            bool selectedBuiltInWithSameId =
                _selectedThemeEntry.IsBuiltIn &&
                string.Equals(_selectedThemeEntry.Theme.Id, _currentModel.Id, StringComparison.OrdinalIgnoreCase);
            if (selectedBuiltInWithSameId || !_selectedThemeEntry.IsBuiltIn) {
                issues.Add(new ThemeValidationIssue("id", "Built-in theme IDs are reserved. Change ID or duplicate first."));
            }
        }

        foreach (ThemeListEntry entry in _themeEntries) {
            if (ReferenceEquals(entry, _selectedThemeEntry))
                continue;

            if (string.Equals(entry.Theme.Id, _currentModel.Id, StringComparison.OrdinalIgnoreCase)) {
                issues.Add(new ThemeValidationIssue("id", $"A theme with id '{_currentModel.Id}' already exists."));
                break;
            }
        }

        return issues;
    }

    private static void AppendIssues(StringBuilder builder, IReadOnlyList<ThemeValidationIssue> issues)
    {
        int maxIssueCount = Math.Min(issues.Count, 5);
        for (int i = 0; i < maxIssueCount; i++) {
            ThemeValidationIssue issue = issues[i];
            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append("• ");
            builder.Append(issue.Message);
        }
    }

    private void TryApplyLivePreviewFromCurrentModel()
    {
        if (_currentModel is null)
            return;

        List<ThemeValidationIssue> issues = _currentModel.Validate();
        if (issues.Count > 0)
            return;

        ColorTheme? previewTheme = _currentModel.ToColorTheme();
        if (previewTheme is null)
            return;

        _livePreviewSession.Preview(previewTheme);
        _activeThemeIdentity.UpdatePreview(previewTheme.Id);
    }

    private void UpdateCompactPreview()
    {
        if (_currentModel is null)
            return;

        ThemePreviewPalette palette = ThemePreviewPaletteBuilder.FromEditableModel(_currentModel);

        ThemePreviewRoot.Background = ToBrush(palette.Background);
        ThemePreviewRoot.BorderBrush = ToBrush(palette.GridLine);

        ThemePreviewHeader.Background = ToBrush(palette.HeaderBackground);
        ThemePreviewHeaderText.Foreground = ToBrush(palette.HeaderText);

        ThemePreviewGutter.Background = ToBrush(palette.GutterBackground);
        ThemePreviewGutterPrimaryText.Foreground = ToBrush(palette.TextSecondary);
        ThemePreviewGutterMutedText.Foreground = ToBrush(palette.TextMuted);

        ThemePreviewTextPrimary.Foreground = ToBrush(palette.TextPrimary);
        ThemePreviewTextSecondary.Foreground = ToBrush(palette.TextSecondary);
        ThemePreviewTextMuted.Foreground = ToBrush(palette.TextMuted);

        ThemePreviewSelectionChip.Background = ToBrush(palette.SelectionHighlight);
        ThemePreviewSelectionChip.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewSelectionChip.BorderThickness = new Thickness(1);
        ThemePreviewSelectionText.Foreground = ToBrush(palette.TextPrimary);

        ThemePreviewCursorChip.Background = ToBrush(palette.CursorHighlight);
        ThemePreviewCursorChip.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewCursorChip.BorderThickness = new Thickness(1);
        ThemePreviewCursorText.Foreground = ToBrush(palette.TextPrimary);
        ThemePreviewCursorBar.Background = ToBrush(palette.CursorBar);

        ThemePreviewRowStripeChip.Background = ToBrush(palette.RowStripe);
        ThemePreviewRowStripeChip.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewRowStripeChip.BorderThickness = new Thickness(1);
        ThemePreviewRowStripeText.Foreground = ToBrush(palette.TextPrimary);

        ThemePreviewColumnStripeChip.Background = ToBrush(palette.ColumnStripe);
        ThemePreviewColumnStripeChip.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewColumnStripeChip.BorderThickness = new Thickness(1);
        ThemePreviewColumnStripeText.Foreground = ToBrush(palette.TextPrimary);

        ThemePreviewGridHorizontalLine.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewGridVerticalLine.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewGridHorizontalLineRight.BorderBrush = ToBrush(palette.GridLine);

        ThemePreviewMatchChip.Background = ToBrush(palette.MatchHighlight);
        ThemePreviewMatchChip.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewMatchChip.BorderThickness = new Thickness(1);
        ThemePreviewMatchText.Foreground = ToBrush(palette.TextPrimary);

        ThemePreviewActiveMatchChip.Background = ToBrush(palette.ActiveMatchHighlight);
        ThemePreviewActiveMatchChip.BorderBrush = ToBrush(palette.GridLine);
        ThemePreviewActiveMatchChip.BorderThickness = new Thickness(1);
        ThemePreviewActiveMatchText.Foreground = ToBrush(palette.TextPrimary);
    }

    private static SolidColorBrush ToBrush(Color color) => new(color);

    private string GetModelColorValue(string colorKey)
    {
        if (_currentModel is null)
            return string.Empty;

        return colorKey switch {
            ThemeColorKeys.TextPrimary => _currentModel.TextPrimary,
            ThemeColorKeys.TextSecondary => _currentModel.TextSecondary,
            ThemeColorKeys.TextMuted => _currentModel.TextMuted,
            ThemeColorKeys.Background => _currentModel.Background,
            ThemeColorKeys.SelectionHighlight => _currentModel.SelectionHighlight,
            ThemeColorKeys.CursorHighlight => _currentModel.CursorHighlight,
            ThemeColorKeys.GridLine => _currentModel.GridLine,
            ThemeColorKeys.HeaderBackground => _currentModel.HeaderBackground,
            ThemeColorKeys.HeaderText => _currentModel.HeaderText,
            ThemeColorKeys.GutterBackground => _currentModel.GutterBackground,
            ThemeColorKeys.CursorBar => _currentModel.CursorBar,
            ThemeColorKeys.MatchHighlight => _currentModel.MatchHighlight,
            ThemeColorKeys.ActiveMatchHighlight => _currentModel.ActiveMatchHighlight,
            ThemeColorKeys.RowStripe => _currentModel.RowStripe,
            ThemeColorKeys.ColumnStripe => _currentModel.ColumnStripe,
            _ => string.Empty
        };
    }

    private static void UpdateColorPreview(ColorRowControls row, string value)
    {
        if (ColorTheme.TryParseColor(value, out Color color)) {
            if (row.ColorPicker.Color != color)
                row.ColorPicker.Color = color;
            row.ValueTextBox.Foreground = null;
        } else {
            row.ValueTextBox.Foreground = Brushes.IndianRed;
        }
    }

    private string GetUniqueDuplicateId(string baseId)
    {
        string candidate = baseId;
        int index = 2;
        while (ThemeIdExists(candidate)) {
            candidate = $"{baseId}-{index}";
            index++;
        }

        return candidate;
    }

    private string GetUniqueDuplicateName(string baseName)
    {
        string candidate = baseName;
        int index = 2;
        while (ThemeNameExists(candidate)) {
            candidate = $"{baseName} ({index})";
            index++;
        }

        return candidate;
    }

    private bool ThemeIdExists(string id)
    {
        foreach (ThemeListEntry entry in _themeEntries) {
            if (string.Equals(entry.Theme.Id, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool ThemeNameExists(string name)
    {
        foreach (ThemeListEntry entry in _themeEntries) {
            if (string.Equals(entry.Theme.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsBuiltInThemeId(string themeId)
    {
        foreach (ColorTheme builtInTheme in ColorTheme.BuiltInThemes) {
            if (string.Equals(builtInTheme.Id, themeId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void SetStatusMessage(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError ? Brushes.IndianRed : Brushes.ForestGreen;
    }

    private sealed record ColorSlotDefinition(string Key, string Label);

    private sealed class ThemeListEntry
    {
        public ThemeListEntry(ColorTheme theme, bool isBuiltIn)
        {
            Theme = theme;
            IsBuiltIn = isBuiltIn;
        }

        public ColorTheme Theme { get; }
        public bool IsBuiltIn { get; }

        public override string ToString()
        {
            string category = IsBuiltIn ? "Built-in" : "User";
            return $"{Theme.Name}  [{category}]";
        }
    }

    private sealed record ColorRowControls(string Key, TextBox ValueTextBox, ColorPicker ColorPicker);

    private sealed class ThemeIdentityDialog : Window
    {
        private readonly TextBox _idTextBox;
        private readonly TextBox _nameTextBox;

        public ThemeIdentityDialog(string title, string id, string name)
        {
            Title = title;
            Width = 420;
            Height = 190;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;

            Grid root = new() {
                Margin = new Thickness(16),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
                RowSpacing = 10
            };

            Grid identityGrid = new() {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                ColumnSpacing = 8,
                RowSpacing = 8
            };
            identityGrid.Children.Add(new TextBlock { Text = "ID", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            _idTextBox = new TextBox { Text = id };
            identityGrid.Children.Add(_idTextBox);
            Grid.SetColumn(_idTextBox, 1);

            TextBlock nameLabel = new() { Text = "Name", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            identityGrid.Children.Add(nameLabel);
            Grid.SetRow(nameLabel, 1);
            _nameTextBox = new TextBox { Text = name };
            identityGrid.Children.Add(_nameTextBox);
            Grid.SetRow(_nameTextBox, 1);
            Grid.SetColumn(_nameTextBox, 1);

            root.Children.Add(identityGrid);

            TextBlock hint = new() {
                Text = "Leave ID empty to generate it from the name.",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(hint);
            Grid.SetRow(hint, 1);

            StackPanel buttons = new() {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8
            };
            Button applyButton = new() { Content = "Apply", MinWidth = 80 };
            Button cancelButton = new() { Content = "Cancel", MinWidth = 80 };
            applyButton.Click += (_, _) => ApplyAndClose();
            cancelButton.Click += (_, _) => Close();
            buttons.Children.Add(applyButton);
            buttons.Children.Add(cancelButton);
            root.Children.Add(buttons);
            Grid.SetRow(buttons, 2);

            Content = root;
        }

        public bool Applied { get; private set; }
        public string ThemeId { get; private set; } = string.Empty;
        public string ThemeName { get; private set; } = string.Empty;

        private void ApplyAndClose()
        {
            ThemeId = (_idTextBox.Text ?? string.Empty).Trim();
            ThemeName = (_nameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(ThemeName))
                return;

            Applied = true;
            Close();
        }
    }

    private sealed class ThemeDeleteDialog : Window
    {
        public ThemeDeleteDialog(string themeName)
        {
            Title = "Delete Theme";
            Width = 420;
            Height = 160;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            StackPanel root = new() {
                Margin = new Thickness(16),
                Spacing = 12
            };
            root.Children.Add(new TextBlock {
                Text = $"Delete user theme '{themeName}'?",
                TextWrapping = TextWrapping.Wrap
            });

            StackPanel buttons = new() {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Spacing = 8
            };
            Button deleteButton = new() { Content = "Delete", MinWidth = 80 };
            Button cancelButton = new() { Content = "Cancel", MinWidth = 80 };
            deleteButton.Click += (_, _) => {
                Confirmed = true;
                Close();
            };
            cancelButton.Click += (_, _) => Close();
            buttons.Children.Add(deleteButton);
            buttons.Children.Add(cancelButton);
            root.Children.Add(buttons);

            Content = root;
        }

        public bool Confirmed { get; private set; }
    }

}
