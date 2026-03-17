using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests committed-vs-preview identity behavior in the theme editor.
/// </summary>
public sealed class ThemeEditorActiveThemeIdentityTests
{
    [Fact]
    public void IsCommittedTheme_AfterUnsavedIdEdit_RenameCheckUsesCommittedId()
    {
        ThemeEditorActiveThemeIdentity identity = new("persisted-active");
        identity.UpdatePreview("unsaved-preview-id");

        Assert.Equal("persisted-active", identity.CommittedThemeId);
        Assert.Equal("unsaved-preview-id", identity.PreviewThemeId);
        Assert.True(identity.IsCommittedTheme("persisted-active"));
        Assert.False(identity.IsCommittedTheme("unsaved-preview-id"));
    }

    [Fact]
    public void IsCommittedTheme_AfterUnsavedIdEdit_DeleteCheckUsesCommittedId()
    {
        ThemeEditorActiveThemeIdentity identity = new("persisted-active");
        identity.UpdatePreview("unsaved-preview-id");

        Assert.True(identity.IsCommittedTheme("persisted-active"));
        Assert.False(identity.IsCommittedTheme("unsaved-preview-id"));
    }

    [Fact]
    public void Commit_AfterRenameOrDeleteBaselineChange_RevertTargetUsesLatestCommittedIdentity()
    {
        ThemeEditorActiveThemeIdentity identity = new("original-theme");
        identity.UpdatePreview("unsaved-id");
        identity.Commit("renamed-or-fallback-theme");
        identity.UpdatePreview("new-unsaved-id");

        Assert.Equal("renamed-or-fallback-theme", identity.CommittedThemeId);
        Assert.Equal("new-unsaved-id", identity.PreviewThemeId);
        Assert.True(identity.IsCommittedTheme("renamed-or-fallback-theme"));
        Assert.False(identity.IsCommittedTheme("original-theme"));
    }
}
