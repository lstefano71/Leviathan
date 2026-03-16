using Leviathan.Core.Search;
using Leviathan.Core.Text;

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

  private static Document MakeDocFromText(string text)
      => MakeDoc(System.Text.Encoding.UTF8.GetBytes(text));

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

  // ── Case-insensitive search ─────────────────────────────────────────────

  [Fact]
  public void FindAll_CaseInsensitive_FindsUpperAndLower()
  {
    using var doc = MakeDocFromText("Hello HELLO hello");
    byte[] pattern = "hello"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, caseSensitive: false).ToList();

    Assert.Equal(3, results.Count);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(6L, results[1].Offset);
    Assert.Equal(12L, results[2].Offset);
  }

  [Fact]
  public void FindAll_CaseInsensitive_MixedCasePattern()
  {
    using var doc = MakeDocFromText("ABCABC");
    byte[] pattern = "AbC"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, caseSensitive: false).ToList();

    Assert.Equal(2, results.Count);
  }

  [Fact]
  public void FindAll_CaseSensitive_DoesNotMatchDifferentCase()
  {
    using var doc = MakeDocFromText("Hello");
    byte[] pattern = "hello"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, caseSensitive: true).ToList();

    Assert.Empty(results);
  }

  // ── Whole-word search ───────────────────────────────────────────────────

  [Fact]
  public void FindAll_WholeWord_MatchesStandaloneWord()
  {
    // "cat concatenate cat scat cat"
    //  ^0              ^16          ^25 — standalone "cat"
    using var doc = MakeDocFromText("cat concatenate cat scat cat");
    byte[] pattern = "cat"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, wholeWord: true).ToList();

    Assert.Equal(3, results.Count);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(16L, results[1].Offset);
    Assert.Equal(25L, results[2].Offset);
  }

  [Fact]
  public void FindAll_WholeWord_MatchesAtStartOfDocument()
  {
    using var doc = MakeDocFromText("cat is here");
    byte[] pattern = "cat"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, wholeWord: true).ToList();

    Assert.Single(results);
    Assert.Equal(0L, results[0].Offset);
  }

  [Fact]
  public void FindAll_WholeWord_MatchesAtEndOfDocument()
  {
    using var doc = MakeDocFromText("the cat");
    byte[] pattern = "cat"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, wholeWord: true).ToList();

    Assert.Single(results);
    Assert.Equal(4L, results[0].Offset);
  }

  [Fact]
  public void FindAll_WholeWord_RejectsMidWord()
  {
    using var doc = MakeDocFromText("concatenate");
    byte[] pattern = "cat"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, wholeWord: true).ToList();

    Assert.Empty(results);
  }

  [Fact]
  public void FindAll_WholeWord_PunctuationBoundary()
  {
    using var doc = MakeDocFromText("(cat)");
    byte[] pattern = "cat"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, wholeWord: true).ToList();

    Assert.Single(results);
    Assert.Equal(1L, results[0].Offset);
  }

  [Fact]
  public void FindAll_WholeWord_CaseInsensitive()
  {
    using var doc = MakeDocFromText("Cat CAT scat");
    byte[] pattern = "cat"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern, caseSensitive: false, wholeWord: true).ToList();

    Assert.Equal(2, results.Count);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(4L, results[1].Offset);
  }

  // ── Regex search ────────────────────────────────────────────────────────

  [Fact]
  public void FindAllRegex_SimplePattern_FindsMatches()
  {
    using var doc = MakeDocFromText("abc 123 def 456");
    ITextDecoder decoder = new Utf8TextDecoder();
    var results = SearchEngine.FindAllRegex(doc, decoder, @"\d+").ToList();

    Assert.Equal(2, results.Count);
    Assert.Equal(4L, results[0].Offset);
    Assert.Equal(3L, results[0].Length);
    Assert.Equal(12L, results[1].Offset);
    Assert.Equal(3L, results[1].Length);
  }

  [Fact]
  public void FindAllRegex_CaseInsensitive_FindsBothCases()
  {
    using var doc = MakeDocFromText("Hello HELLO hello");
    ITextDecoder decoder = new Utf8TextDecoder();
    var results = SearchEngine.FindAllRegex(doc, decoder, "hello", caseSensitive: false).ToList();

    Assert.Equal(3, results.Count);
  }

  [Fact]
  public void FindAllRegex_CaseSensitive_OnlyExactCase()
  {
    using var doc = MakeDocFromText("Hello HELLO hello");
    ITextDecoder decoder = new Utf8TextDecoder();
    var results = SearchEngine.FindAllRegex(doc, decoder, "hello", caseSensitive: true).ToList();

    Assert.Single(results);
    Assert.Equal(12L, results[0].Offset);
  }

  [Fact]
  public void FindAllRegex_InvalidPattern_ReturnsEmpty()
  {
    using var doc = MakeDocFromText("test");
    ITextDecoder decoder = new Utf8TextDecoder();
    var results = SearchEngine.FindAllRegex(doc, decoder, @"[invalid").ToList();

    Assert.Empty(results);
  }

  [Fact]
  public void FindAllRegex_EmptyPattern_ReturnsEmpty()
  {
    using var doc = MakeDocFromText("test");
    ITextDecoder decoder = new Utf8TextDecoder();
    var results = SearchEngine.FindAllRegex(doc, decoder, "").ToList();

    Assert.Empty(results);
  }

  [Fact]
  public void FindAllRegex_EmptyDocument_ReturnsEmpty()
  {
    using var doc = new Document();
    ITextDecoder decoder = new Utf8TextDecoder();
    var results = SearchEngine.FindAllRegex(doc, decoder, "test").ToList();

    Assert.Empty(results);
  }

  [Fact]
  public void FindAllRegex_WordBoundary_MatchesWholeWords()
  {
    using var doc = MakeDocFromText("cat concatenate cat");
    ITextDecoder decoder = new Utf8TextDecoder();
    var results = SearchEngine.FindAllRegex(doc, decoder, @"\bcat\b").ToList();

    Assert.Equal(2, results.Count);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(16L, results[1].Offset);
  }

  [Fact]
  public void FindAllRegex_Cancellation_Respects()
  {
    byte[] data = new byte[64 * 1024];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)('a' + (i % 26));
    using var doc = MakeDoc(data);
    using CancellationTokenSource cts = new();
    ITextDecoder decoder = new Utf8TextDecoder();

    int count = 0;
    foreach (SearchResult _ in SearchEngine.FindAllRegex(doc, decoder, "a", ct: cts.Token)) {
      count++;
      if (count >= 5) {
        cts.Cancel();
        break;
      }
    }

    Assert.Equal(5, count);
  }

  // ── IsValidRegex ────────────────────────────────────────────────────────

  [Fact]
  public void IsValidRegex_ValidPattern_ReturnsTrue()
  {
    Assert.True(SearchEngine.IsValidRegex(@"\d+"));
    Assert.True(SearchEngine.IsValidRegex("abc"));
    Assert.True(SearchEngine.IsValidRegex(@"\bword\b"));
  }

  [Fact]
  public void IsValidRegex_InvalidPattern_ReturnsFalse()
  {
    Assert.False(SearchEngine.IsValidRegex(@"[unclosed"));
    Assert.False(SearchEngine.IsValidRegex(@"(unbalanced"));
  }

  // ── SIMD correctness (Span.IndexOf vs old BMH) ─────────────────────────

  [Fact]
  public void FindAll_LargeDocument_MatchesExpectedCount()
  {
    // Build a document large enough to span multiple internal chunks
    // Pattern "FIND" inserted at known positions
    byte[] data = new byte[256 * 1024];
    Array.Fill(data, (byte)0x00);
    byte[] marker = "FIND"u8.ToArray();

    int expectedCount = 0;
    for (int i = 0; i < data.Length - marker.Length; i += 1000) {
      Buffer.BlockCopy(marker, 0, data, i, marker.Length);
      expectedCount++;
    }

    using var doc = MakeDoc(data);
    var results = SearchEngine.FindAll(doc, marker).ToList();

    Assert.Equal(expectedCount, results.Count);

    // Verify offsets are at expected positions
    for (int i = 0; i < results.Count; i++) {
      Assert.Equal(i * 1000L, results[i].Offset);
      Assert.Equal(4L, results[i].Length);
    }
  }

  [Fact]
  public void FindAll_OverlappingPatterns_AllFound()
  {
    // "aaa" in "aaaa" should find 2 matches at offsets 0 and 1
    using var doc = MakeDocFromText("aaaa");
    byte[] pattern = "aaa"u8.ToArray();
    var results = SearchEngine.FindAll(doc, pattern).ToList();

    Assert.Equal(2, results.Count);
    Assert.Equal(0L, results[0].Offset);
    Assert.Equal(1L, results[1].Offset);
  }
}
