using System.Runtime.CompilerServices;
using System.Text;

namespace Leviathan.Core.Text;

/// <summary>
/// UTF-16 Little-Endian implementation of <see cref="ITextDecoder"/>.
/// Handles BMP characters (2 bytes) and supplementary plane characters via surrogate pairs (4 bytes).
/// </summary>
public sealed class Utf16LeTextDecoder : ITextDecoder
{
  /// <inheritdoc />
  public TextEncoding Encoding => TextEncoding.Utf16Le;

  /// <inheritdoc />
  public int MinCharBytes => 2;

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public (Rune Rune, int ByteLength) DecodeRune(ReadOnlySpan<byte> data, int offset)
  {
    if (offset + 2 > data.Length) {
      return (Rune.ReplacementChar, data.Length - offset);
    }

    ushort code = (ushort)(data[offset] | (data[offset + 1] << 8));

    // High surrogate — expect a trailing low surrogate for a 4-byte pair.
    if (code >= 0xD800 && code <= 0xDBFF) {
      if (offset + 4 > data.Length) {
        return (Rune.ReplacementChar, 2);
      }

      ushort low = (ushort)(data[offset + 2] | (data[offset + 3] << 8));

      if (low >= 0xDC00 && low <= 0xDFFF) {
        int cp = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
        return (new Rune(cp), 4);
      }

      return (Rune.ReplacementChar, 2);
    }

    // Lone low surrogate — invalid without a preceding high surrogate.
    if (code >= 0xDC00 && code <= 0xDFFF) {
      return (Rune.ReplacementChar, 2);
    }

    return (new Rune(code), 2);
  }

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int AlignToCharBoundary(ReadOnlySpan<byte> data, int offset)
  {
    // Snap to an even byte boundary (each code unit is 2 bytes).
    offset &= ~1;

    // If we landed on a low surrogate, back up to the high surrogate.
    if (offset >= 2 && offset + 1 < data.Length) {
      ushort code = (ushort)(data[offset] | (data[offset + 1] << 8));

      if (code >= 0xDC00 && code <= 0xDFFF) {
        offset -= 2;
      }
    }

    return offset;
  }

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int EncodeRune(Rune rune, Span<byte> output)
  {
    Span<char> chars = stackalloc char[2];

    if (!rune.TryEncodeToUtf16(chars, out int charsWritten)) {
      // Replacement character U+FFFD as UTF-16 LE.
      output[0] = 0xFD;
      output[1] = 0xFF;
      return 2;
    }

    output[0] = (byte)(chars[0] & 0xFF);
    output[1] = (byte)(chars[0] >> 8);

    if (charsWritten == 2) {
      output[2] = (byte)(chars[1] & 0xFF);
      output[3] = (byte)(chars[1] >> 8);
      return 4;
    }

    return 2;
  }

  /// <inheritdoc />
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool IsNewline(ReadOnlySpan<byte> data, int offset, out int newlineByteLength)
  {
    if (offset + 2 > data.Length) {
      newlineByteLength = 0;
      return false;
    }

    ushort code = (ushort)(data[offset] | (data[offset + 1] << 8));

    if (code == 0x000A) {
      newlineByteLength = 2;
      return true;
    }

    if (code == 0x000D) {
      newlineByteLength = 2;
      return true;
    }

    newlineByteLength = 0;
    return false;
  }

  /// <inheritdoc />
  public byte[] EncodeString(string text)
      => System.Text.Encoding.Unicode.GetBytes(text);
}
