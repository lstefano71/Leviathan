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

  // ─── UTF-16 LE tests (charWidth = 2) ───

  [Fact]
  public unsafe void ScanChunk_Utf16Le_CountsNewlines()
  {
    // Build a UTF-16 LE buffer with known LF code units (0x0A 0x00) at even offsets
    var data = new byte[256];
    // Place LF code units (0x0A 0x00) at byte offsets 20, 100, 200 (all even)
    data[20] = 0x0A; data[21] = 0x00;
    data[100] = 0x0A; data[101] = 0x00;
    data[200] = 0x0A; data[201] = 0x00;

    var index = new LineIndex(charWidth: 2, sparseFactor: 2);

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 0, CancellationToken.None);
    }

    index.MarkComplete();

    Assert.Equal(3, index.TotalLineCount);
    Assert.True(index.IsComplete);
  }

  [Fact]
  public unsafe void ScanChunk_Utf16Le_IgnoresFalsePositives()
  {
    // 0x0A appearing as the HIGH byte of a code unit at an even position
    // e.g., U+1E0A (Ḋ) = 0x0A 0x1E in UTF-16 LE — this should NOT be counted as LF
    // because the code unit value is 0x1E0A, not 0x000A.
    // Also test 0x0A at odd byte positions (high byte of previous code unit).
    var data = new byte[64];

    // U+1E0A at byte offset 0: low byte = 0x0A, high byte = 0x1E → code unit 0x1E0A (not LF)
    data[0] = 0x0A; data[1] = 0x1E;
    // U+030A at byte offset 2: low byte = 0x0A, high byte = 0x03 → code unit 0x030A (not LF)
    data[2] = 0x0A; data[3] = 0x03;
    // A genuine LF at byte offset 4: 0x0A 0x00 → code unit 0x000A (IS LF)
    data[4] = 0x0A; data[5] = 0x00;
    // 0x0A in a high byte position: code unit at offset 6 = 0x41 0x0A → 0x0A41 (not LF)
    data[6] = 0x41; data[7] = 0x0A;

    var index = new LineIndex(charWidth: 2, sparseFactor: 1000);

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 0, CancellationToken.None);
    }

    // Only the genuine LF at offset 4 should be counted
    Assert.Equal(1, index.TotalLineCount);
  }

  [Fact]
  public unsafe void ScanChunk_Utf16Le_BuildsSparseIndex()
  {
    // Build a buffer with many UTF-16 LE LF code units so sparse entries are generated
    // Each line: 2 bytes for a character + 2 bytes for LF = 4 bytes per line
    int lineCount = 250;
    var data = new byte[lineCount * 4];
    for (int i = 0; i < lineCount; i++) {
      int offset = i * 4;
      data[offset] = 0x41; data[offset + 1] = 0x00; // 'A' in UTF-16 LE
      data[offset + 2] = 0x0A; data[offset + 3] = 0x00; // LF in UTF-16 LE
    }

    var index = new LineIndex(charWidth: 2, sparseFactor: 50);

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 0, CancellationToken.None);
    }

    index.MarkComplete();

    Assert.Equal(lineCount, index.TotalLineCount);
    Assert.True(index.SparseEntryCount > 0);

    // The first sparse offset should point to the 50th LF code unit (byte offset = 49 * 4 + 2 = 198)
    long firstSparseOff = index.GetSparseOffset(0);
    Assert.Equal(198, firstSparseOff);
  }

  [Fact]
  public unsafe void ScanChunk_Utf16Le_CrlfNotDoubleCounted()
  {
    // CRLF in UTF-16 LE = 0D 00 0A 00 — the CR should not be counted, only the LF
    var data = new byte[32];
    // CRLF at offset 0: CR = 0x0D 0x00, LF = 0x0A 0x00
    data[0] = 0x0D; data[1] = 0x00;
    data[2] = 0x0A; data[3] = 0x00;
    // Another CRLF at offset 8
    data[8] = 0x0D; data[9] = 0x00;
    data[10] = 0x0A; data[11] = 0x00;

    var index = new LineIndex(charWidth: 2, sparseFactor: 1000);

    fixed (byte* ptr = data) {
      index.ScanChunk(ptr, data.Length, baseOffset: 0, CancellationToken.None);
    }

    // Should count 2 LFs (CR is not 0x0A so it's not counted)
    Assert.Equal(2, index.TotalLineCount);
  }
}
