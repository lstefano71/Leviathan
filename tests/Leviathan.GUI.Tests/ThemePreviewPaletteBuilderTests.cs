using Avalonia.Media;
using Avalonia.Styling;

using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests compact theme-preview palette generation from editable model values.
/// </summary>
public sealed class ThemePreviewPaletteBuilderTests
{
    [Fact]
    public void FromEditableModel_ValidOverrides_AppliesUpdatedPreviewColors()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.HeaderBackground = "#1A2B3C";
        model.TextPrimary = "#445566";
        model.ActiveMatchHighlight = "#CC778899";

        ThemePreviewPalette palette = ThemePreviewPaletteBuilder.FromEditableModel(model);

        Assert.Equal(ParseColor("#1A2B3C"), palette.HeaderBackground);
        Assert.Equal(ParseColor("#445566"), palette.TextPrimary);
        Assert.Equal(ParseColor("#CC778899"), palette.ActiveMatchHighlight);
    }

    [Fact]
    public void FromEditableModel_InvalidColor_UsesBaseVariantFallback()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.BaseVariant = ThemeVariant.Light;
        model.GridLine = "not-a-color";

        ThemePreviewPalette palette = ThemePreviewPaletteBuilder.FromEditableModel(model);
        Color expected = ParseColor(ColorTheme.GetFallbackColorValue(ThemeColorKeys.GridLine, ThemeVariant.Light));

        Assert.Equal(expected, palette.GridLine);
    }

    [Fact]
    public void FromEditableModel_AfterModelEdit_ReflectsLatestValue()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        ThemePreviewPalette before = ThemePreviewPaletteBuilder.FromEditableModel(model);

        model.MatchHighlight = "#8033AA55";
        ThemePreviewPalette after = ThemePreviewPaletteBuilder.FromEditableModel(model);

        Assert.NotEqual(before.MatchHighlight, after.MatchHighlight);
        Assert.Equal(ParseColor("#8033AA55"), after.MatchHighlight);
    }

    [Fact]
    public void FromEditableModel_BindsEveryPaletteSlotToMatchingModelField()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.Background = "#010203";
        model.HeaderBackground = "#111213";
        model.HeaderText = "#212223";
        model.GutterBackground = "#313233";
        model.TextPrimary = "#414243";
        model.TextSecondary = "#515253";
        model.TextMuted = "#616263";
        model.SelectionHighlight = "#717273";
        model.CursorHighlight = "#818283";
        model.CursorBar = "#919293";
        model.GridLine = "#A1A2A3";
        model.RowStripe = "#B1B2B3";
        model.ColumnStripe = "#C1C2C3";
        model.MatchHighlight = "#D1D2D3";
        model.ActiveMatchHighlight = "#E1E2E3";

        ThemePreviewPalette palette = ThemePreviewPaletteBuilder.FromEditableModel(model);

        Assert.Equal(ParseColor(model.Background), palette.Background);
        Assert.Equal(ParseColor(model.HeaderBackground), palette.HeaderBackground);
        Assert.Equal(ParseColor(model.HeaderText), palette.HeaderText);
        Assert.Equal(ParseColor(model.GutterBackground), palette.GutterBackground);
        Assert.Equal(ParseColor(model.TextPrimary), palette.TextPrimary);
        Assert.Equal(ParseColor(model.TextSecondary), palette.TextSecondary);
        Assert.Equal(ParseColor(model.TextMuted), palette.TextMuted);
        Assert.Equal(ParseColor(model.SelectionHighlight), palette.SelectionHighlight);
        Assert.Equal(ParseColor(model.CursorHighlight), palette.CursorHighlight);
        Assert.Equal(ParseColor(model.CursorBar), palette.CursorBar);
        Assert.Equal(ParseColor(model.GridLine), palette.GridLine);
        Assert.Equal(ParseColor(model.RowStripe), palette.RowStripe);
        Assert.Equal(ParseColor(model.ColumnStripe), palette.ColumnStripe);
        Assert.Equal(ParseColor(model.MatchHighlight), palette.MatchHighlight);
        Assert.Equal(ParseColor(model.ActiveMatchHighlight), palette.ActiveMatchHighlight);
    }

    private static Color ParseColor(string value)
    {
        bool success = ColorTheme.TryParseColor(value, out Color parsed);
        Assert.True(success, $"Expected color to parse: {value}");
        return parsed;
    }
}
