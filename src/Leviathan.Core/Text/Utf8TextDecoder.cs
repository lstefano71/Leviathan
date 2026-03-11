using System.Runtime.CompilerServices;
using System.Text;

namespace Leviathan.Core.Text;

/// <summary>
/// UTF-8 implementation of <see cref="ITextDecoder"/>.
/// Delegates decoding and alignment to the static <see cref="Utf8Utils"/> helpers.
/// </summary>
public sealed class Utf8TextDecoder : ITextDecoder
{
    /// <inheritdoc />
    public TextEncoding Encoding => TextEncoding.Utf8;

    /// <inheritdoc />
    public int MinCharBytes => 1;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Rune Rune, int ByteLength) DecodeRune(ReadOnlySpan<byte> data, int offset)
        => Utf8Utils.DecodeRune(data, offset);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AlignToCharBoundary(ReadOnlySpan<byte> data, int offset)
        => Utf8Utils.AlignToCharBoundary(data, offset);

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int EncodeRune(Rune rune, Span<byte> output)
    {
        if (!rune.TryEncodeToUtf8(output, out int written))
        {
            Rune.ReplacementChar.TryEncodeToUtf8(output, out written);
        }

        return written;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsNewline(ReadOnlySpan<byte> data, int offset, out int newlineByteLength)
    {
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
        => System.Text.Encoding.UTF8.GetBytes(text);
}
