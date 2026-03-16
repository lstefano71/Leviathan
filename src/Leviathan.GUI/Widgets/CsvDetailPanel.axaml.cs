using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Leviathan.Core.Csv;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Side panel showing field-by-field detail of the current CSV record.
/// Auto-updates when the cursor row changes. Toggled with F2.
/// </summary>
public sealed partial class CsvDetailPanel : UserControl
{
    private long _lastRow = -2;
    private int _lastCol = -2;
    private int _fieldCount;

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

        UpdateHighlight(cursorCol);
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
        TitleText.Text = "Record Detail";
        FieldsPanel.Children.Clear();
    }

    private void RebuildFields(AppState state, Views.CsvViewControl csvView, long cursorRow)
    {
        FieldsPanel.Children.Clear();
        _fieldCount = 0;

        long rowOffset = csvView.GetRowByteOffset(cursorRow);
        if (rowOffset < 0 || rowOffset >= state.FileLength)
        {
            TitleText.Text = "Record Detail";
            return;
        }

        TitleText.Text = $"Record #{cursorRow + 1}";

        CsvDialect dialect = state.CsvDialect;
        string[] headers = state.CsvHeaderNames;

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

        Span<byte> unescaped = stackalloc byte[4096];
        for (int i = 0; i < fieldCount; i++)
        {
            string label = i < headers.Length ? headers[i] : $"Column {i + 1}";
            int written = CsvFieldParser.UnescapeField(rowData, fields[i], dialect, unescaped);
            string value = System.Text.Encoding.UTF8.GetString(unescaped[..written]);

            Border fieldRow = CreateFieldRow(label, value);
            FieldsPanel.Children.Add(fieldRow);
        }
    }

    private static Border CreateFieldRow(string label, string value)
    {
        StackPanel row = new()
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Spacing = 1,
            Margin = new Thickness(0, 2)
        };

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Foreground = Brushes.Gray
        });

        row.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(value) ? "(empty)" : value,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = string.IsNullOrEmpty(value) ? Brushes.DarkGray : null,
            FontStyle = string.IsNullOrEmpty(value) ? FontStyle.Italic : FontStyle.Normal
        });

        return new Border
        {
            Child = row,
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(3),
            Tag = "field"
        };
    }

    private void UpdateHighlight(int activeCol)
    {
        for (int i = 0; i < FieldsPanel.Children.Count; i++)
        {
            if (FieldsPanel.Children[i] is Border border)
            {
                border.Background = i == activeCol
                    ? new SolidColorBrush(Color.FromArgb(30, 100, 160, 255))
                    : null;
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
