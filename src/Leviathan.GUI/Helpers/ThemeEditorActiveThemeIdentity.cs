namespace Leviathan.GUI.Helpers;

/// <summary>
/// Tracks committed and transient theme identity state for the theme editor.
/// </summary>
internal sealed class ThemeEditorActiveThemeIdentity
{
    private string _committedThemeId;
    private string _previewThemeId;

    public ThemeEditorActiveThemeIdentity(string initialThemeId)
    {
        if (string.IsNullOrWhiteSpace(initialThemeId))
            throw new ArgumentException("Theme id cannot be empty.", nameof(initialThemeId));

        _committedThemeId = initialThemeId;
        _previewThemeId = initialThemeId;
    }

    /// <summary>
    /// Gets the persisted or explicitly committed active theme identity.
    /// </summary>
    public string CommittedThemeId => _committedThemeId;

    /// <summary>
    /// Gets the most recent transient preview identity.
    /// </summary>
    public string PreviewThemeId => _previewThemeId;

    /// <summary>
    /// Updates transient preview identity without changing committed baseline.
    /// </summary>
    public void UpdatePreview(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
            throw new ArgumentException("Theme id cannot be empty.", nameof(themeId));

        _previewThemeId = themeId;
    }

    /// <summary>
    /// Updates committed baseline identity and synchronizes preview identity.
    /// </summary>
    public void Commit(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
            throw new ArgumentException("Theme id cannot be empty.", nameof(themeId));

        _committedThemeId = themeId;
        _previewThemeId = themeId;
    }

    /// <summary>
    /// Gets whether the provided theme identity matches the committed baseline.
    /// </summary>
    public bool IsCommittedTheme(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
            return false;

        return string.Equals(_committedThemeId, themeId, StringComparison.OrdinalIgnoreCase);
    }
}
