using Avalonia.Input;

using Leviathan.GUI.Views;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests keyboard shortcut routing helpers for MainWindow global handlers.
/// </summary>
public sealed class MainWindowShortcutRoutingTests
{
    [Fact]
    public void ShouldHandleGlobalCtrlShortcut_TextInputFocused_BlocksSensitiveShortcuts()
    {
        bool shouldHandle = MainWindow.ShouldHandleGlobalCtrlShortcut(Key.W, textInputFocused: true);

        Assert.False(shouldHandle);
    }

    [Fact]
    public void ShouldHandleGlobalCtrlShortcut_TextInputFocused_AllowsNonSensitiveShortcut()
    {
        bool shouldHandle = MainWindow.ShouldHandleGlobalCtrlShortcut(Key.G, textInputFocused: true);

        Assert.True(shouldHandle);
    }

    [Fact]
    public void ResolveLegacyEditShortcut_NotTextInput_ReturnsCopyForCtrlInsert()
    {
        MainWindow.LegacyEditShortcutAction action = MainWindow.ResolveLegacyEditShortcut(
            Key.Insert,
            KeyModifiers.Control,
            textInputFocused: false);

        Assert.Equal(MainWindow.LegacyEditShortcutAction.Copy, action);
    }

    [Fact]
    public void ResolveLegacyEditShortcut_NotTextInput_ReturnsPasteForShiftInsert()
    {
        MainWindow.LegacyEditShortcutAction action = MainWindow.ResolveLegacyEditShortcut(
            Key.Insert,
            KeyModifiers.Shift,
            textInputFocused: false);

        Assert.Equal(MainWindow.LegacyEditShortcutAction.Paste, action);
    }

    [Fact]
    public void ResolveLegacyEditShortcut_NotTextInput_ReturnsCutForShiftDelete()
    {
        MainWindow.LegacyEditShortcutAction action = MainWindow.ResolveLegacyEditShortcut(
            Key.Delete,
            KeyModifiers.Shift,
            textInputFocused: false);

        Assert.Equal(MainWindow.LegacyEditShortcutAction.Cut, action);
    }

    [Fact]
    public void ResolveLegacyEditShortcut_TextInputFocused_ReturnsNone()
    {
        MainWindow.LegacyEditShortcutAction action = MainWindow.ResolveLegacyEditShortcut(
            Key.Insert,
            KeyModifiers.Control,
            textInputFocused: true);

        Assert.Equal(MainWindow.LegacyEditShortcutAction.None, action);
    }
}
