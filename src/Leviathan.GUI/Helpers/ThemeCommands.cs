namespace Leviathan.GUI.Helpers;

/// <summary>
/// Centralized display names for theme actions shared by menu and command palette.
/// </summary>
internal static class ThemeCommands
{
    public const string ImportTheme = "Import Theme...";
    public const string ExportCurrentTheme = "Export Current Theme...";
    public const string ThemeEditor = "Theme Editor...";

    /// <summary>
    /// Stable command ordering for theme utility actions.
    /// </summary>
    public static IReadOnlyList<string> UtilityCommandOrder { get; } = [
        ImportTheme,
        ExportCurrentTheme,
        ThemeEditor
    ];
}
