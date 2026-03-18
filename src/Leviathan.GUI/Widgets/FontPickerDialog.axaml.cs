using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Modal font chooser with type-to-search, large preview panel, and explicit Apply/Cancel semantics.
/// </summary>
public sealed partial class FontPickerDialog : Window
{
    private const double MinFontSize = 8;
    private const double MaxFontSize = 72;
    private readonly List<string> _allFonts;
    private List<string> _filteredFonts = [];
    private readonly Action<string, double> _previewFont;
    private bool _suppressSelectionEvents;
    private bool _suppressSizeEvents;

    /// <summary>True if the user accepted the current selection.</summary>
    public bool Applied { get; private set; }

    /// <summary>The currently selected font family name.</summary>
    public string SelectedFontFamily { get; private set; }
    /// <summary>The currently selected font size.</summary>
    public double SelectedFontSize { get; private set; }

    public FontPickerDialog(
        IReadOnlyList<string> fonts,
        string initialFontFamily,
        double fontSize,
        Action<string, double> previewFont)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        ArgumentNullException.ThrowIfNull(initialFontFamily);
        ArgumentNullException.ThrowIfNull(previewFont);

        _previewFont = previewFont;
        SelectedFontSize = ClampFontSize(fontSize);
        _allFonts = BuildUniqueFontList(fonts, initialFontFamily);
        SelectedFontFamily = ResolveInitialSelection(initialFontFamily, _allFonts);

