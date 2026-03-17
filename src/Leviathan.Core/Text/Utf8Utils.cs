using System.Runtime.CompilerServices;
using System.Text;

namespace Leviathan.Core.Text;

/// <summary>
/// Zero-allocation UTF-8 boundary detection and Rune iteration utilities.
/// Used by the Text View to handle arbitrary byte-offset viewports
/// that may land in the middle of a multi-byte sequence.
/// </summary>
public static class Utf8Utils
{
    /// <summary>
    /// Given a raw byte buffer and an offset within it, adjusts the offset
    /// backwards (up to 3 bytes) to find the start of the UTF-8 sequence
    /// containing the byte at <paramref name="offset"/>.
    /// Returns the adjusted offset (always &lt;= <paramref name="offset"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignToCharBoundary(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length) return offset;

        // A continuation byte has the pattern 10xxxxxx (0x80..0xBF).
        // Walk backwards at most 3 bytes to find a leading byte.
        int rewind = 0;
        while (rewind < 3 && (offset - rewind) > 0) {
            byte b = data[offset - rewind];
            if ((b & 0xC0) != 0x80) // Not a continuation byte — this is the start
                return offset - rewind;
            rewind++;
        }

        // If we rewound 3 and still on continuation, try one more (the lead byte itself)
        if (rewind < 4 && (offset - rewind) >= 0) {
            byte b = data[offset - rewind];
            if ((b & 0xC0) != 0x80)
                return offset - rewind;
        }

        // Malformed — return as-is
        return offset;
    }

    /// <summary>
    /// Decodes the next Rune from the buffer at the given byte position.
    /// Returns the decoded Rune and its byte length (1–4).
    /// On invalid sequences, returns <see cref="Rune.ReplacementChar"/> with length 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Rune Rune, int ByteLength) DecodeRune(ReadOnlySpan<byte> data, int offset)
    {
        if (offset >= data.Length)
            return (Rune.ReplacementChar, 0);

        var status = Rune.DecodeFromUtf8(data[offset..], out Rune rune, out int bytesConsumed);
        if (status == System.Buffers.OperationStatus.Done)
            return (rune, bytesConsumed);

        // Invalid or incomplete — consume 1 byte and emit replacement character
        return (Rune.ReplacementChar, 1);
    }

    /// <summary>
    /// Scans a UTF-8 byte span and counts the number of columns (character cell widths)
    /// each Rune occupies. ASCII printable = 1, tab = <paramref name="tabWidth"/>,
    /// CJK wide characters = 2, control = 1 (rendered as replacement).
    /// Returns the total column count.
    /// </summary>
    public static int MeasureColumns(ReadOnlySpan<byte> data, int tabWidth = 4)
    {
        int cols = 0;
        int pos = 0;
        while (pos < data.Length) {
            var (rune, byteLen) = DecodeRune(data, pos);
            if (byteLen == 0) break;
            cols += RuneColumnWidth(rune, tabWidth);
            pos += byteLen;
        }
        return cols;
    }

    /// <summary>
    /// Returns the display column width of a single Rune.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RuneColumnWidth(Rune rune, int tabWidth = 4)
    {
        int cp = rune.Value;
        if (cp == '\t') return tabWidth;
        if (cp == 0xFEFF) return 0; // BOM / zero-width no-break space
        if (cp < 0x20) return 1; // Control chars rendered as replacement

        // CJK Unified Ideographs and common wide ranges
        if (IsWideCharacter(cp)) return 2;

        return 1;
    }

    /// <summary>
    /// Rough heuristic for East Asian wide characters.
    /// Covers CJK Unified Ideographs, CJK Compatibility, Hangul, and fullwidth forms.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWideCharacter(int codePoint) =>
        codePoint >= 0x1100 && (
          (codePoint <= 0x115F) ||                      // Hangul Jamo
          (codePoint >= 0x2E80 && codePoint <= 0x303E) || // CJK Radicals, Kangxi, CJK Symbols
          (codePoint >= 0x3040 && codePoint <= 0x33BF) || // Hiragana, Katakana, Bopomofo, CJK Compat
          (codePoint >= 0x3400 && codePoint <= 0x4DBF) || // CJK Extension A
          (codePoint >= 0x4E00 && codePoint <= 0xA4CF) || // CJK Unified, Yi
          (codePoint >= 0xAC00 && codePoint <= 0xD7AF) || // Hangul Syllables
          (codePoint >= 0xF900 && codePoint <= 0xFAFF) || // CJK Compatibility Ideographs
          (codePoint >= 0xFE30 && codePoint <= 0xFE6F) || // CJK Compatibility Forms
          (codePoint >= 0xFF01 && codePoint <= 0xFF60) || // Fullwidth Forms
          (codePoint >= 0xFFE0 && codePoint <= 0xFFE6) || // Fullwidth signs
          (codePoint >= 0x20000 && codePoint <= 0x2FA1F)  // CJK Extensions B-F + Compat Supplement
        );
}
