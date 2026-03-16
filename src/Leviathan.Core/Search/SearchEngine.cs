using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Leviathan.Core.Text;

namespace Leviathan.Core.Search;

/// <summary>
/// High-performance streaming pattern search over a <see cref="Document"/>.
/// Literal search uses <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/>
/// (SIMD-accelerated in .NET 8+) with 4 MB chunk reads and proper boundary overlap
/// so matches spanning two consecutive chunks are never missed.
/// Allocation policy: one ArrayPool buffer per search, no per-match allocation.
/// </summary>
public static class SearchEngine
{
  private const int ChunkSize = 4 * 1024 * 1024; // 4 MB

  /// <summary>
  /// Yields all occurrences of <paramref name="pattern"/> in <paramref name="doc"/>.
  /// The search is synchronous; wrap in <c>Task.Run</c> for background execution.
  /// When a <paramref name="ct"/> is supplied, cancellation is checked per-chunk
  /// (every ~4 MB) so the search can be stopped promptly even when matches are sparse.
  /// </summary>
  public static IEnumerable<SearchResult> FindAll(
      Document doc, byte[] pattern, bool caseSensitive = true,
      bool wholeWord = false, CancellationToken ct = default)
  {
    if (pattern.Length == 0 || doc.Length == 0) yield break;
    if (pattern.Length > doc.Length) yield break;

    int pLen = pattern.Length;
    int overlap = pLen - 1; // bytes carried over from the previous chunk

    byte[] effectivePattern = caseSensitive ? pattern : FoldToLower(pattern);

    // Buffer layout per chunk: [overlap from prev][ChunkSize new bytes]
    byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + overlap);

