using Avalonia.Styling;

using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for <see cref="EditableThemeModel"/> conversion, validation, and reset behavior.
/// </summary>
public sealed class EditableThemeModelTests
{
    [Fact]
    public void FromColorTheme_CopiesIdentityAndAllColorSlots()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.GreenPhosphor);

        Assert.Equal("green-phosphor", model.Id);
        Assert.Equal("Green Phosphor", model.Name);
        Assert.Equal(ThemeVariant.Dark, model.BaseVariant);
        Assert.Equal(ColorTheme.FormatBrushColor(ColorTheme.GreenPhosphor.TextPrimary), model.TextPrimary);
        Assert.Equal(ColorTheme.FormatBrushColor(ColorTheme.GreenPhosphor.ColumnStripe), model.ColumnStripe);
    }

    [Fact]
    public void ToThemeDto_ContainsAllColorKeysAndSelectedBase()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.Id = "custom-theme";
        model.Name = "Custom Theme";
        model.BaseVariant = ThemeVariant.Light;
        model.ResetAllColors();
        model.TextPrimary = "#112233";

        ThemeDto dto = model.ToThemeDto();

        Assert.Equal("custom-theme", dto.Id);
        Assert.Equal("Custom Theme", dto.Name);
        Assert.Equal("light", dto.Base);
        Assert.NotNull(dto.Colors);
        Assert.Equal(ThemeColorKeys.All.Count, dto.Colors!.Count);
        Assert.Equal("#112233", dto.Colors[ThemeColorKeys.TextPrimary]);
    }

    [Fact]
    public void ToColorTheme_ValidModel_ReturnsTheme()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.Id = "my-theme";
        model.Name = "My Theme";
        model.BaseVariant = ThemeVariant.Light;
        model.ResetAllColors();
        model.Background = "#AABBCC";

        ColorTheme? theme = model.ToColorTheme();

        Assert.NotNull(theme);
        Assert.Equal("my-theme", theme!.Id);
        Assert.Equal("My Theme", theme.Name);
        Assert.Equal(ThemeVariant.Light, theme.BaseVariant);
    }

    [Fact]
    public void Validate_InvalidIdNameAndColor_ReturnsPerFieldIssues()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.Id = "bad id";
        model.Name = "   ";
        model.CursorBar = "not-a-color";

        List<ThemeValidationIssue> issues = model.Validate();

        Assert.Contains(issues, issue => issue.Field == "id");
        Assert.Contains(issues, issue => issue.Field == "name");
        Assert.Contains(issues, issue => issue.Field == "colors.cursorBar");
    }

    [Fact]
    public void ResetColor_UsesCurrentBaseFallback()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.BaseVariant = ThemeVariant.Light;
        model.TextPrimary = "#010203";

        model.ResetColor(ThemeColorKeys.TextPrimary);

        Assert.Equal(
            ColorTheme.GetFallbackColorValue(ThemeColorKeys.TextPrimary, ThemeVariant.Light),
            model.TextPrimary);
    }
}