        InitializeComponent();
        FontSizeTextBox.Text = $"{SelectedFontSize:0}";
        WireEvents();
        RebuildFontList(SearchBox.Text);
        UpdatePreview(SelectedFontFamily);
    }

    public FontPickerDialog() : this(["Consolas"], "Consolas", 12, (_, _) => { }) { }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) {
            OnApply();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void WireEvents()
    {
        SearchBox.TextChanged += (_, _) => RebuildFontList(SearchBox.Text);
        SearchBox.KeyDown += OnSearchBoxKeyDown;
        FontListBox.SelectionChanged += (_, _) => OnFontSelectionChanged();
        FontListBox.DoubleTapped += (_, _) => OnApply();

        DecreaseSizeButton.Click += (_, _) => ChangeFontSizeBy(-1);
        IncreaseSizeButton.Click += (_, _) => ChangeFontSizeBy(+1);
        FontSizeTextBox.TextChanged += (_, _) => OnFontSizeTextChanged();
        FontSizeTextBox.LostFocus += (_, _) => NormalizeFontSizeText();
        FontSizeTextBox.KeyDown += OnFontSizeTextBoxKeyDown;

        ApplyButton.Click += (_, _) => OnApply();
        CancelButton.Click += (_, _) => Close();

        Opened += (_, _) => {
            Dispatcher.UIThread.Post(() => {
                SearchBox.Focus();
                SearchBox.SelectAll();
            }, DispatcherPriority.Input);
        };
    }

    private void OnApply()
    {
        Applied = true;
        Close();
    }

    private void RebuildFontList(string? filter)
    {
        string normalizedFilter = (filter ?? string.Empty).Trim();
        List<string> filteredFonts = [];

        foreach (string font in _allFonts) {
            if (normalizedFilter.Length == 0 || font.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                filteredFonts.Add(font);
        }

        _filteredFonts = filteredFonts;
        FontListBox.ItemsSource = _filteredFonts;
        if (_filteredFonts.Count == 0)
            return;

        string selected = _filteredFonts.FirstOrDefault(font => string.Equals(font, SelectedFontFamily, StringComparison.OrdinalIgnoreCase))
            ?? _filteredFonts[0];

        _suppressSelectionEvents = true;
        FontListBox.SelectedItem = selected;
        _suppressSelectionEvents = false;

        UpdatePreview(selected);
    }

    private void OnFontSelectionChanged()
    {
        if (_suppressSelectionEvents)
            return;

        if (FontListBox.SelectedItem is not string fontName)
            return;

        SelectedFontFamily = fontName;
        UpdatePreview(fontName);
        _previewFont(fontName, SelectedFontSize);
    }

    private void UpdatePreview(string fontFamily)
    {
        SelectedFontFamily = fontFamily;

        FontFamily family = new(fontFamily);
        SelectedFontTextBlock.Text = fontFamily;
        SelectedFontTextBlock.FontFamily = family;

        PreviewSampleLine1.FontFamily = family;
        PreviewSampleLine2.FontFamily = family;
        PreviewSampleLine3.FontFamily = family;
        PreviewSampleLine4.FontFamily = family;

        PreviewSampleLine1.FontSize = SelectedFontSize;
        PreviewSampleLine2.FontSize = SelectedFontSize;
        PreviewSampleLine3.FontSize = SelectedFontSize;
        PreviewSampleLine4.FontSize = SelectedFontSize;
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key) {
            case Key.Up:
                MoveSelectionBy(-1);
                e.Handled = true;
                return;
            case Key.Down:
                MoveSelectionBy(+1);
                e.Handled = true;
                return;
            case Key.PageUp:
                MoveSelectionBy(-8);
                e.Handled = true;
                return;
            case Key.PageDown:
                MoveSelectionBy(+8);
                e.Handled = true;
                return;
            case Key.Home:
                MoveSelectionTo(0);
                e.Handled = true;
                return;
            case Key.End:
                MoveSelectionTo(_filteredFonts.Count - 1);
                e.Handled = true;
                return;
            case Key.Enter:
                OnApply();
                e.Handled = true;
                return;
        }
    }

    private void MoveSelectionBy(int delta)
    {
        if (_filteredFonts.Count == 0)
            return;

        int currentIndex = FontListBox.SelectedIndex;
        if (currentIndex < 0)
            currentIndex = 0;

        MoveSelectionTo(currentIndex + delta);
    }

    private void MoveSelectionTo(int requestedIndex)
    {
        if (_filteredFonts.Count == 0)
            return;

        int clampedIndex = Math.Clamp(requestedIndex, 0, _filteredFonts.Count - 1);
        FontListBox.SelectedIndex = clampedIndex;
        FontListBox.ScrollIntoView(_filteredFonts[clampedIndex]);
    }

    private void ChangeFontSizeBy(int delta)
    {
        SetFontSize(SelectedFontSize + delta, livePreview: true);
    }

    private void OnFontSizeTextChanged()
    {
        if (_suppressSizeEvents)
            return;

        if (double.TryParse(FontSizeTextBox.Text, out double parsedSize))
            SetFontSize(parsedSize, livePreview: true);
    }

    private void OnFontSizeTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;

        if (double.TryParse(FontSizeTextBox.Text, out double parsedSize))
            SetFontSize(parsedSize, livePreview: true);
        else
            NormalizeFontSizeText();

        OnApply();
        e.Handled = true;
    }

    private void NormalizeFontSizeText()
    {
        _suppressSizeEvents = true;
        FontSizeTextBox.Text = $"{SelectedFontSize:0}";
        _suppressSizeEvents = false;
    }

    private void SetFontSize(double fontSize, bool livePreview)
    {
        double clampedSize = ClampFontSize(fontSize);
        if (Math.Abs(SelectedFontSize - clampedSize) < 0.01)
            return;

        SelectedFontSize = clampedSize;
        NormalizeFontSizeText();
        UpdatePreview(SelectedFontFamily);

        if (livePreview)
            _previewFont(SelectedFontFamily, SelectedFontSize);
    }

    private static double ClampFontSize(double size)
    {
        return Math.Clamp(size, MinFontSize, MaxFontSize);
    }

    private static string ResolveInitialSelection(string initialFontFamily, IReadOnlyList<string> fonts)
    {
        foreach (string font in fonts) {
            if (string.Equals(font, initialFontFamily, StringComparison.OrdinalIgnoreCase))
                return font;
        }

        return fonts.Count > 0 ? fonts[0] : "Consolas";
    }

    private static List<string> BuildUniqueFontList(IReadOnlyList<string> fonts, string initialFontFamily)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];

        if (!string.IsNullOrWhiteSpace(initialFontFamily) && seen.Add(initialFontFamily))
            result.Add(initialFontFamily);

        foreach (string font in fonts) {
            if (string.IsNullOrWhiteSpace(font))
                continue;

            if (seen.Add(font))
                result.Add(font);
        }

        if (result.Count == 0)
            result.Add("Consolas");

        return result;
    }
}
