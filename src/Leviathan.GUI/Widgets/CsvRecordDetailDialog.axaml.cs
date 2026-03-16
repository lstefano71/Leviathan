using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Leviathan.Core.Csv;

namespace Leviathan.GUI.Widgets;

/// <summary>
/// Dialog showing a single CSV record in vertical key:value format (F2).
/// </summary>
public sealed partial class CsvRecordDetailDialog : Window
{
    public CsvRecordDetailDialog(AppState state, long rowIndex)
    {
        InitializeComponent();

        TitleText.Text = $"Record #{rowIndex + 1}";
        CloseButton.Click += (_, _) => Close();

        PopulateFields(state, rowIndex);
    }

    public CsvRecordDetailDialog() { InitializeComponent(); }

    private void PopulateFields(AppState state, long rowIndex)
    {
        if (state.Document is null || state.CsvRowIndex is null) return;

        CsvDialect dialect = state.CsvDialect;
        string[] headers = state.CsvHeaderNames;

        // Navigate to the row offset using sparse index
        long adjustedRow = rowIndex + (dialect.HasHeader ? 1 : 0);
        int sparseIdx = (int)(adjustedRow / state.CsvRowIndex.SparseFactor);
        long rowOffset = sparseIdx < state.CsvRowIndex.SparseEntryCount
            ? state.CsvRowIndex.GetSparseOffset(sparseIdx)
            : 0;

        // Walk forward to exact row (simplified)
        long targetWithinBlock = adjustedRow - (long)sparseIdx * state.CsvRowIndex.SparseFactor;
        if (targetWithinBlock > 0 && rowOffset >= 0)
        {
            int scanLen = (int)Math.Min(65536, state.FileLength - rowOffset);
            byte[] scanBuf = new byte[scanLen];
            state.Document.Read(rowOffset, scanBuf);
            int pos = 0;
            long rowsSkipped = 0;
            bool inQ = false;
            byte q = dialect.Quote;
            while (pos < scanLen && rowsSkipped < targetWithinBlock)
            {
                byte b = scanBuf[pos];
                if (inQ) { if (b == q) { if (pos + 1 < scanLen && scanBuf[pos + 1] == q) { pos += 2; continue; } inQ = false; } pos++; continue; }
                if (b == q && q != 0) { inQ = true; pos++; continue; }
                if (b == (byte)'\n') { rowsSkipped++; pos++; continue; }
                if (b == (byte)'\r') { rowsSkipped++; pos++; if (pos < scanLen && scanBuf[pos] == (byte)'\n') pos++; continue; }
                pos++;
            }
            rowOffset += pos;
        }

        // Read row
        int readLen = (int)Math.Min(8192, state.FileLength - rowOffset);
        if (readLen <= 0) return;
        byte[] buf = new byte[readLen];
        state.Document.Read(rowOffset, buf);

        // Find row end
        int rowLen = 0;
        bool inQuoted = false;
        for (int i = 0; i < readLen; i++)
        {
            byte b = buf[i];
            if (inQuoted) { if (b == dialect.Quote) { if (i + 1 < readLen && buf[i + 1] == dialect.Quote) { i++; continue; } inQuoted = false; } continue; }
            if (b == dialect.Quote && dialect.Quote != 0) { inQuoted = true; continue; }
            if (b == (byte)'\n' || b == (byte)'\r') { rowLen = i; break; }
        }
        if (rowLen == 0) rowLen = readLen;

        ReadOnlySpan<byte> rowData = buf.AsSpan(0, rowLen);
        Span<CsvField> fields = stackalloc CsvField[256];
        int fieldCount = CsvFieldParser.ParseRecord(rowData, dialect, fields);

        Span<byte> unescaped = stackalloc byte[4096];
        for (int i = 0; i < fieldCount; i++)
        {
            string label = i < headers.Length ? headers[i] : $"Column {i + 1}";
            int written = CsvFieldParser.UnescapeField(rowData, fields[i], dialect, unescaped);
            string value = System.Text.Encoding.UTF8.GetString(unescaped[..written]);

            StackPanel row = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = $"{label}:",
                FontWeight = FontWeight.SemiBold,
                Width = 150,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            });

            FieldsPanel.Children.Add(row);
        }
    }
}
