using Hexa.NET.ImGui;

using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.Core.Text;

using System.Numerics;
using System.Text;

namespace Leviathan.UI.Windows;

/// <summary>
/// Non-modal floating find bar. Shows above the status bar when open.
/// Ctrl+F opens, Escape closes, F3/Shift+F3 navigate.
/// </summary>
public sealed class FindWindow
{
    private bool _isOpen;
    private readonly byte[] _inputBuf = new byte[256];
    private bool _hexMode;       // false = text (encoding from decoder), true = hex byte pattern
    private bool _matchCase;
    private ITextDecoder? _decoder;
    private bool _focusInput;

    // Search results
    private readonly List<SearchResult> _results = new(1024);
    private int _currentIndex = -1;
    private string _statusText = string.Empty;

    // Background search state
    private Task? _searchTask;
    private CancellationTokenSource? _searchCts;
    private Document? _lastSearchDoc;
    private string _lastSearchQuery = string.Empty;
    // private bool _lastSearchHex;
    // private bool _lastSearchCase;

    // Error display
    private string? _parseError;

    public bool IsOpen => _isOpen;

    /// <summary>Sets the active text decoder for encoding-aware text search.</summary>
    public ITextDecoder? ActiveDecoder { get => _decoder; set => _decoder = value; }

    /// <summary>Opens the find bar and focuses the input field.</summary>
    public void Open()
    {
        _isOpen = true;
        _focusInput = true;
    }

    /// <summary>Closes the find bar and cancels any running search.</summary>
    public void Close()
    {
        _isOpen = false;
        CancelSearch();
    }

    /// <summary>
    /// Navigates to the next match relative to <paramref name="currentOffset"/>.
    /// </summary>
    public void FindNext(long currentOffset, HexView? hexView, TextView? textView, int activeView)
    {
        if (_results.Count == 0) return;
        int next = _results.FindIndex(r => r.Offset > currentOffset);
        if (next < 0) next = 0; // wrap around
        NavigateTo(next, hexView, textView, activeView);
    }

    /// <summary>
    /// Navigates to the previous match relative to <paramref name="currentOffset"/>.
    /// </summary>
    public void FindPrevious(long currentOffset, HexView? hexView, TextView? textView, int activeView)
    {
        if (_results.Count == 0) return;
        int prev = -1;
        for (int i = _results.Count - 1; i >= 0; i--) {
            if (_results[i].Offset < currentOffset) { prev = i; break; }
        }
        if (prev < 0) prev = _results.Count - 1; // wrap around
        NavigateTo(prev, hexView, textView, activeView);
    }

