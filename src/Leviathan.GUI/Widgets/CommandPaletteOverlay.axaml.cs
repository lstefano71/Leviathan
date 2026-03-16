using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// VS Code-style command palette overlay (Ctrl+P).
/// Supports fuzzy command search and ":" goto mode.
/// </summary>
public sealed partial class CommandPaletteOverlay : UserControl
{
    private readonly AppState _state;
    private readonly List<CommandEntry> _allCommands = [];
    private readonly List<CommandEntry> _filteredCommands = [];
    private readonly Action<long> _gotoOffset;
    private readonly Action<long, int?> _gotoTextPosition;
    private readonly Action<long> _gotoTextOffset;
    private readonly Action<long> _gotoRow;
    private readonly Action? _restoreFocus;

    public CommandPaletteOverlay(
        AppState state,
        Action<long> gotoOffset,
        Action<long, int?> gotoTextPosition,
        Action<long> gotoTextOffset,
        Action<long> gotoRow,
        Action? restoreFocus = null)
    {
        _state = state;
        _gotoOffset = gotoOffset;
        _gotoTextPosition = gotoTextPosition;
        _gotoTextOffset = gotoTextOffset;
        _gotoRow = gotoRow;
        _restoreFocus = restoreFocus;

        InitializeComponent();

        PaletteInput.KeyDown += OnInputKeyDown;
        PaletteInput.TextChanged += OnInputChanged;
        CommandList.DoubleTapped += OnCommandSelected;
    }

    public CommandPaletteOverlay() : this(new AppState(), _ => { }, (_, _) => { }, _ => { }, _ => { }, () => { }) { }

    /// <summary>Clears all registered commands.</summary>
    public void ClearCommands()
    {
        _allCommands.Clear();
        _filteredCommands.Clear();
        CommandList.ItemsSource = null;
        CommandList.SelectedIndex = -1;
    }

    /// <summary>Registers a command in the palette.</summary>
    public void RegisterCommand(
        Func<string> displayName,
        string description,
        Action execute,
        string? searchName = null,
        bool closeOnExecute = true,
        bool restoreFocusAfterExecute = false)
    {
        _allCommands.Add(new CommandEntry(
            displayName,
            searchName ?? displayName(),
            description,
            execute,
            closeOnExecute,
            restoreFocusAfterExecute));
    }

    /// <summary>Registers a command in the palette (convenience overload).</summary>
    public void RegisterCommand(
        string name,
        string description,
        Action execute,
        bool closeOnExecute = true,
        bool restoreFocusAfterExecute = false) =>
        RegisterCommand(() => name, description, execute, name, closeOnExecute, restoreFocusAfterExecute);

    /// <summary>Shows the palette and focuses the input.</summary>
    public void Show()
    {
        IsVisible = true;
        ClearGotoPreviewState();
        PaletteInput.Text = "";
        FilterCommands("");
        Dispatcher.UIThread.Post(() =>
        {
            PaletteInput.Focus();
        }, DispatcherPriority.Input);
    }

    /// <summary>Shows the palette in goto mode.</summary>
    public void ShowGoto()
    {
        IsVisible = true;
        SaveGotoPreviewOrigin();
        PaletteInput.Text = ":";
        UpdateGotoHint(":");
        Dispatcher.UIThread.Post(() =>
        {
            PaletteInput.Focus();
            PaletteInput.CaretIndex = 1;
        }, DispatcherPriority.Input);
    }

