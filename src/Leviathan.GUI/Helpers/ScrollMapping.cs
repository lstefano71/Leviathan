using System.Runtime.CompilerServices;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Maps between virtual scroll positions and row indices for files larger
/// than can be represented with standard scroll range.
/// </summary>
public static class ScrollMapping
{
    /// <summary>Maximum virtual pixel height for scrollbar mapping.</summary>
    public const double MaxVirtualHeight = 10_000_000.0;

    /// <summary>
    /// Maps a scroll position (0..maxScroll) to a row index (0..totalRows-1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ScrollToRow(double scrollValue, double maxScroll, long totalRows)
    {
        if (maxScroll <= 0 || totalRows <= 0)
            return 0;

        double fraction = scrollValue / maxScroll;
        return (long)(fraction * (totalRows - 1));
    }

    /// <summary>
    /// Maps a row index (0..totalRows-1) to a scroll position (0..maxScroll).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double RowToScroll(long row, double maxScroll, long totalRows)
    {
        if (totalRows <= 1)
            return 0;

        double fraction = (double)row / (totalRows - 1);
        return fraction * maxScroll;
    }

    /// <summary>
    /// Computes the virtual height for the scrollbar, capping at MaxVirtualHeight.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ComputeVirtualHeight(long totalRows, double lineHeight)
    {
        double naturalHeight = totalRows * lineHeight;
        return Math.Min(naturalHeight, MaxVirtualHeight);
    }

    /// <summary>
    /// Maps a byte offset to a hex view row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long OffsetToRow(long offset, int bytesPerRow)
    {
        return offset / bytesPerRow;
    }

    /// <summary>
    /// Maps a row back to a byte offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long RowToOffset(long row, int bytesPerRow)
    {
        return row * bytesPerRow;
    }
}
