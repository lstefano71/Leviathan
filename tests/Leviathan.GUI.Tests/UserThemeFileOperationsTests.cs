using Avalonia.Styling;

using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for <see cref="UserThemeFileOperations"/> user-theme file workflows.
/// </summary>
public sealed class UserThemeFileOperationsTests
{
    [Fact]
    public void ImportTheme_ValidJson_ImportsThemeFile()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string sourcePath = Path.Combine(tempRoot, "source.json");
            string themesDirectory = Path.Combine(tempRoot, "themes");

            File.WriteAllText(sourcePath, """
            {
                "id": "custom-import",
                "name": "Custom Import",
                "base": "dark",
                "colors": {
                    "textPrimary": "#00FF00"
                }
            }
            """);

            ThemeFileOperationResult result = UserThemeFileOperations.ImportTheme(sourcePath, themesDirectory);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Theme);
            Assert.Equal("custom-import", result.Theme?.Id);
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ImportTheme_BuiltInIdConflict_AutoSuffixesThemeIdentity()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string sourcePath = Path.Combine(tempRoot, "source.json");
            string themesDirectory = Path.Combine(tempRoot, "themes");

            File.WriteAllText(sourcePath, """
            {
                "id": "dark",
                "name": "Dark",
                "base": "dark"
            }
            """);

            ThemeFileOperationResult result = UserThemeFileOperations.ImportTheme(sourcePath, themesDirectory);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Theme);
            Assert.False(string.Equals("dark", result.Theme?.Id, StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("dark-", result.Theme?.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.Equals("Dark", result.Theme?.Name, StringComparison.OrdinalIgnoreCase));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ImportTheme_UserIdAndNameConflict_AutoSuffixesAndPreservesColors()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string sourcePath = Path.Combine(tempRoot, "source.json");
            string themesDirectory = Path.Combine(tempRoot, "themes");
            ColorTheme existingTheme = CreateTheme("custom-import", "Custom Import", ThemeVariant.Dark);
            WriteTheme(themesDirectory, existingTheme);

            File.WriteAllText(sourcePath, """
            {
                "id": "custom-import",
                "name": "Custom Import",
                "base": "dark",
                "colors": {
                    "textPrimary": "#112233",
                    "background": "#010101"
                }
            }
            """);

            ThemeFileOperationResult result = UserThemeFileOperations.ImportTheme(sourcePath, themesDirectory);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Theme);
            Assert.False(string.Equals("custom-import", result.Theme!.Id, StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("custom-import-", result.Theme.Id, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.Equals("Custom Import", result.Theme.Name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("#112233", ColorTheme.FormatBrushColor(result.Theme.TextPrimary));
            Assert.Equal("#010101", ColorTheme.FormatBrushColor(result.Theme.Background));
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ImportTheme_InvalidJson_ReturnsExplicitError()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string sourcePath = Path.Combine(tempRoot, "source.json");
            string themesDirectory = Path.Combine(tempRoot, "themes");
            File.WriteAllText(sourcePath, "not-valid-json");

            ThemeFileOperationResult result = UserThemeFileOperations.ImportTheme(sourcePath, themesDirectory);

            Assert.False(result.Success);
            Assert.Contains("valid theme json", result.Message, StringComparison.OrdinalIgnoreCase);
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ExportTheme_ValidDestination_WritesLoadableJson()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string destinationPath = Path.Combine(tempRoot, "exports", "theme.json");

            ThemeFileOperationResult result = UserThemeFileOperations.ExportTheme(ColorTheme.Light, destinationPath);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(destinationPath));
            string json = File.ReadAllText(destinationPath);
            ColorTheme? loaded = ColorTheme.LoadFromJson(json);
            Assert.NotNull(loaded);
            Assert.Equal(ColorTheme.Light.Id, loaded?.Id);
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ExportTheme_RoundTrip_PreservesThemeIdentityAndColorValues()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string destinationPath = Path.Combine(tempRoot, "exports", "theme-rich.json");
            EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
            model.Id = "export-rich";
            model.Name = "Export Rich";
            model.TextPrimary = "#102030";
            model.Background = "#203040";
            model.GridLine = "#304050";
            model.ActiveMatchHighlight = "#AA405060";
            ColorTheme? sourceTheme = model.ToColorTheme();
            Assert.NotNull(sourceTheme);

            ThemeFileOperationResult result = UserThemeFileOperations.ExportTheme(sourceTheme!, destinationPath);

            Assert.True(result.Success, result.Message);
            string json = File.ReadAllText(destinationPath);
            ColorTheme? loaded = ColorTheme.LoadFromJson(json);
            Assert.NotNull(loaded);
            Assert.Equal(sourceTheme!.Id, loaded!.Id);
            Assert.Equal(sourceTheme.Name, loaded.Name);

            Dictionary<string, string> expectedColors = ColorTheme.ToColorValues(sourceTheme);
            Dictionary<string, string> loadedColors = ColorTheme.ToColorValues(loaded);
            foreach (string key in ThemeColorKeys.All) {
                Assert.True(loadedColors.TryGetValue(key, out string? loadedValue));
                Assert.Equal(expectedColors[key], loadedValue, ignoreCase: true);
            }
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void DuplicateUserTheme_ValidUserTheme_CreatesUniqueThemeAndFile()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string themesDirectory = Path.Combine(tempRoot, "themes");
            ColorTheme sourceTheme = CreateTheme("custom-theme", "Custom Theme", ThemeVariant.Dark);
            string sourcePath = WriteTheme(themesDirectory, sourceTheme);

            ThemeFileOperationResult result = UserThemeFileOperations.DuplicateUserTheme(themesDirectory, sourceTheme.Id);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Theme);
            Assert.False(string.Equals(sourceTheme.Id, result.Theme?.Id, StringComparison.OrdinalIgnoreCase));
            Assert.False(string.Equals(sourceTheme.Name, result.Theme?.Name, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(result.FilePath));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void DuplicateUserTheme_BuiltInTheme_ReturnsError()
    {
        ThemeFileOperationResult result = UserThemeFileOperations.DuplicateUserTheme(@"C:\themes", "dark");

        Assert.False(result.Success);
        Assert.Contains("immutable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenameUserTheme_IdAndNameConflict_AutoSuffixesAndRewritesFile()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string themesDirectory = Path.Combine(tempRoot, "themes");
            ColorTheme alphaTheme = CreateTheme("alpha", "Alpha", ThemeVariant.Dark);
            ColorTheme betaTheme = CreateTheme("beta", "Beta", ThemeVariant.Dark);
            string alphaPath = WriteTheme(themesDirectory, alphaTheme);
            WriteTheme(themesDirectory, betaTheme);

            ThemeFileOperationResult result = UserThemeFileOperations.RenameUserTheme(
                themesDirectory,
                currentUserThemeId: "alpha",
                requestedThemeId: "beta",
                requestedThemeName: "Beta");

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Theme);
            Assert.False(string.Equals("beta", result.Theme?.Id, StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("beta-", result.Theme?.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.Equals("Beta", result.Theme?.Name, StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(alphaPath));
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void RenameUserTheme_BuiltInTheme_ReturnsError()
    {
        ThemeFileOperationResult result = UserThemeFileOperations.RenameUserTheme(
            themesDirectory: @"C:\themes",
            currentUserThemeId: "light",
            requestedThemeId: "renamed-theme",
            requestedThemeName: "Renamed Theme");

        Assert.False(result.Success);
        Assert.Contains("immutable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenameUserTheme_DuplicateThemeIdsInDirectory_ReturnsConflictError()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string themesDirectory = Path.Combine(tempRoot, "themes");
            ColorTheme sourceTheme = CreateTheme("dup-theme", "Duplicate Theme", ThemeVariant.Dark);
            WriteThemeWithFileName(themesDirectory, "first.json", sourceTheme);
            WriteThemeWithFileName(themesDirectory, "second.json", sourceTheme);

            ThemeFileOperationResult result = UserThemeFileOperations.RenameUserTheme(
                themesDirectory,
                currentUserThemeId: sourceTheme.Id,
                requestedThemeId: "renamed",
                requestedThemeName: "Renamed");

            Assert.False(result.Success);
            Assert.Contains("multiple user theme files share id", result.Message, StringComparison.OrdinalIgnoreCase);
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void DeleteUserTheme_ValidUserTheme_RemovesThemeFile()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string themesDirectory = Path.Combine(tempRoot, "themes");
            ColorTheme sourceTheme = CreateTheme("delete-me", "Delete Me", ThemeVariant.Dark);
            string sourcePath = WriteTheme(themesDirectory, sourceTheme);

            ThemeFileOperationResult result = UserThemeFileOperations.DeleteUserTheme(themesDirectory, sourceTheme.Id);

            Assert.True(result.Success, result.Message);
            Assert.False(File.Exists(sourcePath));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void DeleteUserTheme_BuiltInTheme_ReturnsError()
    {
        ThemeFileOperationResult result = UserThemeFileOperations.DeleteUserTheme(@"C:\themes", "green-phosphor");

        Assert.False(result.Success);
        Assert.Contains("immutable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveUserTheme_NewTheme_SavesToThemesDirectory()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string themesDirectory = Path.Combine(tempRoot, "themes");
            EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
            model.Id = "editor-save";
            model.Name = "Editor Save";
            model.TextPrimary = "#224466";

            ThemeFileOperationResult result = UserThemeFileOperations.SaveUserTheme(themesDirectory, model);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Theme);
            Assert.Equal("editor-save", result.Theme?.Id);
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void SaveUserTheme_ExistingUserTheme_UpdatesThemeAndRenamesFile()
    {
        string tempRoot = CreateTempDirectory();
        try {
            string themesDirectory = Path.Combine(tempRoot, "themes");
            ColorTheme sourceTheme = CreateTheme("old-theme", "Old Theme", ThemeVariant.Dark);
            string sourcePath = WriteTheme(themesDirectory, sourceTheme);

            EditableThemeModel model = EditableThemeModel.FromColorTheme(sourceTheme);
            model.Id = "renamed-theme";
            model.Name = "Renamed Theme";
            model.TextPrimary = "#112233";

            ThemeFileOperationResult result = UserThemeFileOperations.SaveUserTheme(
                themesDirectory,
                model,
                currentUserThemeId: sourceTheme.Id);

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.Theme);
            Assert.Equal("renamed-theme", result.Theme?.Id);
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
            Assert.False(File.Exists(sourcePath));

            string json = File.ReadAllText(result.FilePath);
            ColorTheme? reloaded = ColorTheme.LoadFromJson(json);
            Assert.NotNull(reloaded);
            Assert.Equal("#112233", ColorTheme.FormatBrushColor(reloaded!.TextPrimary));
        } finally {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void SaveUserTheme_BuiltInThemeId_ReturnsError()
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.Id = "dark";
        model.Name = "Dark Override";

        ThemeFileOperationResult result = UserThemeFileOperations.SaveUserTheme(@"C:\themes", model);

        Assert.False(result.Success);
        Assert.Contains("reserved", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ColorTheme CreateTheme(string id, string name, ThemeVariant baseVariant)
    {
        ColorTheme seedTheme = baseVariant == ThemeVariant.Light ? ColorTheme.Light : ColorTheme.Dark;
        EditableThemeModel model = EditableThemeModel.FromColorTheme(seedTheme);
        model.Id = id;
        model.Name = name;
        model.BaseVariant = baseVariant;
        ColorTheme? theme = model.ToColorTheme();
        Assert.NotNull(theme);
        return theme!;
    }

    private static string WriteTheme(string themesDirectory, ColorTheme theme)
    {
        Directory.CreateDirectory(themesDirectory);
        string path = Path.Combine(themesDirectory, theme.Id + ".json");
        theme.SaveToJsonFile(path);
        return path;
    }

    private static string WriteThemeWithFileName(string themesDirectory, string fileName, ColorTheme theme)
    {
        Directory.CreateDirectory(themesDirectory);
        string path = Path.Combine(themesDirectory, fileName);
        theme.SaveToJsonFile(path);
        return path;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "leviathan_user_theme_ops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        } catch {
        }
    }
}
