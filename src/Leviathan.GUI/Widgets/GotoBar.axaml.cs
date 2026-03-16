using Avalonia.Controls;
using Avalonia.Input;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Go-to overlay (Ctrl+G). Accepts hex offsets (0x prefix) or line numbers.
/// </summary>
public sealed partial class GotoBar : UserControl
{
    private readonly AppState _state;
    private readonly Action<long> _gotoOffset;
    private readonly Action<long> _gotoLine;

    public GotoBar(AppState state, Action<long> gotoOffset, Action<long> gotoLine)
    {
        _state = state;
        _gotoOffset = gotoOffset;
        _gotoLine = gotoLine;

        InitializeComponent();

        GotoInput.KeyDown += OnInputKeyDown;
        GoButton.Click += (_, _) => ExecuteGoto();
        CloseButton.Click += (_, _) => Hide();
    }

    public GotoBar() : this(new AppState(), _ => { }, _ => { }) { }

    /// <summary>Shows the goto bar and focuses the input.</summary>
    public void ShowBar()
    {
        IsVisible = true;
        GotoInput.Text = "";
        GotoInput.Focus();
    }

    /// <summary>Hides the goto bar.</summary>
    public void Hide()
    {
        IsVisible = false;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ExecuteGoto();
                e.Handled = true;
                break;
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
        }
    }

    private void ExecuteGoto()
    {
        string input = (GotoInput.Text ?? "").Trim();
        if (string.IsNullOrEmpty(input)) return;

        // Hex offset: starts with 0x or contains hex chars
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(input[2..], System.Globalization.NumberStyles.HexNumber, null, out long offset))
            {
                _gotoOffset(offset);
                Hide();
            }
        }
        else if (long.TryParse(input, out long lineNumber) && lineNumber > 0)
        {
            // Line number in text mode, offset in hex mode
            if (_state.ActiveView == ViewMode.Text)
                _gotoLine(lineNumber);
            else
                _gotoOffset(lineNumber);
            Hide();
        }
    }
}
