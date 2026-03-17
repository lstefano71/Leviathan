using System.Runtime.CompilerServices;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Hit-testing helpers for translating pixel coordinates to byte offsets
/// in the hex and text view controls.
/// </summary>
public static class HitTestHelper
{
    /// <summary>
    /// Hit-tests a point in the hex view, returning the byte offset or -1.
    /// </summary>
    public static long HexHitTest(
        double pointX, double pointY,
        double charWidth, double lineHeight,
        int bytesPerRow, int visibleRows,
        long baseOffset, long fileLength)
    {
        if (pointY < 0) return -1;
        int row = (int)(pointY / lineHeight);
        if (row < 0 || row >= visibleRows) return -1;

        int addressDigits = HexFormatter.AddressDigits(fileLength);
        double addressWidth = (addressDigits + 2) * charWidth;

        double hexX = pointX - addressWidth;
        if (hexX >= 0) {
            int groupCount = (bytesPerRow + 7) / 8;
            double totalHexWidth = (bytesPerRow * 3 + groupCount) * charWidth;

            if (hexX < totalHexWidth) {
                int approxCol = (int)(hexX / (3 * charWidth));
                approxCol = Math.Clamp(approxCol, 0, bytesPerRow - 1);
                long offset = baseOffset + (long)row * bytesPerRow + approxCol;
                return Math.Min(offset, fileLength - 1);
            }
        }

        return baseOffset + (long)row * bytesPerRow;
    }

    /// <summary>
    /// Determines the hex column width in pixels: (3 * bytesPerRow + groupCount) * charWidth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double HexColumnsWidth(int bytesPerRow, double charWidth)
    {
        int groupCount = (bytesPerRow + 7) / 8;
        return (bytesPerRow * 3 + groupCount) * charWidth;
    }

    /// <summary>
    /// Determines the address column width: (addressDigits + 2) * charWidth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double AddressColumnWidth(long fileLength, double charWidth)
    {
        int digits = HexFormatter.AddressDigits(fileLength);
        return (digits + 2) * charWidth;
    }
}