    try {
      long docBase = 0;    // document offset of the first NEW byte in this iteration
      int prevLen = 0;     // how many overlap bytes are currently sitting in buffer[0..prevLen]

      while (docBase < doc.Length) {
        ct.ThrowIfCancellationRequested();

        int toRead = (int)Math.Min(ChunkSize, doc.Length - docBase);
        int bytesRead = doc.Read(docBase, buffer.AsSpan(prevLen, toRead));
        if (bytesRead == 0) break;

        int totalLen = prevLen + bytesRead;
        long docOffsetOfBufferStart = docBase - prevLen;

        // For case-insensitive search, fold the newly-read bytes to lowercase.
        // Overlap bytes carried from the previous chunk are already folded.
        if (!caseSensitive)
          FoldBufferToLower(buffer, prevLen, bytesRead);

        // Use Span.IndexOf (SIMD-accelerated in .NET 8+) for the inner search loop.
        // Spans are created inline to avoid ref struct crossing yield boundaries.
        int searchStart = 0;

        while (searchStart <= totalLen - pLen) {
          int idx = buffer.AsSpan(searchStart, totalLen - searchStart)
                         .IndexOf(effectivePattern.AsSpan());
          if (idx < 0) break;

          int matchPos = searchStart + idx;
          long matchOffset = docOffsetOfBufferStart + matchPos;

          if (matchOffset >= docBase) {
            if (!wholeWord || IsWholeWordMatch(buffer, matchPos, pLen, totalLen))
              yield return new SearchResult(matchOffset, pLen);
          }

          searchStart = matchPos + 1;
        }

        // Carry the last `overlap` bytes forward for the next chunk
        int newPrevLen = Math.Min(overlap, totalLen);
        if (newPrevLen > 0)
          Buffer.BlockCopy(buffer, totalLen - newPrevLen, buffer, 0, newPrevLen);

        prevLen = newPrevLen;
        docBase += bytesRead;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <summary>
  /// Yields all occurrences of a regex <paramref name="pattern"/> in <paramref name="doc"/>.
  /// Decodes byte chunks to text using <paramref name="decoder"/>, then runs the regex.
  /// The search is synchronous; wrap in <c>Task.Run</c> for background execution.
  /// Uses <see cref="RegexOptions.NonBacktracking"/> for linear-time guarantees and AOT safety.
  /// </summary>
  /// <returns>
  /// An empty sequence if <paramref name="pattern"/> is empty or invalid regex.
  /// </returns>
  public static IEnumerable<SearchResult> FindAllRegex(
      Document doc, ITextDecoder decoder, string pattern,
      bool caseSensitive = true, CancellationToken ct = default)
  {
    if (string.IsNullOrEmpty(pattern) || doc.Length == 0) yield break;

    RegexOptions options = RegexOptions.NonBacktracking;
    if (!caseSensitive) options |= RegexOptions.IgnoreCase;

    Regex regex;
    try { regex = new Regex(pattern, options); }
    catch (RegexParseException) { yield break; }

    Encoding encoding = GetDotNetEncoding(decoder);

    // Text overlap to catch regex matches that span chunk boundaries.
    // We carry forward enough bytes to cover any reasonable match.
    int textOverlapBytes = Math.Max(pattern.Length * 4 * decoder.MinCharBytes, 4096);

    byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + textOverlapBytes);

    try {
      long docBase = 0;
      int prevLen = 0;

      while (docBase < doc.Length) {
        ct.ThrowIfCancellationRequested();

        int toRead = (int)Math.Min(ChunkSize, doc.Length - docBase);
        int bytesRead = doc.Read(docBase, buffer.AsSpan(prevLen, toRead));
        if (bytesRead == 0) break;

        int totalLen = prevLen + bytesRead;
        long docOffsetOfBufferStart = docBase - prevLen;

        // Align totalLen to a character boundary to avoid splitting a multi-byte char
        int alignedLen = AlignToCharBoundary(buffer, totalLen, decoder);

        string text = encoding.GetString(buffer, 0, alignedLen);

        // Track char→byte offset incrementally for efficiency
        int prevCharEnd = 0;
        int prevByteEnd = 0;

        // Use Matches() rather than EnumerateMatches() because
        // ValueMatchEnumerator is a ref struct that cannot cross yield boundaries.
        foreach (Match match in regex.Matches(text)) {
          prevByteEnd += encoding.GetByteCount(text.AsSpan(prevCharEnd, match.Index - prevCharEnd));
          prevCharEnd = match.Index;

          long matchOffset = docOffsetOfBufferStart + prevByteEnd;
          int matchByteLen = encoding.GetByteCount(text.AsSpan(match.Index, match.Length));

          if (matchOffset >= docBase && matchByteLen > 0)
            yield return new SearchResult(matchOffset, matchByteLen);
        }

        // Carry forward overlap bytes for cross-boundary match detection
        int newPrevLen = Math.Min(textOverlapBytes, alignedLen);
        if (newPrevLen > 0)
          Buffer.BlockCopy(buffer, alignedLen - newPrevLen, buffer, 0, newPrevLen);

        prevLen = newPrevLen;
        docBase += bytesRead;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <summary>
  /// Validates whether a regex pattern is syntactically correct for the
  /// <see cref="RegexOptions.NonBacktracking"/> engine.
  /// </summary>
  public static bool IsValidRegex(string pattern)
  {
    try {
      _ = new Regex(pattern, RegexOptions.NonBacktracking);
      return true;
    } catch (RegexParseException) {
      return false;
    }
  }

  /// <summary>
  /// Finds the first occurrence of <paramref name="pattern"/> at or after
  /// <paramref name="startOffset"/>. Returns null if not found.
  /// </summary>
  public static SearchResult? FindNext(Document doc, byte[] pattern, long startOffset)
  {
    foreach (SearchResult result in FindAll(doc, pattern)) {
      if (result.Offset >= startOffset)
        return result;
    }
    return null;
  }

  /// <summary>
  /// Finds the last occurrence of <paramref name="pattern"/> strictly before
  /// <paramref name="beforeOffset"/>. Returns null if not found.
  /// </summary>
  public static SearchResult? FindPrevious(Document doc, byte[] pattern, long beforeOffset)
  {
    SearchResult? last = null;
    foreach (SearchResult result in FindAll(doc, pattern)) {
      if (result.Offset >= beforeOffset) break;
      last = result;
    }
    return last;
  }

  /// <summary>
  /// Parses a space-separated hex string into a byte array.
  /// Accepts both "DE AD BE EF" and "DEADBEEF" forms.
  /// Throws <see cref="FormatException"/> on invalid input.
  /// </summary>
  public static byte[] ParseHexPattern(ReadOnlySpan<char> input)
  {
    // Compact by removing all spaces
    Span<char> compact = input.Length <= 512
        ? stackalloc char[input.Length]
        : new char[input.Length];

    int compactLen = 0;
    for (int i = 0; i < input.Length; i++) {
      char c = input[i];
      if (c == ' ' || c == '\t') continue;
      compact[compactLen++] = c;
    }

    if (compactLen == 0) return [];
    if (compactLen % 2 != 0)
      throw new FormatException("Hex pattern must have an even number of hex digits.");

    byte[] result = new byte[compactLen / 2];
    for (int i = 0; i < compactLen; i += 2) {
      if (!byte.TryParse(compact.Slice(i, 2), NumberStyles.HexNumber, null, out result[i / 2]))
        throw new FormatException($"Invalid hex byte at position {i}: '{compact[i]}{compact[i + 1]}'");
    }

    return result;
  }

  // ── Whole-word helpers ──────────────────────────────────────────────────

  /// <summary>Tests whether <paramref name="b"/> is an ASCII word character [A-Za-z0-9_].</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static bool IsWordChar(byte b) =>
      (b >= (byte)'A' && b <= (byte)'Z') ||
      (b >= (byte)'a' && b <= (byte)'z') ||
      (b >= (byte)'0' && b <= (byte)'9') ||
      b == (byte)'_';

  /// <summary>
  /// Returns true when the match at <paramref name="matchStart"/> is surrounded
  /// by non-word characters (or buffer boundaries).
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static bool IsWholeWordMatch(byte[] buffer, int matchStart, int matchLen, int bufferLen)
  {
    if (matchStart > 0 && IsWordChar(buffer[matchStart - 1]))
      return false;
    int afterMatch = matchStart + matchLen;
    if (afterMatch < bufferLen && IsWordChar(buffer[afterMatch]))
      return false;
    return true;
  }

  // ── Case-folding helpers ────────────────────────────────────────────────

  /// <summary>Folds ASCII uppercase to lowercase.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static byte ToLowerAscii(byte b) => b >= 0x41 && b <= 0x5A ? (byte)(b + 0x20) : b;

  /// <summary>Returns a copy of the pattern with ASCII bytes folded to lowercase.</summary>
  private static byte[] FoldToLower(byte[] pattern)
  {
    byte[] result = new byte[pattern.Length];
    for (int i = 0; i < pattern.Length; i++)
      result[i] = ToLowerAscii(pattern[i]);
    return result;
  }

  /// <summary>
  /// Folds a region of the buffer to ASCII lowercase in-place.
  /// </summary>
  private static void FoldBufferToLower(byte[] buffer, int offset, int length)
  {
    for (int i = offset; i < offset + length; i++)
      buffer[i] = ToLowerAscii(buffer[i]);
  }

  // ── Regex helpers ───────────────────────────────────────────────────────

  /// <summary>
  /// Maps the <see cref="ITextDecoder"/> encoding to the closest
  /// <see cref="System.Text.Encoding"/> for bulk byte↔text conversion.
  /// </summary>
  private static Encoding GetDotNetEncoding(ITextDecoder decoder) =>
      decoder.Encoding switch {
        TextEncoding.Utf8 => Encoding.UTF8,
        TextEncoding.Utf16Le => Encoding.Unicode,
        _ => Encoding.Latin1, // Windows-1252 ≈ Latin1 for byte-count mapping
      };

  /// <summary>
  /// Trims <paramref name="length"/> backward so it does not split a multi-byte character.
  /// </summary>
  private static int AlignToCharBoundary(byte[] buffer, int length, ITextDecoder decoder)
  {
    if (decoder.MinCharBytes <= 1 && decoder.Encoding == TextEncoding.Utf8) {
      // Walk back past any trailing continuation bytes (10xxxxxx)
      int pos = length;
      while (pos > 0 && (buffer[pos - 1] & 0xC0) == 0x80) pos--;
      // If the lead byte indicates more bytes than available, trim it
      if (pos > 0) {
        byte lead = buffer[pos - 1];
        int expected = lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
        if (pos - 1 + expected > length) pos--;
      }
      return pos;
    }
    if (decoder.MinCharBytes == 2) {
      // UTF-16 LE: ensure even length
      return length & ~1;
    }
    return length; // Single-byte encodings are always aligned
  }
}
