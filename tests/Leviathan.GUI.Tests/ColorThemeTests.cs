using Avalonia.Media;
using Avalonia.Styling;
using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for <see cref="ColorTheme"/> — built-in palettes, JSON loading,
/// color parsing, fallback, and user theme loading.
/// </summary>
public sealed class ColorThemeTests
{
    // ── Built-in palette non-null checks ─────────────────────────────

    [Fact]
    public void Dark_AllBrushesAndPensNonNull()
    {
        ColorTheme dark = ColorTheme.Dark;
        AssertAllPropertiesNonNull(dark);
    }

    [Fact]
    public void Light_AllBrushesAndPensNonNull()
    {
        ColorTheme light = ColorTheme.Light;
        AssertAllPropertiesNonNull(light);
    }

    [Fact]
    public void GreenPhosphor_AllBrushesAndPensNonNull()
    {
        ColorTheme gp = ColorTheme.GreenPhosphor;
        AssertAllPropertiesNonNull(gp);
    }

    [Fact]
    public void AmberPhosphor_AllBrushesAndPensNonNull()
    {
        ColorTheme ap = ColorTheme.AmberPhosphor;
        AssertAllPropertiesNonNull(ap);
    }

    // ── Identity checks ──────────────────────────────────────────────

    [Fact]
    public void BuiltInThemes_HaveUniqueIds()
    {
        HashSet<string> ids = [];
        foreach (ColorTheme theme in ColorTheme.BuiltInThemes)
        {
            Assert.True(ids.Add(theme.Id), $"Duplicate ID: {theme.Id}");
        }
    }

    [Fact]
    public void Dark_HasDarkBaseVariant()
    {
        Assert.Equal(ThemeVariant.Dark, ColorTheme.Dark.BaseVariant);
    }

    [Fact]
    public void Light_HasLightBaseVariant()
    {
        Assert.Equal(ThemeVariant.Light, ColorTheme.Light.BaseVariant);
    }

    [Fact]
    public void GreenPhosphor_HasDarkBaseVariant()
    {
        Assert.Equal(ThemeVariant.Dark, ColorTheme.GreenPhosphor.BaseVariant);
    }

    [Fact]
    public void AmberPhosphor_HasDarkBaseVariant()
    {
        Assert.Equal(ThemeVariant.Dark, ColorTheme.AmberPhosphor.BaseVariant);
    }

    [Fact]
    public void DarkAndLight_HaveDifferentTextPrimary()
    {
        Assert.NotSame(ColorTheme.Dark.TextPrimary, ColorTheme.Light.TextPrimary);
    }

    [Fact]
    public void DarkAndLight_HaveDifferentBackground()
    {
        Assert.NotSame(ColorTheme.Dark.Background, ColorTheme.Light.Background);
    }

    // ── Pen caching ──────────────────────────────────────────────────

    [Fact]
    public void GridLinePen_ReturnsSameInstance()
    {
        IPen pen1 = ColorTheme.Dark.GridLinePen;
        IPen pen2 = ColorTheme.Dark.GridLinePen;
        Assert.Same(pen1, pen2);
    }

    [Fact]
    public void GutterPen_ReturnsSameInstance()
    {
        IPen pen1 = ColorTheme.Dark.GutterPen;
        IPen pen2 = ColorTheme.Dark.GutterPen;
        Assert.Same(pen1, pen2);
    }

    // ── FindById ─────────────────────────────────────────────────────

    [Fact]
    public void FindById_Dark_ReturnsDark()
    {
        Assert.Same(ColorTheme.Dark, ColorTheme.FindById("dark"));
    }

    [Fact]
    public void FindById_Light_ReturnsLight()
    {
        Assert.Same(ColorTheme.Light, ColorTheme.FindById("light"));
    }

    [Fact]
    public void FindById_GreenPhosphor_ReturnsGreenPhosphor()
    {
        Assert.Same(ColorTheme.GreenPhosphor, ColorTheme.FindById("green-phosphor"));
    }

    [Fact]
    public void FindById_CaseInsensitive()
    {
        Assert.Same(ColorTheme.Dark, ColorTheme.FindById("DARK"));
        Assert.Same(ColorTheme.Light, ColorTheme.FindById("Light"));
    }

    [Fact]
    public void FindById_Unknown_FallsToDark()
    {
        Assert.Same(ColorTheme.Dark, ColorTheme.FindById("does-not-exist"));
    }

    // ── Color parsing ────────────────────────────────────────────────

