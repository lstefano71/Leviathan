using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.Core.Text;

namespace Leviathan.TUI2;

/// <summary>
/// Mutable application state shared across all TUI2 components.
/// </summary>
internal sealed class AppState
{
  // --- Document ---
  public Document? Document { get; set; }
  public string? CurrentFilePath { get; set; }

  // --- View mode ---
  public ViewMode ActiveView { get; set; } = ViewMode.Hex;

  // --- Encoding ---
  public ITextDecoder Decoder { get; set; } = new Utf8TextDecoder();

  // --- Hex view state ---
  public long HexBaseOffset { get; set; }
  public int BytesPerRow { get; set; } = 16;
  public int BytesPerRowSetting { get; set; } // 0 = auto
  public long HexCursorOffset { get; set; } = -1;
  public long HexSelectionAnchor { get; set; } = -1;
  public bool NibbleLow { get; set; }

  // --- Text view state ---
  public long TextTopOffset { get; set; }
  public long TextCursorOffset { get; set; } = -1;
  public long TextSelectionAnchor { get; set; } = -1;
  public bool WordWrap { get; set; } = true;
  public int TabWidth { get; set; } = 4;
  public long EstimatedTotalLines { get; set; }

  // --- BOM ---
  /// <summary>Length of the BOM prefix in the current file (0 when none).</summary>
  public int BomLength { get; set; }

  // --- Scroll ---
  public int VisibleRows { get; set; } = 24;

  // --- Find state ---
  public string FindInput { get; set; } = "";
  public bool FindHexMode { get; set; }
  public bool FindCaseSensitive { get; set; }
  public List<SearchResult> SearchResults { get; } = [];
  public int CurrentMatchIndex { get; set; } = -1;
  public string SearchStatus { get; set; } = "";
  public CancellationTokenSource? SearchCts { get; set; }
  public bool IsSearching { get; set; }

  // --- Goto preview state ---
  /// <summary>Cursor offset before goto preview started (-1 = no preview active).</summary>
  public long GotoPreviewOrigin { get; set; } = -1;
  /// <summary>Top offset before goto preview started.</summary>
  public long GotoPreviewTopOrigin { get; set; }

  // --- Settings ---
  public TuiSettings Settings { get; set; } = TuiSettings.Load();

  // --- Computed helpers ---
  public long FileLength => Document?.Length ?? 0;
  public bool IsModified => Document?.IsModified ?? false;

  public long HexSelStart => HexSelectionAnchor < 0 ? -1 : Math.Min(HexCursorOffset, HexSelectionAnchor);
  public long HexSelEnd => HexSelectionAnchor < 0 ? -1 : Math.Max(HexCursorOffset, HexSelectionAnchor);
  public long TextSelStart => TextSelectionAnchor < 0 ? -1 : Math.Min(TextCursorOffset, TextSelectionAnchor);
  public long TextSelEnd => TextSelectionAnchor < 0 ? -1 : Math.Max(TextCursorOffset, TextSelectionAnchor);

  /// <summary>
  /// Current cursor offset in whichever view is active.
  /// </summary>
  public long CurrentCursorOffset => ActiveView == ViewMode.Hex ? HexCursorOffset : TextCursorOffset;

  /// <summary>
  /// Computes auto-fit bytes per row for a given terminal width.
  /// </summary>
  public int ComputeBytesPerRow(int terminalWidth)
  {
    if (BytesPerRowSetting > 0)
      return BytesPerRowSetting;

    const int overhead = 20;
    const int perByte = 4;

    int available = terminalWidth - overhead;
    if (available < perByte * 8 + 1)
      return 8;

    double effectivePerByte = perByte + 1.0 / 8;
    int maxCols = (int)(available / effectivePerByte);
    int result = Math.Max(8, (maxCols / 8) * 8);
    return Math.Min(result, 64);
  }

  /// <summary>
  /// Opens a file, auto-detecting encoding.
  /// </summary>
  public void OpenFile(string path)
  {
    CancelSearch();
    Document?.Dispose();
    Document = new Document(path);
    CurrentFilePath = path;

    int sampleSize = (int)Math.Min(8192, Document.Length);
    Span<byte> sample = stackalloc byte[sampleSize];
    Document.Read(0, sample);
    (TextEncoding encoding, int bomLength) = EncodingDetector.Detect(sample);
    Decoder = CreateDecoder(encoding);
    BomLength = bomLength;

    HexBaseOffset = 0;
    HexCursorOffset = 0;
    HexSelectionAnchor = -1;
    NibbleLow = false;
    TextTopOffset = 0;
    TextCursorOffset = bomLength;
    TextSelectionAnchor = -1;
    EstimatedTotalLines = Math.Max(1, Document.Length / 80);
    SearchResults.Clear();
    CurrentMatchIndex = -1;
    SearchStatus = "";

    Settings.AddRecent(path);
  }

  /// <summary>
  /// Attempts to save the document. Returns true on success, sets error message on failure.
  /// </summary>
  public bool TrySave(string path, out string? errorMessage)
  {
    errorMessage = null;
    if (Document is null) return false;
    try {
      Document.SaveTo(path);
      CurrentFilePath = path;
      Settings.AddRecent(path);
      return true;
    } catch (Exception ex) {
      errorMessage = ex.Message;
      return false;
    }
  }

  /// <summary>
  /// Switches encoding decoder at runtime.
  /// </summary>
  public void SwitchEncoding(TextEncoding encoding)
  {
    Decoder = CreateDecoder(encoding);
  }

  /// <summary>
  /// Cancels any in-progress search.
  /// </summary>
  public void CancelSearch()
  {
    SearchCts?.Cancel();
    SearchCts?.Dispose();
    SearchCts = null;
    IsSearching = false;
  }

  private static ITextDecoder CreateDecoder(TextEncoding encoding) => encoding switch {
    TextEncoding.Utf8 => new Utf8TextDecoder(),
    TextEncoding.Utf16Le => new Utf16LeTextDecoder(),
    TextEncoding.Windows1252 => new Windows1252TextDecoder(),
    _ => new Utf8TextDecoder()
  };
}

internal enum ViewMode
{
  Hex,
  Text
}
