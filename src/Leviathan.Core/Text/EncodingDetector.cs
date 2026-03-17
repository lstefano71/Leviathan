using System.Buffers;
using System.Text;

namespace Leviathan.Core.Text;

/// <summary>
/// Detects the text encoding of a byte sample (typically the first 8 KB of a file).
/// Detection follows a priority chain: BOM → UTF-16 LE heuristic → UTF-8 validation → Windows-1252 fallback.
/// </summary>
public static class EncodingDetector
{
    /// <summary>Minimum sample size required for the UTF-16 LE heuristic (no-BOM path).</summary>
    private const int MinUtf16SampleSize = 8;

    /// <summary>
    /// Ratio threshold of null bytes at odd positions above which the sample is guessed as UTF-16 LE.
    /// </summary>
    private const double Utf16LeNullThreshold = 0.3;

    /// <summary>
    /// Maximum fraction of replacement characters tolerated before rejecting a UTF-8 guess.
    /// </summary>
    private const double Utf8MaxErrorRate = 0.01;

    /// <summary>
    /// Detects the most likely <see cref="TextEncoding"/> for the given byte sample.
    /// </summary>
    /// <param name="sample">
    /// A read-only span of bytes to analyse. Typically the first 8 KB of the file.
    /// An empty span returns <see cref="TextEncoding.Utf8"/> with a BOM length of 0.
    /// </param>
    /// <returns>
    /// A tuple of the detected <see cref="TextEncoding"/> and the length of the BOM prefix
    /// (0 when no BOM was found).
    /// </returns>
    public static (TextEncoding Encoding, int BomLength) Detect(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty) {
            return (TextEncoding.Utf8, 0);
        }

        // 1. BOM check.
        (TextEncoding encoding, int bomLength) = CheckBom(sample);
        if (bomLength > 0) {
            return (encoding, bomLength);
        }

        // 2. UTF-16 LE heuristic (no BOM).
        if (sample.Length >= MinUtf16SampleSize && LooksLikeUtf16Le(sample)) {
            return (TextEncoding.Utf16Le, 0);
        }

        // 3. UTF-8 validation.
        if (IsLikelyUtf8(sample)) {
            return (TextEncoding.Utf8, 0);
        }

        // 4. Fallback — Windows-1252 accepts every byte value.
        return (TextEncoding.Windows1252, 0);
    }

    /// <summary>
    /// Checks whether <paramref name="sample"/> starts with a recognised BOM.
    /// </summary>
    private static (TextEncoding Encoding, int BomLength) CheckBom(ReadOnlySpan<byte> sample)
    {
        // UTF-8 BOM: EF BB BF (check first — 3 bytes is longer than the 2-byte LE BOM).
        if (sample.Length >= 3 &&
            sample[0] == 0xEF &&
            sample[1] == 0xBB &&
            sample[2] == 0xBF) {
            return (TextEncoding.Utf8, 3);
        }

        // UTF-16 LE BOM: FF FE.
        if (sample.Length >= 2 &&
            sample[0] == 0xFF &&
            sample[1] == 0xFE) {
            return (TextEncoding.Utf16Le, 2);
        }

        return (default, 0);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the ratio of <c>0x00</c> bytes at odd indices
    /// exceeds <see cref="Utf16LeNullThreshold"/>, which strongly suggests UTF-16 LE
    /// (ASCII-range characters have a zero high byte at every odd position).
    /// </summary>
    private static bool LooksLikeUtf16Le(ReadOnlySpan<byte> sample)
    {
        int oddCount = 0;
        int nullCount = 0;

        for (int i = 1; i < sample.Length; i += 2) {
            oddCount++;
            if (sample[i] == 0x00) {
                nullCount++;
            }
        }

        return oddCount > 0 && (double)nullCount / oddCount > Utf16LeNullThreshold;
    }

    /// <summary>
    /// Validates the sample as UTF-8 using <see cref="Rune.DecodeFromUtf8"/>.
    /// Pure-ASCII samples (all bytes &lt; 0x80) are accepted as valid UTF-8.
    /// Multi-byte samples are accepted when the replacement-character error rate is below
    /// <see cref="Utf8MaxErrorRate"/>.
    /// </summary>
    private static bool IsLikelyUtf8(ReadOnlySpan<byte> sample)
    {
        int totalRunes = 0;
        int errors = 0;
        bool hasMultiByte = false;

        ReadOnlySpan<byte> remaining = sample;

        while (!remaining.IsEmpty) {
            OperationStatus status = Rune.DecodeFromUtf8(remaining, out Rune rune, out int bytesConsumed);
            totalRunes++;

            if (status != OperationStatus.Done) {
                errors++;
                // Advance at least one byte to avoid infinite loops on invalid data.
                bytesConsumed = bytesConsumed > 0 ? bytesConsumed : 1;
            } else if (bytesConsumed > 1) {
                hasMultiByte = true;
            }

            remaining = remaining.Slice(bytesConsumed);
        }

        // Pure ASCII — valid UTF-8 by definition.
        if (!hasMultiByte && errors == 0) {
            return true;
        }

        // Multi-byte sequences present: accept when error rate is below threshold.
        if (hasMultiByte && totalRunes > 0) {
            double errorRate = (double)errors / totalRunes;
            return errorRate < Utf8MaxErrorRate;
        }

        return false;
    }
}
