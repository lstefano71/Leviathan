using Leviathan.Core.Search;

namespace Leviathan.Core.Tests;

public class SearchTests
{
  // ── Helper ───────────────────────────────────────────────────────────────

  private static Document MakeDoc(byte[] data)
  {
    var doc = new Document();
    if (data.Length > 0)
      doc.Insert(0, data);
    return doc;
  }

  // ── FindAll ───────────────────────────────────────────────────────────────

  [Fact]
  public void FindAll_PatternAtStart_Found()
  {
    using var doc = MakeDoc([0xAA, 0xBB, 0x01, 0x02, 0x03]);
    var results = SearchEngine.FindAll(doc, [0xAA, 0xBB]).ToList();

    Assert.Single(results);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(2L, results[0].Length);
  }

  [Fact]
  public void FindAll_PatternAtEnd_Found()
  {
    using var doc = MakeDoc([0x01, 0x02, 0x03, 0xAA, 0xBB]);
    var results = SearchEngine.FindAll(doc, [0xAA, 0xBB]).ToList();

    Assert.Single(results);
    Assert.Equal(3L, results[0].Offset);
  }

  [Fact]
  public void FindAll_NoMatch_EmptyResults()
  {
    using var doc = MakeDoc([0x01, 0x02, 0x03]);
    var results = SearchEngine.FindAll(doc, [0xFF]).ToList();

    Assert.Empty(results);
  }

  [Fact]
  public void FindAll_MultipleMatches_AllFound()
  {
    using var doc = MakeDoc([0xAA, 0x00, 0xAA, 0x00, 0xAA]);
    var results = SearchEngine.FindAll(doc, [0xAA]).ToList();

    Assert.Equal(3, results.Count);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(2L, results[1].Offset);
    Assert.Equal(4L, results[2].Offset);
  }

  [Fact]
  public void FindAll_SingleBytePattern_Found()
  {
    using var doc = MakeDoc([0x41, 0x42, 0x41, 0x43]);
    var results = SearchEngine.FindAll(doc, [0x41]).ToList();

    Assert.Equal(2, results.Count);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(2L, results[1].Offset);
  }

  [Fact]
  public void FindAll_PatternSpansPieceBoundary_Found()
  {
    // Build a multi-piece document: original "HELLO" + append " WORLD"
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, "HELLO"u8.ToArray());

    try {
      using var doc = new Document(path);
      doc.Insert(5, " WORLD"u8);

      // Pattern "O W" spans the piece boundary (O is in original, " W" in append)
      byte[] pattern = "O W"u8.ToArray();
      var results = SearchEngine.FindAll(doc, pattern).ToList();

      Assert.Single(results);
      Assert.Equal(4L, results[0].Offset);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void FindAll_EmptyPattern_YieldsNothing()
  {
    using var doc = MakeDoc([0x01, 0x02, 0x03]);
    var results = SearchEngine.FindAll(doc, []).ToList();

    Assert.Empty(results);
  }

  [Fact]
  public void FindAll_EmptyDocument_YieldsNothing()
  {
    using var doc = new Document();
    var results = SearchEngine.FindAll(doc, [0xAA]).ToList();

    Assert.Empty(results);
  }

  [Fact]
  public void FindAll_PatternLongerThanDocument_YieldsNothing()
  {
    using var doc = MakeDoc([0x01, 0x02]);
    var results = SearchEngine.FindAll(doc, [0x01, 0x02, 0x03]).ToList();

    Assert.Empty(results);
  }

  // ── FindNext / FindPrevious ───────────────────────────────────────────────

  [Fact]
  public void FindNext_ReturnsFirstMatchAtOrAfterOffset()
  {
    using var doc = MakeDoc([0xAA, 0x00, 0xAA, 0x00, 0xAA]);
    var r = SearchEngine.FindNext(doc, [0xAA], startOffset: 1);

    Assert.NotNull(r);
    Assert.Equal(2L, r!.Value.Offset);
  }

  [Fact]
  public void FindPrevious_ReturnsLastMatchBeforeOffset()
  {
    using var doc = MakeDoc([0xAA, 0x00, 0xAA, 0x00, 0xAA]);
    var r = SearchEngine.FindPrevious(doc, [0xAA], beforeOffset: 4);

    Assert.NotNull(r);
    Assert.Equal(2L, r!.Value.Offset);
  }

  [Fact]
  public void FindNext_NoMatchAfterOffset_ReturnsNull()
  {
    using var doc = MakeDoc([0xAA, 0x00, 0x00]);
    var r = SearchEngine.FindNext(doc, [0xAA], startOffset: 1);

    Assert.Null(r);
  }

  // ── ParseHexPattern ──────────────────────────────────────────────────────

  [Fact]
  public void ParseHexPattern_SpacedPairs_ParsesCorrectly()
  {
    byte[] result = SearchEngine.ParseHexPattern("DE AD BE EF".AsSpan());

    Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, result);
  }

  [Fact]
  public void ParseHexPattern_NoSpaces_ParsesCorrectly()
  {
    byte[] result = SearchEngine.ParseHexPattern("DEADBEEF".AsSpan());

    Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, result);
  }

  [Fact]
  public void ParseHexPattern_LowerCase_ParsesCorrectly()
  {
    byte[] result = SearchEngine.ParseHexPattern("de ad".AsSpan());

    Assert.Equal(new byte[] { 0xDE, 0xAD }, result);
  }

  [Fact]
  public void ParseHexPattern_OddDigits_ThrowsFormatException()
  {
    Assert.Throws<FormatException>(() =>
        SearchEngine.ParseHexPattern("DEA".AsSpan()));
  }

  [Fact]
  public void ParseHexPattern_InvalidChars_ThrowsFormatException()
  {
    Assert.Throws<FormatException>(() =>
        SearchEngine.ParseHexPattern("GG".AsSpan()));
  }

  [Fact]
  public void ParseHexPattern_EmptyInput_ReturnsEmptyArray()
  {
    byte[] result = SearchEngine.ParseHexPattern("".AsSpan());

    Assert.Empty(result);
  }

  // ── CancellationToken support ───────────────────────────────────────────

  [Fact]
  public void FindAll_CancelledToken_ThrowsOperationCanceled()
  {
    using var doc = MakeDoc([0xAA, 0x00, 0xAA, 0x00, 0xAA]);
    using CancellationTokenSource cts = new();
    cts.Cancel();

    Assert.Throws<OperationCanceledException>(() =>
        SearchEngine.FindAll(doc, [0xAA], ct: cts.Token).ToList());
  }

  [Fact]
  public void FindAll_CancelDuringIteration_BreaksCleanly()
  {
    // Create a document with many matches
    byte[] data = new byte[64 * 1024];
    Array.Fill(data, (byte)0xAA);
    using var doc = MakeDoc(data);
    using CancellationTokenSource cts = new();

    int count = 0;
    foreach (SearchResult _ in SearchEngine.FindAll(doc, [0xAA], ct: cts.Token)) {
      count++;
      if (count >= 10) {
        cts.Cancel();
        break;
      }
    }

    Assert.Equal(10, count);
  }
}