    /// <summary>
    /// Renders the find bar. Call each frame after all other windows.
    /// </summary>
    public unsafe void Render(
        Document? document,
        HexView? hexView,
        TextView? textView,
        int activeView,
        Settings settings)
    {
        if (!_isOpen) return;

        // Position at top-right, just below menu bar
        var viewport = ImGui.GetMainViewport();
        float menuBarH = ImGui.GetFrameHeight();
        ImGui.SetNextWindowPos(
            new Vector2(viewport.Size.X - 420, menuBarH + 4),
            ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(410, 0), ImGuiCond.Always);

        bool open = _isOpen;
        if (ImGui.Begin("Find##FindBar"u8, ref open,
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings)) {

            // ── Search input ──
            if (_focusInput) {
                ImGui.SetKeyboardFocusHere();
                _focusInput = false;
            }

            ImGui.SetNextItemWidth(200);
            bool enter;
            fixed (byte* pBuf = _inputBuf) {
                enter = ImGui.InputText("##findInput"u8, pBuf, (nuint)_inputBuf.Length,
                    ImGuiInputTextFlags.EnterReturnsTrue);
            }

            ImGui.SameLine();

            // ── Mode toggle ──
            if (_hexMode) {
                if (ImGui.Button("Hex"u8)) _hexMode = !_hexMode;
            } else {
                string encodingLabel = _decoder?.Encoding switch {
                    TextEncoding.Utf16Le => "Text (UTF-16)",
                    TextEncoding.Windows1252 => "Text (W1252)",
                    _ => "Text (UTF-8)"
                };
                if (ImGui.Button(encodingLabel)) _hexMode = !_hexMode;
            }

            ImGui.SameLine();

            // ── Match case ──
            ImGui.Checkbox("Aa"u8, ref _matchCase);

            // ── Error display ──
            if (_parseError is not null) {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _parseError);
            }

            // ── Buttons ──
            bool doSearch = enter || ImGui.Button("Find"u8);
            ImGui.SameLine();

            bool doNext = ImGui.Button("Next (F3)"u8);
            ImGui.SameLine();
            bool doPrev = ImGui.Button("Prev (Shift+F3)"u8);
            ImGui.SameLine();

            if (ImGui.Button("X"u8)) Close();

            // ── Status ──
            if (!string.IsNullOrEmpty(_statusText))
                ImGui.TextUnformatted(_statusText);

            // ── Execute search ──
            if (doSearch && document is not null) {
                string query = Encoding.UTF8.GetString(_inputBuf).TrimEnd('\0');
                if (!string.IsNullOrEmpty(query)) {
                    settings.AddFindHistory(query);
                    StartSearch(document, query);
                }
            }

            // ── Navigation ──
            long cursorOff = activeView == 0
                ? (hexView?.SelectedOffset ?? 0)
                : (textView?.CursorOffset ?? 0);

            if (doNext || (ImGui.IsKeyPressed(ImGuiKey.F3) && !ImGui.GetIO().KeyShift))
                FindNext(cursorOff, hexView, textView, activeView);

            if (doPrev || (ImGui.IsKeyPressed(ImGuiKey.F3) && ImGui.GetIO().KeyShift))
                FindPrevious(cursorOff, hexView, textView, activeView);

            // ── Poll background search completion ──
            if (_searchTask is { IsCompleted: true }) {
                _searchTask = null;
                int count = _results.Count;
                _statusText = count == 0 ? "No matches" : $"{count} match{(count == 1 ? "" : "es")} found";
            }
        }
        ImGui.End();

        if (!open) Close();
    }

    // ── Private helpers ──────────────────────────────────────────────

    private void StartSearch(Document doc, string query)
    {
        CancelSearch();
        _results.Clear();
        _currentIndex = -1;
        _parseError = null;
        _statusText = "Searching…";
        _lastSearchQuery = query;
        _lastSearchDoc = doc;

        byte[] pattern;
        try {
            pattern = BuildPattern(query);
        } catch (FormatException ex) {
            _parseError = ex.Message;
            _statusText = string.Empty;
            return;
        }

        _searchCts = new CancellationTokenSource();
        CancellationToken ct = _searchCts.Token;

        _searchTask = Task.Run(() => {
            try {
                foreach (var result in SearchEngine.FindAll(doc, pattern)) {
                    ct.ThrowIfCancellationRequested();
                    lock (_results) _results.Add(result);
                }
            } catch (OperationCanceledException) { }
        }, ct);
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchTask = null;
    }

    private byte[] BuildPattern(string query)
    {
        if (_hexMode)
            return SearchEngine.ParseHexPattern(query.AsSpan());

        if (!_matchCase)
            query = query.ToLowerInvariant();

        // Use active decoder encoding if set, otherwise default to UTF-8
        if (_decoder is not null)
            return _decoder.EncodeString(query);

        return Encoding.UTF8.GetBytes(query);
    }

    private void NavigateTo(int index, HexView? hexView, TextView? textView, int activeView)
    {
        if (index < 0 || index >= _results.Count) return;
        _currentIndex = index;
        var r = _results[index];
        _statusText = $"Match {index + 1} of {_results.Count}";

        if (activeView == 0)
            hexView?.JumpToMatch(r.Offset);
        else
            textView?.SetSelection(r.Offset, r.Offset + r.Length);
    }
}
