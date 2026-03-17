using Avalonia.Media;
using Leviathan.Core;
using Leviathan.Core.Csv;
using Leviathan.Core.Indexing;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.GUI.Helpers;

namespace Leviathan.GUI;

/// <summary>
/// Mutable application state shared across all GUI components.
/// Ported from TUI2 AppState — same Document lifecycle and view state management.
/// </summary>
public sealed class AppState
{
    private static readonly HashSet<string> CsvExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".tab"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".rst",
        ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config",
        ".cs", ".csx", ".csproj", ".sln", ".slnx", ".props", ".targets",
        ".xaml", ".axaml",
        ".js", ".jsx", ".ts", ".tsx", ".css", ".scss", ".sass", ".less", ".html", ".htm",
        ".sql", ".ps1", ".psm1", ".psd1", ".cmd", ".bat", ".sh",
        ".py", ".go", ".java", ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".rs"
    };

    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "LICENSE", "CHANGELOG", "Makefile", "Dockerfile",
        ".gitignore", ".gitattributes", ".editorconfig"
    };

    // --- Document ---
    public Document? Document { get; set; }
    public string? CurrentFilePath { get; set; }

    // --- View mode ---
    public ViewMode ActiveView { get; set; } = ViewMode.Hex;
    public bool IsReadOnly { get; set; }

    // --- Display options ---
    /// <summary>Whether the row-number / offset gutter is visible.</summary>
    public bool GutterVisible { get; set; } = true;
    /// <summary>When true, hex view shows file offsets in decimal instead of hex.</summary>
    public bool HexOffsetDecimal { get; set; }

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

    // --- Line index ---
    /// <summary>Background line indexer for fast offset/line mapping.</summary>
    public LineIndexer? Indexer { get; set; }
    /// <summary>Sparse line index built by the background indexer.</summary>
    public LineIndex? LineIndex => Indexer?.Index;

    // --- Scroll ---
    public int VisibleRows { get; set; } = 24;

    // --- Find state ---
    public string FindInput { get; set; } = "";
    public bool FindHexMode { get; set; }
    public bool FindCaseSensitive { get; set; }
    public bool FindRegexMode { get; set; }
    public bool FindWholeWord { get; set; }
    public List<SearchResult> SearchResults { get; set; } = [];
    public int CurrentMatchIndex { get; set; } = -1;
    public string SearchStatus { get; set; } = "";
    public CancellationTokenSource? SearchCts { get; set; }
    public Task? SearchTask { get; set; }
    public bool IsSearching { get; set; }

    // --- Goto preview state ---
    /// <summary>Cursor offset before goto preview started (-1 = no preview active).</summary>
    public long GotoPreviewOrigin { get; set; } = -1;
    /// <summary>Top offset before goto preview started.</summary>
    public long GotoPreviewTopOrigin { get; set; }

    // --- CSV view state ---
    /// <summary>Active CSV dialect (separator, quote, escape, header).</summary>
    public CsvDialect CsvDialect { get; set; } = CsvDialect.Csv();
    /// <summary>Background CSV row indexer.</summary>
    public CsvRowIndexer? CsvRowIndexer { get; set; }
    /// <summary>Sparse CSV row index built by the background indexer.</summary>
    public CsvRowIndex? CsvRowIndex => CsvRowIndexer?.Index;
    /// <summary>First visible data row index (0-based).</summary>
    public long CsvTopRowIndex { get; set; }
    /// <summary>Current cursor row (0-based data row, excluding header).</summary>
    public long CsvCursorRow { get; set; }
    /// <summary>Current cursor column (0-based).</summary>
    public int CsvCursorCol { get; set; }
    /// <summary>Selection anchor row for multi-row selection (-1 = no selection).</summary>
    public long CsvSelectionAnchorRow { get; set; } = -1;
    /// <summary>Computed column widths for the grid display.</summary>
    public int[]? CsvColumnWidths { get; set; }
    /// <summary>Horizontal scroll offset (number of columns scrolled).</summary>
    public int CsvHorizontalScroll { get; set; }
    /// <summary>Header field names (empty if no header).</summary>
    public string[] CsvHeaderNames { get; set; } = [];
    /// <summary>Number of columns detected.</summary>
    public int CsvColumnCount { get; set; }
    /// <summary>Whether the CSV detail side panel is visible (session-only, not persisted).</summary>
    public bool CsvDetailPanelVisible { get; set; }
    public HashSet<int> CsvHiddenColumns { get; set; } = [];

    // --- Settings ---
    public GuiSettings Settings { get; set; } = GuiSettings.Load();

    // --- Theme & Font ---
    /// <summary>Active color theme used by all custom view controls.</summary>
    internal ColorTheme ActiveTheme { get; set; } = ColorTheme.Dark;

    /// <summary>Typeface used by all custom view controls.</summary>
    public Typeface ContentTypeface { get; set; } = new("Consolas, Courier New, monospace");

    /// <summary>Font size (device-independent pixels) used by all custom view controls.</summary>
    public double ContentFontSize { get; set; } = 14;

    /// <summary>Line padding added below each text line in view controls.</summary>
    public const double LinePadding = 2;

    /// <summary>User themes loaded from the themes/ directory.</summary>
    internal List<ColorTheme> UserThemes { get; set; } = [];

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
    /// Computes auto-fit bytes per row for a given pixel width and character width.
    /// </summary>
    public int ComputeBytesPerRow(double availableWidth, double charWidth)
    {
        if (BytesPerRowSetting > 0)
            return BytesPerRowSetting;

        // Layout: [Address 10ch] [space] [Hex: 3ch per byte] [space per group of 8] [separator 3ch] [ASCII: 1ch per byte]
        double overhead = (10 + 1 + 3) * charWidth; // address + gaps + separator
        double perByte = (3 + 1) * charWidth;       // hex pair + space + ascii char

        double available = availableWidth - overhead;
        if (available < perByte * 8 + charWidth)
            return 8;

        double effectivePerByte = perByte + charWidth / 8.0; // group separator every 8 bytes
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
        Indexer?.Dispose();
        Indexer = null;
        CsvRowIndexer?.Dispose();
        CsvRowIndexer = null;
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

        // Start background line indexing
        if (Document.FileSource is { } source)
        {
            Indexer = new LineIndexer(source, Decoder.MinCharBytes);
            Indexer.StartScan();
        }

        SearchResults = [];
        CurrentMatchIndex = -1;
        SearchStatus = "";

        // Reset CSV state
        CsvTopRowIndex = 0;
        CsvCursorRow = 0;
        CsvCursorCol = 0;
        CsvSelectionAnchorRow = -1;
        CsvHorizontalScroll = 0;
        CsvHeaderNames = [];
        CsvColumnWidths = null;
        CsvColumnCount = 0;

        Settings.AddRecent(path);

        ActiveView = DetermineDefaultView(path, sample, encoding);
        if (ActiveView == ViewMode.Csv)
        {
            InitCsvView();
        }
    }

    private static ViewMode DetermineDefaultView(string path, ReadOnlySpan<byte> sample, TextEncoding encoding)
    {
        string ext = Path.GetExtension(path);
        if (CsvExtensions.Contains(ext))
            return ViewMode.Csv;

        if (TextExtensions.Contains(ext) || TextFileNames.Contains(Path.GetFileName(path)))
            return ViewMode.Text;

        return LooksLikeTextContent(sample, encoding) ? ViewMode.Text : ViewMode.Hex;
    }

    private static bool LooksLikeTextContent(ReadOnlySpan<byte> sample, TextEncoding encoding)
    {
        if (sample.IsEmpty)
            return true;

        if (encoding == TextEncoding.Utf16Le)
            return true;

        int controlCount = 0;
        for (int i = 0; i < sample.Length; i++)
        {
            byte b = sample[i];
            if (b == 0)
                return false;

            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\r' and not (byte)'\n' and not (byte)'\f' and not (byte)'\b')
                controlCount++;
        }

        int maxControlChars = Math.Max(1, sample.Length / 100);
        return controlCount <= maxControlChars;
    }

    /// <summary>
    /// Closes the current document and resets all state back to the welcome screen.
    /// </summary>
    public void CloseFile()
    {
        CancelSearch();
        Indexer?.Dispose();
        Indexer = null;
        CsvRowIndexer?.Dispose();
        CsvRowIndexer = null;
        Document?.Dispose();
        Document = null;
        CurrentFilePath = null;

        ActiveView = ViewMode.Hex;
        HexBaseOffset = 0;
        HexCursorOffset = -1;
        HexSelectionAnchor = -1;
        NibbleLow = false;
        TextTopOffset = 0;
        TextCursorOffset = -1;
        TextSelectionAnchor = -1;
        EstimatedTotalLines = 0;
        BomLength = 0;
        SearchResults = [];
        CurrentMatchIndex = -1;
        SearchStatus = "";
        CsvTopRowIndex = 0;
        CsvCursorRow = 0;
        CsvCursorCol = 0;
        CsvSelectionAnchorRow = -1;
        CsvHorizontalScroll = 0;
        CsvHeaderNames = [];
        CsvColumnWidths = null;
        CsvColumnCount = 0;
    }

    /// <summary>
    /// Attempts to save the document. Returns true on success, sets error message on failure.
    /// </summary>
    public bool TrySave(string path, out string? errorMessage)
    {
        errorMessage = null;
        if (Document is null) return false;

        Indexer?.Dispose();
        Indexer = null;

        try
        {
            Document.SaveTo(path);
            CurrentFilePath = path;
            Settings.AddRecent(path);

            if (Document.FileSource is { } source)
            {
                Indexer = new LineIndexer(source, Decoder.MinCharBytes);
                Indexer.StartScan();
            }

            return true;
        }
        catch (Exception ex)
        {
            if (Indexer is null && Document.FileSource is { } src)
            {
                Indexer = new LineIndexer(src, Decoder.MinCharBytes);
                Indexer.StartScan();
            }
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Switches encoding decoder at runtime and restarts the line indexer.
    /// </summary>
    public void SwitchEncoding(TextEncoding encoding)
    {
        Decoder = CreateDecoder(encoding);

        Indexer?.Dispose();
        Indexer = null;
        if (Document?.FileSource is { } source)
        {
            Indexer = new LineIndexer(source, Decoder.MinCharBytes);
            Indexer.StartScan();
        }
    }

    /// <summary>
    /// Cancels any in-progress search, waiting for the background task to finish.
    /// </summary>
    public void CancelSearch()
    {
        CancellationTokenSource? searchCts = SearchCts;
        SearchCts = null;
        searchCts?.Cancel();
        try { SearchTask?.Wait(); } catch (AggregateException) { /* expected on cancellation */ }
        SearchTask = null;
        searchCts?.Dispose();
        IsSearching = false;
    }

    /// <summary>
    /// Invalidates search results after a document edit. Clears all match data
    /// and notifies the UI so the find bar shows the stale-results state.
    /// </summary>
    public void InvalidateSearchResults()
    {
        if (SearchResults.Count == 0 && CurrentMatchIndex < 0) return;
        CancelSearch();
        SearchResults = [];
        CurrentMatchIndex = -1;
        SearchStatus = SearchStatus.Length > 0 ? "Results invalidated — search again" : "";
        SearchInvalidated?.Invoke();
    }

    /// <summary>Callback invoked when search results are invalidated by an edit.</summary>
    public Action? SearchInvalidated { get; set; }

    /// <summary>
    /// Initialises CSV view state for the currently open file.
    /// </summary>
    public void InitCsvView()
    {
        if (Document is null) return;

        CsvRowIndexer?.Dispose();
        CsvRowIndexer = null;

        CsvFileSettings? saved = CurrentFilePath is not null
            ? Settings.GetCsvFileSettings(CurrentFilePath)
            : null;

        if (saved is not null)
        {
            CsvDialect = new CsvDialect(saved.Separator, saved.Quote, saved.Escape, saved.HasHeader);
        }
        else
        {
            int sampleSize = (int)Math.Min(32768, Document.Length);
            byte[] sampleBuf = new byte[sampleSize];
            Document.Read(0, sampleBuf);
            ReadOnlySpan<byte> sample = sampleBuf.AsSpan(0, sampleSize);
            CsvDialect detected = CsvDialectDetector.Detect(sample);
            bool hasHeader = CsvHeaderDetector.Detect(sample, detected);
            CsvDialect = detected with { HasHeader = hasHeader };
        }

        CsvTopRowIndex = 0;
        CsvCursorRow = 0;
        CsvCursorCol = 0;
        CsvSelectionAnchorRow = -1;
        CsvHorizontalScroll = 0;

        if (Document.FileSource is { } source)
        {
            CsvRowIndexer = new CsvRowIndexer(source, CsvDialect);
            CsvRowIndexer.StartScan();
            CsvColumnCount = CsvRowIndexer.Index.ColumnCount;
        }

        ParseCsvHeaders();
        ComputeCsvColumnWidths();

        if (CurrentFilePath is not null && saved is null)
        {
            Settings.SetCsvFileSettings(CurrentFilePath, new CsvFileSettings
            {
                Separator = CsvDialect.Separator,
                Quote = CsvDialect.Quote,
                Escape = CsvDialect.Escape,
                HasHeader = CsvDialect.HasHeader
            });
        }
    }

    /// <summary>
    /// Parses the header row names from the document.
    /// </summary>
    private void ParseCsvHeaders()
    {
        if (Document is null || !CsvDialect.HasHeader)
        {
            CsvHeaderNames = [];
            return;
        }

        int readSize = (int)Math.Min(8192, Document.Length);
        byte[] buf = new byte[readSize];
        Document.Read(0, buf);

        int pos = 0;
        bool inQuoted = false;
        byte quote = CsvDialect.Quote;

        while (pos < readSize)
        {
            byte b = buf[pos];
            if (inQuoted)
            {
                if (b == quote)
                {
                    if (pos + 1 < readSize && buf[pos + 1] == quote)
                    {
                        pos += 2;
                        continue;
                    }
                    inQuoted = false;
                }
                pos++;
                continue;
            }
            if (b == quote && quote != 0) { inQuoted = true; pos++; continue; }
            if (b == (byte)'\n' || b == (byte)'\r') break;
            pos++;
        }

        ReadOnlySpan<byte> headerRow = buf.AsSpan(0, pos);
        Span<CsvField> fields = stackalloc CsvField[256];
        int fieldCount = CsvFieldParser.ParseRecord(headerRow, CsvDialect, fields);

        CsvHeaderNames = new string[fieldCount];
        Span<byte> unescaped = stackalloc byte[1024];
        for (int i = 0; i < fieldCount; i++)
        {
            int written = CsvFieldParser.UnescapeField(headerRow, fields[i], CsvDialect, unescaped);
            CsvHeaderNames[i] = System.Text.Encoding.UTF8.GetString(unescaped[..written]);
        }
    }

    /// <summary>
    /// Computes column widths from sampling the first ~100 rows.
    /// </summary>
    private void ComputeCsvColumnWidths()
    {
        if (Document is null || CsvColumnCount == 0)
        {
            CsvColumnWidths = [];
            return;
        }

        int colCount = CsvColumnCount;
        int[] widths = new int[colCount];

        for (int i = 0; i < CsvHeaderNames.Length && i < colCount; i++)
            widths[i] = Math.Min(CsvHeaderNames[i].Length, 40);

        int sampleSize = (int)Math.Min(65536, Document.Length);
        byte[] buf = new byte[sampleSize];
        Document.Read(0, buf);

        int pos = 0;
        bool inQuoted = false;
        byte quote = CsvDialect.Quote;

        if (CsvDialect.HasHeader)
        {
            while (pos < sampleSize)
            {
                byte b = buf[pos];
                if (inQuoted)
                {
                    if (b == quote)
                    {
                        if (pos + 1 < sampleSize && buf[pos + 1] == quote) { pos += 2; continue; }
                        inQuoted = false;
                    }
                    pos++;
                    continue;
                }
                if (b == quote && quote != 0) { inQuoted = true; pos++; continue; }
                if (b == (byte)'\n') { pos++; break; }
                if (b == (byte)'\r') { pos++; if (pos < sampleSize && buf[pos] == (byte)'\n') pos++; break; }
                pos++;
            }
            inQuoted = false;
        }

        Span<CsvField> fields = stackalloc CsvField[256];
        for (int row = 0; row < 100 && pos < sampleSize; row++)
        {
            int rowStart = pos;

            while (pos < sampleSize)
            {
                byte b = buf[pos];
                if (inQuoted)
                {
                    if (b == quote)
                    {
                        if (pos + 1 < sampleSize && buf[pos + 1] == quote) { pos += 2; continue; }
                        inQuoted = false;
                    }
                    pos++;
                    continue;
                }
                if (b == quote && quote != 0) { inQuoted = true; pos++; continue; }
                if (b == (byte)'\n') { pos++; break; }
                if (b == (byte)'\r') { pos++; if (pos < sampleSize && buf[pos] == (byte)'\n') pos++; break; }
                pos++;
            }

            int rowEnd = pos;
            int rowLen = rowEnd - rowStart;
            while (rowLen > 0 && (buf[rowStart + rowLen - 1] == (byte)'\n' || buf[rowStart + rowLen - 1] == (byte)'\r'))
                rowLen--;

            ReadOnlySpan<byte> rowBytes = buf.AsSpan(rowStart, rowLen);
            int fieldCount = CsvFieldParser.ParseRecord(rowBytes, CsvDialect, fields);

            for (int c = 0; c < fieldCount && c < colCount; c++)
            {
                int displayWidth = ComputePreviewDisplayWidth(rowBytes, fields[c], CsvDialect, 40);
                widths[c] = Math.Max(widths[c], displayWidth);
            }
            inQuoted = false;
        }

        for (int i = 0; i < colCount; i++)
            widths[i] = Math.Max(widths[i], 4);

        CsvColumnWidths = widths;
    }

    private static int ComputePreviewDisplayWidth(ReadOnlySpan<byte> record, CsvField field, CsvDialect dialect, int maxWidth)
    {
        if (field.Length <= 0 || maxWidth <= 0)
            return 0;

        Span<byte> unescaped = field.Length <= 1024
            ? stackalloc byte[1024]
            : new byte[field.Length];
        int written = CsvFieldParser.UnescapeField(record, field, dialect, unescaped);
        ReadOnlySpan<byte> data = unescaped[..written];
        int width = 0;
        int offset = 0;
        while (offset < data.Length && width < maxWidth)
        {
            System.Buffers.OperationStatus status =
                System.Text.Rune.DecodeFromUtf8(data[offset..], out System.Text.Rune rune, out int consumed);
            if (status != System.Buffers.OperationStatus.Done || consumed <= 0)
            {
                offset++;
                width++;
                continue;
            }

            if (rune.Value == '\r')
            {
                offset += consumed;
                if (offset < data.Length && data[offset] == (byte)'\n')
                    offset++;
                width++;
                continue;
            }

            offset += consumed;
            if (rune.Value == '\n')
            {
                width++;
                continue;
            }

            width++;
        }

        return width;
    }

    private static ITextDecoder CreateDecoder(TextEncoding encoding) => encoding switch
    {
        TextEncoding.Utf8 => new Utf8TextDecoder(),
        TextEncoding.Utf16Le => new Utf16LeTextDecoder(),
        TextEncoding.Windows1252 => new Windows1252TextDecoder(),
        _ => new Utf8TextDecoder()
    };
}

public enum ViewMode
{
    Hex,
    Text,
    Csv
}
