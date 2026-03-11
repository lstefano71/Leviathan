using System.Runtime.CompilerServices;
using System.Text;

namespace Leviathan.Core.Text;

/// <summary>
/// Windows code page 1252 implementation of <see cref="ITextDecoder"/>.
/// Single-byte encoding where every byte maps to exactly one Unicode code point.
/// Bytes 0x80–0x9F use a special mapping that distinguishes W1252 from ISO-8859-1.
/// </summary>
public sealed class Windows1252TextDecoder : ITextDecoder
{
    /// <summary>
    /// Maps bytes 0x80–0x9F to their Unicode code points.
    /// Undefined positions (0x81, 0x8D, 0x8F, 0x90, 0x9D) use U+FFFD.
    /// </summary>
    private static ReadOnlySpan<char> HighMap =>
    [
        '\u20AC', '\uFFFD', '\u201A', '\u0192', // 0x80-0x83
        '\u201E', '\u2026', '\u2020', '\u2021', // 0x84-0x87
        '\u02C6', '\u2030', '\u0160', '\u2039', // 0x88-0x8B
        '\u0152', '\uFFFD', '\u017D', '\uFFFD', // 0x8C-0x8F
        '\uFFFD', '\u2018', '\u2019', '\u201C', // 0x90-0x93
        '\u201D', '\u2022', '\u2013', '\u2014', // 0x94-0x97
        '\u02DC', '\u2122', '\u0161', '\u203A', // 0x98-0x9B
        '\u0153', '\uFFFD', '\u017E', '\u0178', // 0x9C-0x9F
    ];

    /// <inheritdoc />
    public TextEncoding Encoding => TextEncoding.Windows1252;

    /// <inheritdoc />
    public int MinCharBytes => 1;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Rune Rune, int ByteLength) DecodeRune(ReadOnlySpan<byte> data, int offset)
    {
        if (offset >= data.Length)
        {
            return (Rune.ReplacementChar, 0);
        }

        byte b = data[offset];

        if (b < 0x80)
        {
            return (new Rune(b), 1);
        }

        if (b <= 0x9F)
        {
            char ch = HighMap[b - 0x80];
            return (new Rune(ch), 1);
        }

        return (new Rune(b), 1);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AlignToCharBoundary(ReadOnlySpan<byte> data, int offset)
        => offset;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int EncodeRune(Rune rune, Span<byte> output)
    {
        int cp = rune.Value;

        if (cp < 0x80)
        {
            output[0] = (byte)cp;
            return 1;
        }

        if (cp >= 0xA0 && cp <= 0xFF)
        {
            output[0] = (byte)cp;
            return 1;
        }

        // Reverse lookup through the 0x80–0x9F special range.
        ReadOnlySpan<char> map = HighMap;
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] == cp)
            {
                output[0] = (byte)(0x80 + i);
                return 1;
            }
        }

        output[0] = (byte)'?';
        return 1;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsNewline(ReadOnlySpan<byte> data, int offset, out int newlineByteLength)
    {
        if (offset >= data.Length)
        {
            newlineByteLength = 0;
            return false;
        }

        byte b = data[offset];

        if (b == 0x0A)
        {
            newlineByteLength = 1;
            return true;
        }

        if (b == 0x0D)
        {
            newlineByteLength = 1;
            return true;
        }

        newlineByteLength = 0;
        return false;
    }

    /// <inheritdoc />
    public byte[] EncodeString(string text)
    {
        byte[] result = new byte[text.Length];
        Span<byte> single = stackalloc byte[1];

        for (int i = 0; i < text.Length; i++)
        {
            EncodeRune(new Rune(text[i]), single);
            result[i] = single[0];
        }

        return result;
    }
}
