namespace Leviathan.GUI.Helpers;

/// <summary>
/// Pure helper for computing linked-view anchor offsets when switching tabs.
/// </summary>
internal static class ViewAnchorSync
{
    /// <summary>
    /// Captures the source-view anchor offset used for cross-view synchronization.
    /// </summary>
    internal static long CaptureSourceAnchorOffset(
        AppState state,
        ViewMode sourceMode,
        Func<long, long>? csvRowOffsetProvider)
    {
        if (state.Document is null)
            return 0;

        long maxOffset = Math.Max(0, state.FileLength - 1);
        return sourceMode switch {
            ViewMode.Hex => CaptureHexAnchorOffset(state, maxOffset),
            ViewMode.Text => CaptureTextAnchorOffset(state),
            ViewMode.Csv => CaptureCsvAnchorOffset(state, maxOffset, csvRowOffsetProvider),
            _ => 0
        };
    }

    /// <summary>
    /// Maps a captured anchor offset onto the destination view's valid range.
    /// </summary>
    internal static long MapAnchorToTargetOffset(AppState state, ViewMode targetMode, long anchorOffset)
    {
        if (state.Document is null)
            return 0;

        long maxOffset = Math.Max(0, state.FileLength - 1);
        long clamped = Math.Clamp(anchorOffset, 0, maxOffset);
        return targetMode == ViewMode.Text
            ? Math.Clamp(clamped, state.BomLength, state.FileLength)
            : clamped;
    }

    private static long CaptureCsvAnchorOffset(AppState state, long maxOffset, Func<long, long>? csvRowOffsetProvider)
    {
        if (csvRowOffsetProvider is not null) {
            long visibleRows = Math.Max(1, state.VisibleRows);
            long topRow = state.CsvTopRowIndex;
            long cursorRow = state.CsvCursorRow;
            if (cursorRow >= topRow && cursorRow < topRow + visibleRows) {
                long cursorRowOffset = csvRowOffsetProvider(cursorRow);
                if (cursorRowOffset >= 0)
                    return Math.Clamp(cursorRowOffset, 0, maxOffset);
            }

            long topRowOffset = csvRowOffsetProvider(state.CsvTopRowIndex);
            if (topRowOffset >= 0)
                return Math.Clamp(topRowOffset, 0, maxOffset);
        }

        long textLikeFallback = Math.Clamp(state.TextTopOffset, state.BomLength, state.FileLength);
        return Math.Clamp(textLikeFallback, 0, maxOffset);
    }

    private static long CaptureHexAnchorOffset(AppState state, long maxOffset)
    {
        long baseOffset = Math.Clamp(state.HexBaseOffset, 0, maxOffset);
        long cursorOffset = Math.Clamp(state.HexCursorOffset, 0, maxOffset);
        long visibleRows = Math.Max(1, state.VisibleRows);
        long bytesPerRow = Math.Max(1, state.BytesPerRow);
        long visibleEnd = baseOffset + visibleRows * bytesPerRow;

        return cursorOffset >= baseOffset && cursorOffset < visibleEnd
            ? cursorOffset
            : baseOffset;
    }

    private static long CaptureTextAnchorOffset(AppState state)
    {
        long topOffset = Math.Clamp(state.TextTopOffset, state.BomLength, state.FileLength);
        long cursorOffset = Math.Clamp(state.TextCursorOffset, state.BomLength, state.FileLength);
        return cursorOffset >= topOffset ? cursorOffset : topOffset;
    }
}
