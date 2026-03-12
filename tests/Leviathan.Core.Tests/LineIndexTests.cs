using Leviathan.Core.Indexing;

namespace Leviathan.Core.Tests;

public class LineIndexTests
{
  [Fact]
  public unsafe void ScanChunk_CountsNewlines()
  {
    // Create a buffer with known newlines
    var data = new byte[256];
    data[10] = 0x0A;
    data[50] = 0x0A;
    data[100] = 0x0A;
    data[200] = 0x0A;

    var index = new LineIndex(sparseFactor: 2);

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 0, CancellationToken.None);
    }

    index.MarkComplete();

    Assert.Equal(4, index.TotalLineCount);
    Assert.True(index.IsComplete);
  }

  [Fact]
  public unsafe void ScanChunk_BuildsSparseIndex()
  {
    // Create 2000+ newlines so sparse entries are built at sparseFactor=100
    var data = new byte[4096];
    int nlCount = 0;
    for (int i = 0; i < data.Length; i++) {
      if (i % 4 == 0) {
        data[i] = 0x0A;
        nlCount++;
      } else {
        data[i] = 0x41; // 'A'
      }
    }

    var index = new LineIndex(sparseFactor: 100);

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 0, CancellationToken.None);
    }

    index.MarkComplete();

    Assert.Equal(nlCount, index.TotalLineCount);
    Assert.True(index.SparseEntryCount > 0);

    // The first sparse offset should be the position of the 100th newline
    long firstSparseOff = index.GetSparseOffset(0);
    Assert.True(firstSparseOff > 0);
  }

  [Fact]
  public unsafe void ScanChunk_EmptyData_ZeroLines()
  {
    var data = new byte[128]; // all zeros except 0x0A isn't present (0x00 != 0x0A)
                              // Actually 0x00 is not 0x0A, so zero newlines
    var index = new LineIndex();

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 0, CancellationToken.None);
    }

    Assert.Equal(0, index.TotalLineCount);
  }

  [Fact]
  public unsafe void ScanChunk_Cancellation_StopsEarly()
  {
    // Create data with many newlines
    var data = new byte[4096];
    Array.Fill(data, (byte)0x0A);

    var index = new LineIndex(sparseFactor: 10);
    using CancellationTokenSource cts = new();

    // Cancel immediately — ScanChunk should throw
    cts.Cancel();
    bool threw = false;
    fixed (byte* ptr = data) {
      try {
        index.ScanChunk(ptr, data.Length, baseOffset: 0, cts.Token);
      } catch (OperationCanceledException) {
        threw = true;
      }
    }

    Assert.True(threw, "ScanChunk should throw OperationCanceledException when token is cancelled");
    // Should not have processed all newlines
    Assert.True(index.TotalLineCount < data.Length);
  }

  [Fact]
  public unsafe void ScanChunk_SparseEntryCount_ConsistentWithOffsets()
  {
    // Verify that when SparseEntryCount is N, entries 0..N-1 are valid
    var data = new byte[8192];
    int nlCount = 0;
    for (int i = 0; i < data.Length; i++) {
      if (i % 2 == 0) {
        data[i] = 0x0A;
        nlCount++;
      } else {
        data[i] = 0x41;
      }
    }

    var index = new LineIndex(sparseFactor: 50);

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 1000, CancellationToken.None);
    }

    int entryCount = index.SparseEntryCount;
    Assert.True(entryCount > 0);

    // All entries should be within the data range
    for (int i = 0; i < entryCount; i++) {
      long offset = index.GetSparseOffset(i);
      Assert.True(offset >= 1000, $"Entry {i} offset {offset} should be >= base 1000");
      Assert.True(offset < 1000 + data.Length, $"Entry {i} offset {offset} should be < base + length");
    }
  }
}
