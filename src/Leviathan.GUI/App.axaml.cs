using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using Leviathan.GUI.Helpers;
using Leviathan.GUI.Views;

namespace Leviathan.GUI;

/// <summary>
/// Avalonia application entry point. Configures theme and main window.
/// </summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            MainWindow mainWindow = new();
            desktop.MainWindow = mainWindow;

            // Apply persisted theme variant on startup
            string themeName = mainWindow.GetThemeName();
            ColorTheme theme = ColorTheme.FindById(themeName);
            RequestedThemeVariant = theme.BaseVariant;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