    [Fact]
    public void TryParseColor_HexRgb_Parses()
    {
        Assert.True(ColorTheme.TryParseColor("#33FF33", out Color color));
        Assert.Equal(255, color.A);
        Assert.Equal(0x33, color.R);
        Assert.Equal(0xFF, color.G);
        Assert.Equal(0x33, color.B);
    }

    [Fact]
    public void TryParseColor_HexArgb_Parses()
    {
        Assert.True(ColorTheme.TryParseColor("#80FF0000", out Color color));
        Assert.Equal(0x80, color.A);
        Assert.Equal(0xFF, color.R);
        Assert.Equal(0, color.G);
        Assert.Equal(0, color.B);
    }

    [Fact]
    public void TryParseColor_Rgba_Parses()
    {
        Assert.True(ColorTheme.TryParseColor("rgba(51,255,51,80)", out Color color));
        Assert.Equal(80, color.A);
        Assert.Equal(51, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(51, color.B);
    }

    [Fact]
    public void TryParseColor_Invalid_ReturnsFalse()
    {
        Assert.False(ColorTheme.TryParseColor("not-a-color", out _));
        Assert.False(ColorTheme.TryParseColor("#GG0000", out _));
        Assert.False(ColorTheme.TryParseColor("", out _));
    }

    [Fact]
    public void TryParseColor_WithWhitespace_TrimsAndParses()
    {
        Assert.True(ColorTheme.TryParseColor("  #FF0000  ", out Color color));
        Assert.Equal(0xFF, color.R);
    }

    // ── JSON loading ─────────────────────────────────────────────────

    [Fact]
    public void LoadFromJson_ValidDarkTheme_Parses()
    {
        string json = """
        {
            "name": "Test Theme",
            "base": "dark",
            "colors": {
                "textPrimary": "#00FF00",
                "background": "#000000"
            }
        }
        """;

        ColorTheme? theme = ColorTheme.LoadFromJson(json);
        Assert.NotNull(theme);
        Assert.Equal("test-theme", theme.Id);
        Assert.Equal("Test Theme", theme.Name);
        Assert.Equal(ThemeVariant.Dark, theme.BaseVariant);

        // Specified colors should be applied
        SolidColorBrush primary = Assert.IsType<SolidColorBrush>(theme.TextPrimary);
        Assert.Equal(0, primary.Color.R);
        Assert.Equal(255, primary.Color.G);
        Assert.Equal(0, primary.Color.B);

        // Unspecified colors should fall back to dark palette
        Assert.NotNull(theme.GridLine);
        Assert.NotNull(theme.SelectionHighlight);
    }

    [Fact]
    public void LoadFromJson_ValidLightTheme_UsesLightFallback()
    {
        string json = """
        {
            "name": "Light Custom",
            "base": "light",
            "colors": {
                "textPrimary": "#112233"
            }
        }
        """;

        ColorTheme? theme = ColorTheme.LoadFromJson(json);
        Assert.NotNull(theme);
        Assert.Equal(ThemeVariant.Light, theme.BaseVariant);
        // Background should fall back to the light built-in
        Assert.Same(ColorTheme.Light.Background, theme.Background);
    }

    [Fact]
    public void LoadFromJson_WithExplicitId_UsesProvidedId()
    {
        string json = """
        {
            "id": "my-id",
            "name": "My Theme",
            "base": "dark"
        }
        """;

        ColorTheme? theme = ColorTheme.LoadFromJson(json);
        Assert.NotNull(theme);
        Assert.Equal("my-id", theme.Id);
    }

    [Fact]
    public void LoadFromJson_MissingName_ReturnsNull()
    {
        string json = """{ "base": "dark" }""";
        Assert.Null(ColorTheme.LoadFromJson(json));
    }

    [Fact]
    public void LoadFromJson_InvalidJson_ReturnsNull()
    {
        Assert.Null(ColorTheme.LoadFromJson("not json at all"));
    }

    [Fact]
    public void LoadFromJson_EmptyColors_FallsBackCompletely()
    {
        string json = """
        {
            "name": "Minimal",
            "base": "dark",
            "colors": {}
        }
        """;

        ColorTheme? theme = ColorTheme.LoadFromJson(json);
        Assert.NotNull(theme);
        // All colors should match Dark fallback
        Assert.Same(ColorTheme.Dark.TextPrimary, theme.TextPrimary);
        Assert.Same(ColorTheme.Dark.Background, theme.Background);
    }

    [Fact]
    public void LoadFromJson_InvalidColorValue_FallsBack()
    {
        string json = """
        {
            "name": "Bad Color",
            "base": "dark",
            "colors": {
                "textPrimary": "not-a-color",
                "background": "#000000"
            }
        }
        """;

        ColorTheme? theme = ColorTheme.LoadFromJson(json);
        Assert.NotNull(theme);
        // textPrimary should fall back to dark
        Assert.Same(ColorTheme.Dark.TextPrimary, theme.TextPrimary);
        // background was valid
        SolidColorBrush bg = Assert.IsType<SolidColorBrush>(theme.Background);
        Assert.Equal(0, bg.Color.R);
        Assert.Equal(0, bg.Color.G);
        Assert.Equal(0, bg.Color.B);
    }

    // ── User theme loading ───────────────────────────────────────────

    [Fact]
    public void LoadUserThemes_NonExistentDirectory_ReturnsEmpty()
    {
        List<ColorTheme> themes = ColorTheme.LoadUserThemes(@"C:\does_not_exist_12345");
        Assert.Empty(themes);
    }

    [Fact]
    public void LoadUserThemes_ValidThemeFile_LoadsTheme()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "leviathan_test_themes_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "test.json"), """
            {
                "name": "Test User Theme",
                "base": "dark",
                "colors": { "textPrimary": "#FF0000" }
            }
            """);

            List<ColorTheme> themes = ColorTheme.LoadUserThemes(tempDir);
            Assert.Single(themes);
            Assert.Equal("Test User Theme", themes[0].Name);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void LoadUserThemes_InvalidFile_SkipsGracefully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "leviathan_test_themes_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "bad.json"), "not valid json");
            File.WriteAllText(Path.Combine(tempDir, "good.json"), """
            {
                "name": "Good Theme",
                "base": "light",
                "colors": {}
            }
            """);

            List<ColorTheme> themes = ColorTheme.LoadUserThemes(tempDir);
            Assert.Single(themes);
            Assert.Equal("Good Theme", themes[0].Name);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void FindById_WithUserThemes_FindsUserTheme()
    {
        string json = """
        {
            "id": "custom-user",
            "name": "Custom User",
            "base": "dark"
        }
        """;
        ColorTheme userTheme = ColorTheme.LoadFromJson(json)!;
        List<ColorTheme> userThemes = [userTheme];

        ColorTheme found = ColorTheme.FindById("custom-user", userThemes);
        Assert.Same(userTheme, found);
    }

    [Fact]
    public void FindById_BuiltInTakesPrecedenceOverUser()
    {
        string json = """
        {
            "id": "dark",
            "name": "Custom Dark",
            "base": "dark"
        }
        """;
        ColorTheme userTheme = ColorTheme.LoadFromJson(json)!;
        List<ColorTheme> userThemes = [userTheme];

        // Built-in "dark" should be returned, not the user's "dark"
        ColorTheme found = ColorTheme.FindById("dark", userThemes);
        Assert.Same(ColorTheme.Dark, found);
    }

    // ── ViewTheme shim ───────────────────────────────────────────────

    [Fact]
    public void ViewThemeShim_Dark_DelegatesToColorTheme()
    {
        ViewTheme dark = ViewTheme.Dark;
        // Should expose the same brush instances as ColorTheme.Dark
        Assert.Same(ColorTheme.Dark.TextPrimary, dark.TextPrimary);
        Assert.Same(ColorTheme.Dark.Background, dark.Background);
    }

    [Fact]
    public void ViewThemeShim_Resolve_ReturnsDarkOrLight()
    {
        ViewTheme dark = ViewTheme.Resolve(ThemeVariant.Dark);
        Assert.Same(ViewTheme.Dark, dark);

        ViewTheme light = ViewTheme.Resolve(ThemeVariant.Light);
        Assert.Same(ViewTheme.Light, light);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void AssertAllPropertiesNonNull(ColorTheme theme)
    {
        Assert.NotNull(theme.Id);
        Assert.NotNull(theme.Name);
        Assert.NotNull(theme.TextPrimary);
        Assert.NotNull(theme.TextSecondary);
        Assert.NotNull(theme.TextMuted);
        Assert.NotNull(theme.Background);
        Assert.NotNull(theme.SelectionHighlight);
        Assert.NotNull(theme.CursorHighlight);
        Assert.NotNull(theme.GridLine);
        Assert.NotNull(theme.HeaderBackground);
        Assert.NotNull(theme.HeaderText);
        Assert.NotNull(theme.GutterBackground);
        Assert.NotNull(theme.CursorBar);
        Assert.NotNull(theme.MatchHighlight);
        Assert.NotNull(theme.ActiveMatchHighlight);
        Assert.NotNull(theme.GridLinePen);
        Assert.NotNull(theme.GutterPen);
    }
}
