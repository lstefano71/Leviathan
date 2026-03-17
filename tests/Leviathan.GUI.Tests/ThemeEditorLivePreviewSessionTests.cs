using Avalonia.Styling;

using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for <see cref="ThemeEditorLivePreviewSession"/> live-preview and revert behavior.
/// </summary>
public sealed class ThemeEditorLivePreviewSessionTests
{
    [Fact]
    public void Preview_ValidTheme_AppliesWithoutPersistenceAndMarksUncommitted()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ThemeEditorLivePreviewSession session = new(ColorTheme.Dark, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });
        ColorTheme previewTheme = CreateTheme("preview-theme", "Preview Theme", "#123456");

        session.Preview(previewTheme);

        Assert.True(session.HasUncommittedPreview);
        Assert.Single(applied);
        Assert.Same(previewTheme, applied[0].Theme);
        Assert.False(applied[0].PersistSelection);
    }

    [Fact]
    public void RevertIfNeeded_AfterPreview_ReappliesCommittedThemeWithoutPersistence()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ThemeEditorLivePreviewSession session = new(ColorTheme.Dark, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });
        ColorTheme previewTheme = CreateTheme("preview-theme", "Preview Theme", "#654321");

        session.Preview(previewTheme);
        session.RevertIfNeeded();

        Assert.False(session.HasUncommittedPreview);
        Assert.Equal(2, applied.Count);
        Assert.Same(ColorTheme.Dark, applied[1].Theme);
        Assert.False(applied[1].PersistSelection);
    }

    [Fact]
    public void RevertIfNeeded_WithoutUncommittedPreview_DoesNotApplyTheme()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ThemeEditorLivePreviewSession session = new(ColorTheme.Dark, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });

        session.RevertIfNeeded();

        Assert.False(session.HasUncommittedPreview);
        Assert.Empty(applied);
    }

    [Fact]
    public void Commit_WithPersistence_AppliesPersistedThemeAndClearsUncommitted()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ThemeEditorLivePreviewSession session = new(ColorTheme.Dark, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });
        ColorTheme savedTheme = CreateTheme("saved-theme", "Saved Theme", "#224466");

        session.Commit(savedTheme, persistSelection: true);
        session.RevertIfNeeded();

        Assert.False(session.HasUncommittedPreview);
        Assert.Single(applied);
        Assert.Same(savedTheme, applied[0].Theme);
        Assert.True(applied[0].PersistSelection);
    }

    [Fact]
    public void RevertIfNeeded_AfterCommitAndFurtherPreview_RevertsToLastCommittedTheme()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ThemeEditorLivePreviewSession session = new(ColorTheme.Dark, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });
        ColorTheme savedTheme = CreateTheme("saved-theme", "Saved Theme", "#AABBCC");
        ColorTheme scratchTheme = CreateTheme("scratch-theme", "Scratch Theme", "#BBCCDD");

        session.Commit(savedTheme, persistSelection: true);
        session.Preview(scratchTheme);
        session.RevertIfNeeded();

        Assert.False(session.HasUncommittedPreview);
        Assert.Equal(3, applied.Count);
        Assert.Same(savedTheme, applied[2].Theme);
        Assert.False(applied[2].PersistSelection);
    }

    [Fact]
    public void Commit_AfterThemeRename_RevertRestoresRenamedThemeIdentity()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ColorTheme initialTheme = CreateTheme("old-theme", "Old Theme", "#101112");
        ThemeEditorLivePreviewSession session = new(initialTheme, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });
        ColorTheme renamedTheme = CreateTheme("renamed-theme", "Renamed Theme", "#334455");
        ColorTheme scratchTheme = CreateTheme("scratch-theme", "Scratch Theme", "#556677");

        session.Commit(renamedTheme, persistSelection: true);
        session.Preview(scratchTheme);
        session.RevertIfNeeded();

        Assert.False(session.HasUncommittedPreview);
        Assert.Equal(3, applied.Count);
        Assert.Equal("renamed-theme", applied[2].Theme.Id);
        Assert.Equal("Renamed Theme", applied[2].Theme.Name);
        Assert.NotEqual(initialTheme.Id, applied[2].Theme.Id);
        Assert.False(applied[2].PersistSelection);
    }

    [Fact]
    public void Commit_AfterThemeDeleteFallback_RevertRestoresFallbackInsteadOfDeletedIdentity()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ColorTheme deletedTheme = CreateTheme("deleted-theme", "Deleted Theme", "#121212");
        ThemeEditorLivePreviewSession session = new(deletedTheme, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });
        ColorTheme fallbackTheme = ColorTheme.Dark;
        ColorTheme scratchTheme = CreateTheme("scratch-theme", "Scratch Theme", "#223344");

        session.Commit(fallbackTheme, persistSelection: true);
        session.Preview(scratchTheme);
        session.RevertIfNeeded();

        Assert.False(session.HasUncommittedPreview);
        Assert.Equal(3, applied.Count);
        Assert.Equal(ColorTheme.Dark.Id, applied[2].Theme.Id);
        Assert.NotEqual(deletedTheme.Id, applied[2].Theme.Id);
        Assert.False(applied[2].PersistSelection);
    }

    [Fact]
    public void Preview_EquivalentToCommitted_DoesNotMarkUncommitted()
    {
        List<(ColorTheme Theme, bool PersistSelection)> applied = [];
        ThemeEditorLivePreviewSession session = new(ColorTheme.Dark, (theme, persistSelection) => {
            applied.Add((theme, persistSelection));
        });

        session.Preview(ColorTheme.Dark);

        Assert.False(session.HasUncommittedPreview);
        Assert.Single(applied);
        Assert.Same(ColorTheme.Dark, applied[0].Theme);
        Assert.False(applied[0].PersistSelection);
    }

    private static ColorTheme CreateTheme(string id, string name, string textPrimary)
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(ColorTheme.Dark);
        model.Id = id;
        model.Name = name;
        model.BaseVariant = ThemeVariant.Dark;
        model.TextPrimary = textPrimary;
        ColorTheme? theme = model.ToColorTheme();
        Assert.NotNull(theme);
        return theme!;
    }
}