    /// <summary>Hides the palette.</summary>
    public void Hide(bool restoreFocus = true)
    {
        IsVisible = false;
        if (restoreFocus && _restoreFocus is not null)
        {
            Dispatcher.UIThread.Post(_restoreFocus, DispatcherPriority.Input);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && (e.Key == Key.P || e.Key == Key.G))
        {
            if (IsGotoMode)
                CancelGotoAndHide();
            else
                Hide();

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                ExecuteSelectionOrConfirmGoto();
                e.Handled = true;
                break;
            case Key.Escape:
                if (IsGotoMode)
                    CancelGotoAndHide();
                else
                    Hide();
                e.Handled = true;
                break;
            case Key.Down:
                if (!IsGotoMode && CommandList.ItemCount > 0)
                    CommandList.SelectedIndex = Math.Min(CommandList.SelectedIndex + 1, CommandList.ItemCount - 1);
                e.Handled = true;
                break;
            case Key.Up:
                if (!IsGotoMode && CommandList.ItemCount > 0)
                    CommandList.SelectedIndex = Math.Max(CommandList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.PageDown:
                if (!IsGotoMode && CommandList.ItemCount > 0)
                {
                    CommandList.SelectedIndex = Math.Min(CommandList.SelectedIndex + 10, CommandList.ItemCount - 1);
                    CommandList.ScrollIntoView(CommandList.SelectedIndex);
                }
                e.Handled = true;
                break;
            case Key.PageUp:
                if (!IsGotoMode && CommandList.ItemCount > 0)
                {
                    CommandList.SelectedIndex = Math.Max(CommandList.SelectedIndex - 10, 0);
                    CommandList.ScrollIntoView(CommandList.SelectedIndex);
                }
                e.Handled = true;
                break;
        }
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        string input = PaletteInput.Text ?? "";

        if (input.StartsWith(':'))
        {
            UpdateGotoHint(input);
        }
        else
        {
            FilterCommands(input);
        }
    }

    private bool IsGotoMode => (PaletteInput.Text ?? string.Empty).StartsWith(':');

    private void UpdateGotoHint(string input)
    {
        PreviewGoto(input);
        CommandList.ItemsSource = new[] { BuildGotoHint(input) };
        CommandList.SelectedIndex = -1;
    }

    private string BuildGotoHint(string rawInput)
    {
        string target = rawInput.Length > 1 ? rawInput[1..].Trim() : string.Empty;
        if (string.IsNullOrEmpty(target))
        {
            return _state.ActiveView switch
            {
                ViewMode.Hex => "Type :0xOFFSET or :OFFSET then press Enter",
                ViewMode.Csv => "Type :row then press Enter",
                _ => "Type :line or :line:column then press Enter"
            };
        }

        return _state.ActiveView switch
        {
            ViewMode.Hex => TryParseOffset(target, out long offset)
                ? $"Press Enter to jump to offset 0x{offset:X}"
                : "Enter a valid byte offset like :0x1A3F or :6703",
            ViewMode.Csv => long.TryParse(target, out long row) && row > 0
                ? $"Press Enter to jump to CSV row {row}"
                : "Enter a valid CSV row number",
            _ => BuildTextGotoHint(target)
        };
    }

    private static string BuildTextGotoHint(string input)
    {
        if (!TryParseTextTarget(input, out long lineNumber, out int? columnNumber, out bool awaitingColumn))
            return "Enter a valid line number or :line:column";

        if (columnNumber is int column)
            return $"Press Enter to jump to line {lineNumber}, column {column}";

        if (awaitingColumn)
            return $"Now type a column after :{lineNumber}:";

        return $"Press Enter to jump to line {lineNumber} (add :column for a text column)";
    }

    private void FilterCommands(string query)
    {
        _filteredCommands.Clear();
        if (string.IsNullOrEmpty(query))
        {
            _filteredCommands.AddRange(_allCommands);
        }
        else
        {
            string lowerQuery = query.ToLowerInvariant();
            _filteredCommands.AddRange(_allCommands.Where(c =>
                c.SearchName.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)));
        }

        CommandList.ItemsSource = _filteredCommands.Select(FormatCommandDisplay).ToArray();
        CommandList.SelectedIndex = _filteredCommands.Count > 0 ? 0 : -1;
    }

    private static string FormatCommandDisplay(CommandEntry entry) =>
        $"{entry.DisplayName()} - {entry.Description}";

    private void ExecuteSelectionOrConfirmGoto()
    {
        string input = PaletteInput.Text ?? "";

        if (input.StartsWith(':'))
        {
            if (TryExecuteGoto(input[1..].Trim()))
                ConfirmGotoAndHide();

            return;
        }

        if (CommandList.SelectedIndex < 0 || CommandList.SelectedIndex >= _filteredCommands.Count)
            return;

        CommandEntry entry = _filteredCommands[CommandList.SelectedIndex];
        if (entry.CloseOnExecute)
            Hide(restoreFocus: false);

        entry.Execute();

        if (entry.CloseOnExecute && entry.RestoreFocusAfterExecute && _restoreFocus is not null)
        {
            Dispatcher.UIThread.Post(_restoreFocus, DispatcherPriority.Input);
        }
    }

