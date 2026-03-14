using Leviathan.Core.Csv;

namespace Leviathan.Core.Tests;

public sealed class CsvRowIndexTests
{
  [Fact]
  public unsafe void ScanChunk_SimpleRows_CorrectCount()
  {
    byte[] data = "a,b\n1,2\n3,4\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
    }

    index.MarkComplete();
    Assert.Equal(3, index.TotalRowCount);
    Assert.True(index.IsComplete);
  }

  [Fact]
  public unsafe void ScanChunk_QuotedFieldWithNewline_NotCountedAsRowBoundary()
  {
    // "line1\nline2" is inside quotes — should be 2 records, not 3
    byte[] data = "a,b\n1,\"line1\nline2\"\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
    }

    index.MarkComplete();
    Assert.Equal(2, index.TotalRowCount); // header + 1 data row
  }

  [Fact]
  public unsafe void ScanChunk_QuotedFieldWithCRLF_NotCountedAsRowBoundary()
  {
    byte[] data = "a,b\r\n1,\"line1\r\nline2\"\r\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
    }

    index.MarkComplete();
    Assert.Equal(2, index.TotalRowCount);
  }

  [Fact]
  public unsafe void ScanChunk_DoubledQuotesInField_CorrectStateMachine()
  {
    // "he""llo" contains doubled quotes — should not break state machine
    byte[] data = "a\n\"he\"\"llo\"\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
    }

    index.MarkComplete();
    Assert.Equal(2, index.TotalRowCount);
  }

  [Fact]
  public unsafe void ScanChunk_ChunkBoundaryInsideQuotedField_StateCarried()
  {
    // Split the data so chunk boundary falls inside a quoted field
    byte[] data = "a,b\n1,\"hello\nworld\"\n3,4\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    // Process in two chunks: first 10 bytes, then the rest
    int split = 10; // inside the quoted field "hello\nworld"
    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, split, 0, dialect, CancellationToken.None);
      index.ScanChunk(ptr + split, data.Length - split, split, dialect, CancellationToken.None);
    }

    index.MarkComplete();
    Assert.Equal(3, index.TotalRowCount); // header + data row with embedded newline + last row
  }

  [Fact]
  public unsafe void ScanChunk_SparseIndex_RecordsCorrectOffsets()
  {
    // Create enough rows to trigger sparse index storage
    var rows = new System.Text.StringBuilder();
    for (int i = 0; i < 150; i++)
      rows.Append($"{i},data{i}\n");

    byte[] data = System.Text.Encoding.UTF8.GetBytes(rows.ToString());
    CsvRowIndex index = new(sparseFactor: 50);
    CsvDialect dialect = CsvDialect.Csv();

    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
    }

    index.MarkComplete();
    Assert.Equal(150, index.TotalRowCount);
    Assert.True(index.SparseEntryCount > 0);

    // First sparse entry should be at some reasonable offset
    long offset = index.GetSparseOffset(0);
    Assert.True(offset > 0 && offset < data.Length);
  }

  [Fact]
  public unsafe void ScanChunk_EmptyFile_ZeroRows()
  {
    byte[] data = Array.Empty<byte>();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    // Nothing to scan
    index.MarkComplete();
    Assert.Equal(0, index.TotalRowCount);
    Assert.True(index.IsComplete);
  }

  [Fact]
  public unsafe void ScanChunk_HeaderOnly_OneRow()
  {
    byte[] data = "a,b,c\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
    }

    index.MarkComplete();
    Assert.Equal(1, index.TotalRowCount);
  }

  [Fact]
  public unsafe void ScanChunk_Cancellation_ThrowsAndStopsEarly()
  {
    byte[] data = "a,b\n1,2\n3,4\n5,6\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();
    using CancellationTokenSource cts = new();
    cts.Cancel();

    bool thrown = false;
    fixed (byte* ptr = data)
    {
      try
      {
        index.ScanChunk(ptr, data.Length, 0, dialect, cts.Token);
      }
      catch (OperationCanceledException)
      {
        thrown = true;
      }
    }

    Assert.True(thrown);
  }

  [Fact]
  public unsafe void EstimateRowForOffset_ReturnsReasonableEstimate()
  {
    var rows = new System.Text.StringBuilder();
    for (int i = 0; i < 200; i++)
      rows.Append($"{i},data{i}\n");

    byte[] data = System.Text.Encoding.UTF8.GetBytes(rows.ToString());
    CsvRowIndex index = new(sparseFactor: 50);
    CsvDialect dialect = CsvDialect.Csv();

    fixed (byte* ptr = data)
    {
      index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
    }

    index.MarkComplete();

    // Offset at the middle of the file should give a non-zero estimate
    long midEstimate = index.EstimateRowForOffset(data.Length / 2, data.Length);
    Assert.True(midEstimate > 0);
    Assert.True(midEstimate <= index.TotalRowCount);
  }

  [Fact]
  public void FirstDataRowOffset_SetAfterFirstRow()
  {
    byte[] data = "a,b\n1,2\n"u8.ToArray();
    CsvRowIndex index = new(sparseFactor: 100);
    CsvDialect dialect = CsvDialect.Csv();

    unsafe
    {
      fixed (byte* ptr = data)
      {
        index.ScanChunk(ptr, data.Length, 0, dialect, CancellationToken.None);
      }
    }

    Assert.Equal(4, index.FirstDataRowOffset); // "a,b\n" = 4 bytes
  }
}
