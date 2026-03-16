using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Leviathan.Core.Csv;
using Leviathan.Core.Search;
using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Side panel showing field-by-field detail of the current CSV record.
/// Auto-updates when the cursor row changes. Toggled with F2.
/// </summary>
public sealed partial class CsvDetailPanel : UserControl
{
    private static readonly IBrush StripeBrush = new SolidColorBrush(Color.FromArgb(15, 128, 128, 128));
    private static readonly IBrush ActiveColBrush = new SolidColorBrush(Color.FromArgb(30, 100, 160, 255));
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 0));
    private static readonly IBrush ActiveMatchBrush = new SolidColorBrush(Color.FromArgb(140, 255, 165, 0));

    private long _lastRow = -2;
    private int _lastCol = -2;
    private int _fieldCount;
    private long _lastRowOffset;

    /// <summary>Fired when the user clicks the close (✕) button.</summary>
    internal Action? CloseRequested;

    public CsvDetailPanel()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
    }

    /// <summary>
    /// Updates the panel to reflect the current cursor position.
    /// Skips full rebuild if only the column changed (just updates highlight).
    /// </summary>
    internal void UpdateRecord(AppState state, Views.CsvViewControl csvView)
    {
        if (state.Document is null || state.CsvRowIndex is null)
        {
            ClearPanel();
            return;
        }

        long cursorRow = state.CsvCursorRow;
        int cursorCol = state.CsvCursorCol;

        if (cursorRow == _lastRow && cursorCol == _lastCol)
            return;

        if (cursorRow != _lastRow)
        {
            RebuildFields(state, csvView, cursorRow);
            _lastRow = cursorRow;
        }

        UpdateHighlight(cursorCol, state);
        _lastCol = cursorCol;
    }

    /// <summary>
    /// Clears the panel when no data is available.
    /// </summary>
    internal void ClearPanel()
    {
        _lastRow = -2;
        _lastCol = -2;
        _fieldCount = 0;
        _lastRowOffset = -1;
        TitleText.Text = "Record Detail";
        FieldsPanel.Children.Clear();
    }

    private void RebuildFields(AppState state, Views.CsvViewControl csvView, long cursorRow)
    {
        FieldsPanel.Children.Clear();
        _fieldCount = 0;

        long rowOffset = csvView.GetRowByteOffset(cursorRow);
        _lastRowOffset = rowOffset;
        if (rowOffset < 0 || rowOffset >= state.FileLength)
        {
            TitleText.Text = "Record Detail";
            return;
        }

        TitleText.Text = $"Record #{cursorRow + 1}";

        CsvDialect dialect = state.CsvDialect;
        string[] headers = state.CsvHeaderNames;
        HashSet<int> hiddenCols = state.CsvHiddenColumns;

        int readLen = (int)Math.Min(8192, state.FileLength - rowOffset);
        if (readLen <= 0) return;

        byte[] buf = new byte[readLen];
        state.Document!.Read(rowOffset, buf);

        // Find row end (respecting quoted fields)
        int rowLen = FindRowEnd(buf, readLen, dialect);
        ReadOnlySpan<byte> rowData = buf.AsSpan(0, rowLen);

        Span<CsvField> fields = stackalloc CsvField[256];
        int fieldCount = CsvFieldParser.ParseRecord(rowData, dialect, fields);
        _fieldCount = fieldCount;

        // Find matches in this row for highlighting
        List<SearchResult> matches = state.SearchResults;
        int matchCursor = SearchHighlightHelper.BinarySearchFirstMatch(matches, rowOffset);

        Span<byte> unescaped = stackalloc byte[4096];
        for (int i = 0; i < fieldCount; i++)
        {
            string label = i < headers.Length ? headers[i] : $"Column {i + 1}";
            int written = CsvFieldParser.UnescapeField(rowData, fields[i], dialect, unescaped);
            string value = System.Text.Encoding.UTF8.GetString(unescaped[..written]);
            bool isHidden = hiddenCols.Contains(i);

            // Check for search match overlap with this field
            long fieldStart = rowOffset + fields[i].Offset;
            long fieldEnd = fieldStart + fields[i].Length - 1;
            bool hasMatch = false;
            bool hasActiveMatch = false;

            int mc = matchCursor;
            while (mc < matches.Count)
            {
                long mStart = matches[mc].Offset;
                long mEnd = mStart + matches[mc].Length - 1;
                if (mStart > fieldEnd) break;
                if (mEnd >= fieldStart)
                {
                    hasMatch = true;
                    hasActiveMatch = mc == state.CurrentMatchIndex;
                    break;
                }
                mc++;
            }

            Border fieldRow = CreateFieldRow(label, value, i, isHidden, hasMatch, hasActiveMatch);
            FieldsPanel.Children.Add(fieldRow);
        }
    }

    private static Border CreateFieldRow(string label, string value, int index, bool isHidden,
        bool hasMatch, bool hasActiveMatch)
    {
        StackPanel row = new()
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 1,
            Margin = new Thickness(0, 1)
        };

        TextBlock labelBlock = new()
        {
            Text = isHidden ? $"{label} (hidden)" : label,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brushes.Gray
        };
        if (isHidden)
            labelBlock.FontStyle = FontStyle.Italic;

        row.Children.Add(labelBlock);

        bool isEmpty = string.IsNullOrEmpty(value);
        TextBlock valueBlock = new()
        {
            Text = isEmpty ? "(empty)" : value,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        if (isEmpty)
        {
            valueBlock.Foreground = Brushes.DarkGray;
            valueBlock.FontStyle = FontStyle.Italic;
        }
        if (isHidden)
            valueBlock.Opacity = 0.5;

        row.Children.Add(valueBlock);

        // Background: match highlight > active col > stripe > none
        IBrush? bg = null;
        if (hasActiveMatch)
            bg = ActiveMatchBrush;
        else if (hasMatch)
            bg = MatchBrush;
        else if (index % 2 == 0)
            bg = StripeBrush;

        return new Border
        {
            Child = row,
            Padding = new Thickness(4, 2),
            CornerRadius = new CornerRadius(3),
            Background = bg,
            Tag = "field"
        };
    }

    private void UpdateHighlight(int activeCol, AppState state)
    {
        List<SearchResult> matches = state.SearchResults;

        for (int i = 0; i < FieldsPanel.Children.Count; i++)
        {
            if (FieldsPanel.Children[i] is not Border border) continue;

            // Determine background priority: active col > match > stripe > none
            if (i == activeCol)
            {
                border.Background = ActiveColBrush;
            }
            else
            {
                // Preserve match or stripe background
                IBrush? bg = i % 2 == 0 ? StripeBrush : null;
                border.Background = bg;
            }
        }
    }

    private static int FindRowEnd(byte[] buf, int length, CsvDialect dialect)
    {
        bool inQuoted = false;
        for (int i = 0; i < length; i++)
        {
            byte b = buf[i];
            if (inQuoted)
            {
                if (b == dialect.Quote)
                {
                    if (i + 1 < length && buf[i + 1] == dialect.Quote)
                    {
                        i++;
                        continue;
                    }
                    inQuoted = false;
                }
                continue;
            }
            if (b == dialect.Quote && dialect.Quote != 0) { inQuoted = true; continue; }
            if (b == (byte)'\n' || b == (byte)'\r') return i;
        }
        return length;
    }
}
