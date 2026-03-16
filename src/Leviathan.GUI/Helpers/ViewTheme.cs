using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Provides theme-aware color palettes for the custom view controls.
/// Resolves Light and Dark variants so hex/text/CSV views remain legible
/// regardless of the active Avalonia theme.
/// </summary>
internal sealed class ViewTheme
{
    // ── Color roles ──────────────────────────────────────────────────

    /// <summary>Main content text (hex bytes, text characters, cell values).</summary>
    public IBrush TextPrimary { get; }

    /// <summary>Secondary text (addresses, line numbers, ASCII sidebar).</summary>
    public IBrush TextSecondary { get; }

    /// <summary>Dimmed text (non-printable ASCII placeholders).</summary>
    public IBrush TextMuted { get; }

    /// <summary>View control background — drawn explicitly in Render().</summary>
    public IBrush Background { get; }

    /// <summary>Selection highlight (translucent overlay).</summary>
    public IBrush SelectionHighlight { get; }

    /// <summary>Cursor / active-cell highlight (translucent overlay).</summary>
    public IBrush CursorHighlight { get; }

    /// <summary>Separator and grid lines.</summary>
    public IBrush GridLine { get; }

    /// <summary>CSV header / column header background strip.</summary>
    public IBrush HeaderBackground { get; }

    /// <summary>CSV header text.</summary>
    public IBrush HeaderText { get; }

    /// <summary>Line-number gutter background.</summary>
    public IBrush GutterBackground { get; }

    /// <summary>Cursor bar / caret in text mode.</summary>
    public IBrush CursorBar { get; }

    private ViewTheme(
        IBrush textPrimary, IBrush textSecondary, IBrush textMuted,
        IBrush background, IBrush selectionHighlight, IBrush cursorHighlight,
        IBrush gridLine, IBrush headerBackground, IBrush headerText,
        IBrush gutterBackground, IBrush cursorBar)
    {
        TextPrimary = textPrimary;
        TextSecondary = textSecondary;
        TextMuted = textMuted;
        Background = background;
        SelectionHighlight = selectionHighlight;
        CursorHighlight = cursorHighlight;
        GridLine = gridLine;
        HeaderBackground = headerBackground;
        HeaderText = headerText;
        GutterBackground = gutterBackground;
        CursorBar = cursorBar;
    }

    // ── Palette definitions ──────────────────────────────────────────

    /// <summary>Dark theme palette — light text on dark background.</summary>
    public static ViewTheme Dark { get; } = new(
        textPrimary:       new SolidColorBrush(Color.FromRgb(220, 220, 220)),      // #DCDCDC
        textSecondary:     new SolidColorBrush(Color.FromRgb(128, 128, 200)),      // muted blue-purple
        textMuted:         new SolidColorBrush(Color.FromRgb(120, 120, 120)),      // dim gray
        background:        new SolidColorBrush(Color.FromRgb(30, 30, 30)),         // #1E1E1E
        selectionHighlight:new SolidColorBrush(Color.FromArgb(80, 51, 153, 255)),  // translucent blue
        cursorHighlight:   new SolidColorBrush(Color.FromArgb(120, 255, 200, 50)), // translucent gold
        gridLine:          new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), // faint gray
        headerBackground:  new SolidColorBrush(Color.FromArgb(40, 100, 100, 200)), // faint blue
        headerText:        new SolidColorBrush(Color.FromRgb(200, 200, 255)),      // light lavender
        gutterBackground:  new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)), // near-transparent gray
        cursorBar:         new SolidColorBrush(Color.FromArgb(200, 220, 220, 220)) // bright caret
    );

    /// <summary>Light theme palette — dark text on light background.</summary>
    public static ViewTheme Light { get; } = new(
        textPrimary:       new SolidColorBrush(Color.FromRgb(30, 30, 30)),         // near-black
        textSecondary:     new SolidColorBrush(Color.FromRgb(80, 80, 140)),        // muted blue
        textMuted:         new SolidColorBrush(Color.FromRgb(160, 160, 160)),      // mid gray
        background:        new SolidColorBrush(Color.FromRgb(252, 252, 252)),      // near-white
        selectionHighlight:new SolidColorBrush(Color.FromArgb(60, 51, 120, 255)),  // translucent blue
        cursorHighlight:   new SolidColorBrush(Color.FromArgb(80, 255, 180, 0)),   // translucent amber
        gridLine:          new SolidColorBrush(Color.FromArgb(50, 80, 80, 80)),    // faint dark gray
        headerBackground:  new SolidColorBrush(Color.FromArgb(30, 60, 60, 160)),   // faint blue
        headerText:        new SolidColorBrush(Color.FromRgb(40, 40, 120)),        // dark blue
        gutterBackground:  new SolidColorBrush(Color.FromArgb(20, 80, 80, 80)),    // near-transparent
        cursorBar:         new SolidColorBrush(Color.FromArgb(220, 30, 30, 30))    // dark caret
    );

    /// <summary>
    /// Resolves the correct palette for the current application theme variant.
    /// Returns <see cref="Dark"/> for <see cref="ThemeVariant.Dark"/>,
    /// <see cref="Light"/> otherwise.
    /// </summary>
    public static ViewTheme Resolve()
    {
        ThemeVariant? variant = Application.Current?.ActualThemeVariant;
        return variant == ThemeVariant.Dark ? Dark : Light;
    }

    /// <summary>
    /// Resolves the correct palette for a specific theme variant.
    /// </summary>
    public static ViewTheme Resolve(ThemeVariant variant)
    {
        return variant == ThemeVariant.Dark ? Dark : Light;
    }

    /// <summary>Pen for separator / grid lines, cached per theme.</summary>
    public Pen GridLinePen => new(GridLine, 1);

    /// <summary>Pen for gutter separator line.</summary>
    public Pen GutterPen => new(GridLine, 1);
}
