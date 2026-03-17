namespace Leviathan.GUI.Helpers;

/// <summary>
/// Tracks live-preview state in the theme editor so unsaved edits can be reverted.
/// </summary>
internal sealed class ThemeEditorLivePreviewSession
{
    private readonly Action<ColorTheme, bool> _applyTheme;
    private ColorTheme _committedTheme;

    public ThemeEditorLivePreviewSession(ColorTheme initialTheme, Action<ColorTheme, bool> applyTheme)
    {
        ArgumentNullException.ThrowIfNull(initialTheme);
        ArgumentNullException.ThrowIfNull(applyTheme);

        _committedTheme = initialTheme;
        _applyTheme = applyTheme;
    }

    /// <summary>
    /// Gets whether a non-persisted preview is currently active.
    /// </summary>
    public bool HasUncommittedPreview { get; private set; }

    /// <summary>
    /// Applies a live preview without persistence.
    /// </summary>
    public void Preview(ColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        _applyTheme(theme, false);
        HasUncommittedPreview = !AreEquivalent(theme, _committedTheme);
    }

    /// <summary>
    /// Commits the current theme as the new revert baseline.
    /// </summary>
    public void Commit(ColorTheme theme, bool persistSelection)
    {
        ArgumentNullException.ThrowIfNull(theme);

        _applyTheme(theme, persistSelection);
        _committedTheme = theme;
        HasUncommittedPreview = false;
    }

    /// <summary>
    /// Reverts any unsaved preview to the committed baseline.
    /// </summary>
    public void RevertIfNeeded()
    {
        if (!HasUncommittedPreview)
            return;

        _applyTheme(_committedTheme, false);
        HasUncommittedPreview = false;
    }

    private static bool AreEquivalent(ColorTheme left, ColorTheme right)
    {
        if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
            !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.BaseVariant != right.BaseVariant) {
            return false;
        }

        Dictionary<string, string> leftColors = ColorTheme.ToColorValues(left);
        Dictionary<string, string> rightColors = ColorTheme.ToColorValues(right);
        foreach (string key in ThemeColorKeys.All) {
            if (!leftColors.TryGetValue(key, out string? leftValue) ||
                !rightColors.TryGetValue(key, out string? rightValue) ||
                !string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
        }

        return true;
    }
}
