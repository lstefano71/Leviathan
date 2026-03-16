using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Leviathan.Core.Search;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Find bar overlay (Ctrl+F). Supports text, hex, regex, and whole-word search
/// with case sensitivity toggle and match navigation.
/// </summary>
public sealed partial class FindBar : UserControl
{
    private readonly AppState _state;
    private readonly Action _onSearchStarted;
    private readonly Action _onFindNext;
    private readonly Action _onFindPrev;
    private readonly Action? _onHide;
    private bool _suppressToggleReSearch;

    public FindBar(AppState state, Action onSearchStarted, Action onFindNext, Action onFindPrev, Action? onHide = null)
    {
        _state = state;
        _onSearchStarted = onSearchStarted;
        _onFindNext = onFindNext;
        _onFindPrev = onFindPrev;
        _onHide = onHide;

        InitializeComponent();

        SearchInput.KeyDown += OnSearchKeyDown;
        NextButton.Click += (_, _) => _onFindNext();
        PrevButton.Click += (_, _) => _onFindPrev();
        CloseButton.Click += (_, _) => Hide();

        CaseSensitiveToggle.IsCheckedChanged += (_, _) =>
        {
            _state.FindCaseSensitive = CaseSensitiveToggle.IsChecked == true;
            ReSearchOnToggle();
        };

        WholeWordToggle.IsCheckedChanged += (_, _) =>
        {
            _state.FindWholeWord = WholeWordToggle.IsChecked == true;
            ReSearchOnToggle();
        };

        RegexToggle.IsCheckedChanged += (_, _) =>
        {
            _state.FindRegexMode = RegexToggle.IsChecked == true;
            // Regex and Hex are mutually exclusive
            if (_state.FindRegexMode && _state.FindHexMode)
            {
                _suppressToggleReSearch = true;
                HexModeToggle.IsChecked = false;
                _state.FindHexMode = false;
                _suppressToggleReSearch = false;
            }
            ReSearchOnToggle();
        };

        HexModeToggle.IsCheckedChanged += (_, _) =>
        {
            _state.FindHexMode = HexModeToggle.IsChecked == true;
            // Hex and Regex are mutually exclusive
            if (_state.FindHexMode && _state.FindRegexMode)
            {
                _suppressToggleReSearch = true;
                RegexToggle.IsChecked = false;
                _state.FindRegexMode = false;
                _suppressToggleReSearch = false;
            }
            ReSearchOnToggle();
        };
    }

    public FindBar() : this(new AppState(), () => { }, () => { }, () => { }, () => { }) { }

    /// <summary>Shows the find bar and focuses the search input.</summary>
    public void ShowBar()
    {
        IsVisible = true;
        SearchInput.Text = _state.FindInput;
        CaseSensitiveToggle.IsChecked = _state.FindCaseSensitive;
        WholeWordToggle.IsChecked = _state.FindWholeWord;
        RegexToggle.IsChecked = _state.FindRegexMode;
        HexModeToggle.IsChecked = _state.FindHexMode;
        // Delay focus to after layout pass so control is visible
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SearchInput.Focus();
            SearchInput.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    /// <summary>Hides the find bar.</summary>
    public void Hide(bool restoreFocus = true)
    {
        IsVisible = false;
        if (restoreFocus && _onHide is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(_onHide, Avalonia.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>Updates the match status display.</summary>
    public void UpdateMatchStatus()
    {
        if (_state.IsSearching)
        {
            int count = _state.SearchResults.Count;
            MatchStatus.Text = count > 0 ? $"Searching... ({count})" : "Searching...";
        }
        else if (_state.SearchResults.Count > 0)
        {
            MatchStatus.Text = $"{_state.CurrentMatchIndex + 1}/{_state.SearchResults.Count}";
        }
        else if (!string.IsNullOrEmpty(_state.FindInput))
        {
            MatchStatus.Text = _state.SearchStatus.Length > 0 ? _state.SearchStatus : "No matches";
        }
        else
        {
            MatchStatus.Text = "";
        }
    }

    /// <summary>Re-triggers search when a toggle changes (if a query is active).</summary>
    private void ReSearchOnToggle()
    {
        if (_suppressToggleReSearch) return;
        if (!string.IsNullOrEmpty(_state.FindInput) && _state.SearchResults.Count > 0)
        {
            _state.FindInput = (SearchInput.Text ?? "").Trim();
            _onSearchStarted();
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                string query = (SearchInput.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(query) && query == _state.FindInput && _state.SearchResults.Count > 0)
                {
                    // Same query with existing results → navigate to next match
                    _onFindNext();
                }
                else
                {
                    // New or changed query → start fresh search
                    _state.FindInput = query;
                    _onSearchStarted();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.F3:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    _onFindPrev();
                else
                    _onFindNext();
                e.Handled = true;
                break;
        }
    }
}
