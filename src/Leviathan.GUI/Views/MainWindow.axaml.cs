using Avalonia.Controls;

namespace Leviathan.GUI.Views;

/// <summary>
/// Main application window. Hosts menu bar, status bar, and the active view.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
