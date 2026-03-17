using Avalonia.Styling;

using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for <see cref="ViewTheme"/> compatibility shim.
/// Verifies the shim correctly delegates to <see cref="ColorTheme"/>.
/// </summary>
public sealed class ViewThemeTests
{
    [Fact]
    public void Resolve_Dark_ReturnsDarkPalette()
    {
        ViewTheme theme = ViewTheme.Resolve(ThemeVariant.Dark);
        Assert.Same(ViewTheme.Dark, theme);
    }

    [Fact]
    public void Resolve_Light_ReturnsLightPalette()
    {
        ViewTheme theme = ViewTheme.Resolve(ThemeVariant.Light);
        Assert.Same(ViewTheme.Light, theme);
    }

    [Fact]
    public void Resolve_Default_ReturnsLightPalette()
    {
        // Default (non-Dark) should resolve to Light
        ViewTheme theme = ViewTheme.Resolve(ThemeVariant.Default);
        Assert.Same(ViewTheme.Light, theme);
    }

    [Fact]
    public void DarkPalette_AllBrushesNonNull()
    {
        ViewTheme dark = ViewTheme.Dark;
        Assert.NotNull(dark.TextPrimary);
        Assert.NotNull(dark.TextSecondary);
        Assert.NotNull(dark.TextMuted);
        Assert.NotNull(dark.Background);
        Assert.NotNull(dark.SelectionHighlight);
        Assert.NotNull(dark.CursorHighlight);
        Assert.NotNull(dark.GridLine);
        Assert.NotNull(dark.HeaderBackground);
        Assert.NotNull(dark.HeaderText);
        Assert.NotNull(dark.GutterBackground);
        Assert.NotNull(dark.CursorBar);
        Assert.NotNull(dark.GridLinePen);
        Assert.NotNull(dark.GutterPen);
    }

    [Fact]
    public void LightPalette_AllBrushesNonNull()
    {
        ViewTheme light = ViewTheme.Light;
        Assert.NotNull(light.TextPrimary);
        Assert.NotNull(light.TextSecondary);
        Assert.NotNull(light.TextMuted);
        Assert.NotNull(light.Background);
        Assert.NotNull(light.SelectionHighlight);
        Assert.NotNull(light.CursorHighlight);
        Assert.NotNull(light.GridLine);
        Assert.NotNull(light.HeaderBackground);
        Assert.NotNull(light.HeaderText);
        Assert.NotNull(light.GutterBackground);
        Assert.NotNull(light.CursorBar);
        Assert.NotNull(light.GridLinePen);
        Assert.NotNull(light.GutterPen);
    }

    [Fact]
    public void DarkAndLight_HaveDifferentTextPrimary()
    {
        Assert.NotSame(ViewTheme.Dark.TextPrimary, ViewTheme.Light.TextPrimary);
    }

    [Fact]
    public void DarkAndLight_HaveDifferentBackgrounds()
    {
        Assert.NotSame(ViewTheme.Dark.Background, ViewTheme.Light.Background);
    }
}