    private bool TryExecuteGoto(string target)
    {
        switch (_state.ActiveView)
        {
            case ViewMode.Hex:
                if (!TryParseOffset(target, out long offset))
                    return false;

                _gotoOffset(offset);
                return true;

            case ViewMode.Csv:
                if (!long.TryParse(target, out long rowNumber) || rowNumber < 1)
                    return false;

                _gotoRow(rowNumber);
                return true;

            default:
                if (!TryParseTextTarget(target, out long lineNumber, out int? columnNumber, out bool awaitingColumn) || awaitingColumn)
                    return false;

                _gotoTextPosition(lineNumber, columnNumber);
                return true;
        }
    }

    private void PreviewGoto(string rawInput)
    {
        if (!rawInput.StartsWith(':'))
            return;

        string target = rawInput.Length > 1 ? rawInput[1..].Trim() : string.Empty;
        if (string.IsNullOrEmpty(target))
            return;

        switch (_state.ActiveView)
        {
            case ViewMode.Hex:
                if (TryParseOffset(target, out long offset))
                    _gotoOffset(offset);
                break;

            case ViewMode.Csv:
                if (long.TryParse(target, out long rowNumber) && rowNumber > 0)
                    _gotoRow(rowNumber);
                break;

            default:
                if (TryParseTextTarget(target, out long lineNumber, out int? columnNumber, out _))
                    _gotoTextPosition(lineNumber, columnNumber);
                break;
        }
    }

    private void SaveGotoPreviewOrigin()
    {
        switch (_state.ActiveView)
        {
            case ViewMode.Hex:
                _state.GotoPreviewOrigin = _state.HexCursorOffset;
                _state.GotoPreviewTopOrigin = _state.HexBaseOffset;
                break;

            case ViewMode.Csv:
                _state.GotoPreviewOrigin = _state.CsvCursorRow;
                _state.GotoPreviewTopOrigin = _state.CsvTopRowIndex;
                break;

            default:
                _state.GotoPreviewOrigin = _state.TextCursorOffset;
                _state.GotoPreviewTopOrigin = _state.TextTopOffset;
                break;
        }
    }

    private void ConfirmGotoAndHide()
    {
        ClearGotoPreviewState();
        Hide();
    }

    private void CancelGotoAndHide()
    {
        if (_state.GotoPreviewOrigin >= 0)
        {
            switch (_state.ActiveView)
            {
                case ViewMode.Hex:
                    _gotoOffset(_state.GotoPreviewOrigin);
                    break;

                case ViewMode.Csv:
                    _gotoRow(_state.GotoPreviewOrigin + 1);
                    break;

                default:
                    _gotoTextOffset(_state.GotoPreviewOrigin);
                    _state.TextTopOffset = _state.GotoPreviewTopOrigin;
                    break;
            }

            if (_state.ActiveView == ViewMode.Hex)
                _state.HexBaseOffset = _state.GotoPreviewTopOrigin;
            else if (_state.ActiveView == ViewMode.Csv)
                _state.CsvTopRowIndex = _state.GotoPreviewTopOrigin;
        }

        ClearGotoPreviewState();
        Hide();
    }

    private void ClearGotoPreviewState()
    {
        _state.GotoPreviewOrigin = -1;
        _state.GotoPreviewTopOrigin = 0;
    }

    private static bool TryParseTextTarget(string input, out long lineNumber, out int? columnNumber, out bool awaitingColumn)
    {
        lineNumber = 0;
        columnNumber = null;
        awaitingColumn = false;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        string[] parts = input.Split(':');
        if (parts.Length == 0 || parts.Length > 2)
            return false;

        if (!long.TryParse(parts[0], out lineNumber) || lineNumber < 1)
            return false;

        if (parts.Length == 1)
            return true;

        if (string.IsNullOrWhiteSpace(parts[1]))
        {
            awaitingColumn = true;
            return true;
        }

        if (!int.TryParse(parts[1], out int parsedColumn) || parsedColumn < 1)
            return false;

        columnNumber = parsedColumn;
        return true;
    }

    private static bool TryParseOffset(string input, out long offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string trimmed = input.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out offset);
        }

        if (trimmed.Any(static c => (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
        {
            return long.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out offset);
        }

        return long.TryParse(trimmed, out offset);
    }

    private void OnCommandSelected(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        ExecuteSelectionOrConfirmGoto();
    }

    private sealed record CommandEntry(
        Func<string> DisplayName,
        string SearchName,
        string Description,
        Action Execute,
        bool CloseOnExecute,
        bool RestoreFocusAfterExecute);
}
