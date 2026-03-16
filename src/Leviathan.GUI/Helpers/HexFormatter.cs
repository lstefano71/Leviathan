using System.Runtime.CompilerServices;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Testable hex formatting helpers extracted from HexViewControl.
/// </summary>
public static class HexFormatter
{
    /// <summary>Lookup table for zero-alloc byte-to-hex conversion.</summary>
    private static ReadOnlySpan<byte> HexChars => "0123456789ABCDEF"u8;

    /// <summary>
    /// Formats a byte as two hex characters into the destination span.
    /// Returns 2 (number of chars written).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FormatByte(byte value, Span<char> destination)
    {
        destination[0] = (char)HexChars[value >> 4];
        destination[1] = (char)HexChars[value & 0xF];
        return 2;
    }

    /// <summary>
    /// Formats a long offset as a hex string with the given number of digits.
    /// </summary>
    public static string FormatOffset(long offset, int digits)
    {
        return digits == 16 ? offset.ToString("X16") : offset.ToString("X8");
    }

    /// <summary>
    /// Determines the number of hex digits needed for the address column.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AddressDigits(long fileLength)
    {
        return fileLength > 0xFFFF_FFFFL ? 16 : 8;
    }

    /// <summary>
    /// Parses a single hex digit from a key, returning -1 if not a hex key.
    /// </summary>
    public static int ParseHexDigit(char c)
    {
        return c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => -1
        };
    }
}
