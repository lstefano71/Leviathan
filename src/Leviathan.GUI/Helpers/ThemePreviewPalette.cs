using Avalonia.Media;
using Avalonia.Styling;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Color snapshot for the compact theme editor preview panel.
/// </summary>
internal readonly record struct ThemePreviewPalette(
    Color Background,
    Color HeaderBackground,
    Color HeaderText,
    Color GutterBackground,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted,
    Color SelectionHighlight,
    Color CursorHighlight,
    Color CursorBar,
    Color GridLine,
    Color RowStripe,
    Color ColumnStripe,
    Color MatchHighlight,
    Color ActiveMatchHighlight);

/// <summary>
/// Builds compact-preview palettes from editable theme values.
/// </summary>
internal static class ThemePreviewPaletteBuilder
{
    /// <summary>
    /// Resolves all preview colors from an editable model, falling back to built-in base colors when invalid.
    /// </summary>
    public static ThemePreviewPalette FromEditableModel(EditableThemeModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new ThemePreviewPalette(
            Background: ResolveColor(model.Background, ThemeColorKeys.Background, model.BaseVariant),
            HeaderBackground: ResolveColor(model.HeaderBackground, ThemeColorKeys.HeaderBackground, model.BaseVariant),
            HeaderText: ResolveColor(model.HeaderText, ThemeColorKeys.HeaderText, model.BaseVariant),
            GutterBackground: ResolveColor(model.GutterBackground, ThemeColorKeys.GutterBackground, model.BaseVariant),
            TextPrimary: ResolveColor(model.TextPrimary, ThemeColorKeys.TextPrimary, model.BaseVariant),
            TextSecondary: ResolveColor(model.TextSecondary, ThemeColorKeys.TextSecondary, model.BaseVariant),
            TextMuted: ResolveColor(model.TextMuted, ThemeColorKeys.TextMuted, model.BaseVariant),
            SelectionHighlight: ResolveColor(model.SelectionHighlight, ThemeColorKeys.SelectionHighlight, model.BaseVariant),
            CursorHighlight: ResolveColor(model.CursorHighlight, ThemeColorKeys.CursorHighlight, model.BaseVariant),
            CursorBar: ResolveColor(model.CursorBar, ThemeColorKeys.CursorBar, model.BaseVariant),
            GridLine: ResolveColor(model.GridLine, ThemeColorKeys.GridLine, model.BaseVariant),
            RowStripe: ResolveColor(model.RowStripe, ThemeColorKeys.RowStripe, model.BaseVariant),
            ColumnStripe: ResolveColor(model.ColumnStripe, ThemeColorKeys.ColumnStripe, model.BaseVariant),
            MatchHighlight: ResolveColor(model.MatchHighlight, ThemeColorKeys.MatchHighlight, model.BaseVariant),
            ActiveMatchHighlight: ResolveColor(model.ActiveMatchHighlight, ThemeColorKeys.ActiveMatchHighlight, model.BaseVariant));
    }

    private static Color ResolveColor(string value, string colorKey, ThemeVariant baseVariant)
    {
        if (ColorTheme.TryParseColor(value, out Color parsed))
            return parsed;

        string fallbackValue = ColorTheme.GetFallbackColorValue(colorKey, baseVariant);
        return ColorTheme.TryParseColor(fallbackValue, out parsed) ? parsed : Colors.Transparent;
    }
}
