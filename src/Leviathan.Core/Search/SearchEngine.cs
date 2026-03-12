using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Leviathan.Core.Search;

/// <summary>
/// High-performance streaming pattern search over a <see cref="Document"/>.
/// Uses Boyer-Moore-Horspool with 4 MB chunk reads and proper boundary overlap
/// so matches spanning two consecutive chunks are never missed.
/// Allocation policy: one ArrayPool buffer per search, no per-match allocation.
/// </summary>
public static class SearchEngine
{
  private const int ChunkSize = 4 * 1024 * 1024; // 4 MB

  /// <summary>
  /// Yields all occurrences of <paramref name="pattern"/> in <paramref name="doc"/>.
  /// The search is synchronous; wrap in <c>Task.Run</c> for background execution.
  /// </summary>
  public static IEnumerable<SearchResult> FindAll(Document doc, byte[] pattern, bool caseSensitive = true)
  {
    if (pattern.Length == 0 || doc.Length == 0) yield break;
    if (pattern.Length > doc.Length) yield break;

    int pLen = pattern.Length;
    int overlap = pLen - 1; // bytes carried over from the previous chunk

    // For case-insensitive search, fold the pattern to lowercase
    byte[] effectivePattern = caseSensitive ? pattern : FoldToLower(pattern);
    int[] badChar = caseSensitive
        ? BuildBadCharTable(effectivePattern)
        : BuildBadCharTableCaseInsensitive(effectivePattern);

    // Buffer layout per chunk: [overlap from prev][ChunkSize new bytes]
    byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + overlap);

    try {
      long docBase = 0;    // document offset of the first NEW byte in this iteration
      int prevLen = 0;     // how many overlap bytes are currently sitting in buffer[0..prevLen]

      while (docBase < doc.Length) {
        int toRead = (int)Math.Min(ChunkSize, doc.Length - docBase);
        int bytesRead = doc.Read(docBase, buffer.AsSpan(prevLen, toRead));
        if (bytesRead == 0) break;

        int totalLen = prevLen + bytesRead;

        // docOffsetOfBufferStart: document offset that maps to buffer[0]
        long docOffsetOfBufferStart = docBase - prevLen;

        // Search buffer[0..totalLen] — only yield matches that START at >= docBase
        // (matches before docBase were already reported in the previous iteration)
        int end = totalLen - pLen;
        int i = 0;

        while (i <= end) {
          int j = pLen - 1;
          while (j >= 0 && (caseSensitive
              ? buffer[i + j] == effectivePattern[j]
              : ToLowerAscii(buffer[i + j]) == effectivePattern[j]))
            j--;

          if (j < 0) {
            // Full match at buffer[i]
            long matchOffset = docOffsetOfBufferStart + i;
            if (matchOffset >= docBase)
              yield return new SearchResult(matchOffset, pLen);
            i++;
          } else {
            byte b = caseSensitive ? buffer[i + pLen - 1] : ToLowerAscii(buffer[i + pLen - 1]);
            int skip = badChar[b];
            i += skip > 0 ? skip : 1;
          }
        }

        // Carry the last `overlap` bytes forward for the next chunk
        int newPrevLen = Math.Min(overlap, bytesRead);
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
  /// Finds the first occurrence of <paramref name="pattern"/> at or after
  /// <paramref name="startOffset"/>. Returns null if not found.
  /// </summary>
  public static SearchResult? FindNext(Document doc, byte[] pattern, long startOffset)
  {
    foreach (var result in FindAll(doc, pattern)) {
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
    foreach (var result in FindAll(doc, pattern)) {
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

  /// <summary>
  /// Boyer-Moore-Horspool bad-character skip table.
  /// For each byte value, stores the distance from the right end of the pattern
  /// to the rightmost occurrence of that byte (excluding the last position).
  /// Chars not in the pattern get a skip of <c>pattern.Length</c>.
  /// </summary>
  private static int[] BuildBadCharTable(byte[] pattern)
  {
    int pLen = pattern.Length;
    int[] table = new int[256];

    for (int i = 0; i < 256; i++)
      table[i] = pLen;

    for (int i = 0; i < pLen - 1; i++)
      table[pattern[i]] = pLen - 1 - i;

    return table;
  }

  /// <summary>
  /// Case-insensitive bad-character table. Builds entries for both upper and lower
  /// ASCII variants so that the skip table works correctly with folded lookups.
  /// </summary>
  private static int[] BuildBadCharTableCaseInsensitive(byte[] lowerPattern)
  {
    int pLen = lowerPattern.Length;
    int[] table = new int[256];

    for (int i = 0; i < 256; i++)
      table[i] = pLen;

    for (int i = 0; i < pLen - 1; i++) {
      byte b = lowerPattern[i];
      table[b] = pLen - 1 - i;
      // Also set for the uppercase variant
      if (b >= 0x61 && b <= 0x7A)
        table[b - 0x20] = pLen - 1 - i;
    }

    return table;
  }

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
}
