using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Thin compatibility shim — delegates to <see cref="ColorTheme"/> for all color roles.
/// Maintained so that <c>ViewTheme.Resolve()</c> call sites continue to work
/// while the migration to <see cref="ColorTheme"/> proceeds.
/// </summary>
internal sealed class ViewTheme
{
    private readonly ColorTheme _inner;

    private ViewTheme(ColorTheme inner)
    {
        _inner = inner;
    }

    // ── Color roles (forwarded) ──────────────────────────────────────

    public IBrush TextPrimary => _inner.TextPrimary;
    public IBrush TextSecondary => _inner.TextSecondary;
    public IBrush TextMuted => _inner.TextMuted;
    public IBrush Background => _inner.Background;
    public IBrush SelectionHighlight => _inner.SelectionHighlight;
    public IBrush CursorHighlight => _inner.CursorHighlight;
    public IBrush GridLine => _inner.GridLine;
    public IBrush HeaderBackground => _inner.HeaderBackground;
    public IBrush HeaderText => _inner.HeaderText;
    public IBrush GutterBackground => _inner.GutterBackground;
    public IBrush CursorBar => _inner.CursorBar;
    public IBrush MatchHighlight => _inner.MatchHighlight;
    public IBrush ActiveMatchHighlight => _inner.ActiveMatchHighlight;

    /// <summary>Pen for separator / grid lines (cached in ColorTheme).</summary>
    public IPen GridLinePen => _inner.GridLinePen;

    /// <summary>Pen for gutter separator line (cached in ColorTheme).</summary>
    public IPen GutterPen => _inner.GutterPen;

    // ── Static palettes (via ColorTheme) ─────────────────────────────

    public static ViewTheme Dark { get; } = new(ColorTheme.Dark);
    public static ViewTheme Light { get; } = new(ColorTheme.Light);

    /// <summary>
    /// Resolves the correct palette for the current application theme variant.
    /// </summary>
    public static ViewTheme Resolve()
    {
        ThemeVariant? variant = Application.Current?.ActualThemeVariant;
        return variant == ThemeVariant.Dark ? Dark : Light;
    }

    /// <summary>
    /// Resolves the correct palette for a specific theme variant.
    /// </summary>
    public static ViewTheme Resolve(ThemeVariant variant)
    {
        return variant == ThemeVariant.Dark ? Dark : Light;
    }
}
