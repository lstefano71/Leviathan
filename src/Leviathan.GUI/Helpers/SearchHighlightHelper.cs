using Leviathan.Core.Search;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Shared helpers for search match highlighting across all view controls.
/// </summary>
internal static class SearchHighlightHelper
{
    /// <summary>
    /// Binary-searches the sorted <paramref name="matches"/> list to find
    /// the index of the first match whose end offset (Offset + Length - 1)
    /// is at or after <paramref name="startOffset"/>.
    /// Returns <c>matches.Count</c> if no such match exists.
    /// </summary>
    internal static int BinarySearchFirstMatch(List<SearchResult> matches, long startOffset)
    {
        int lo = 0, hi = matches.Count - 1;
        int result = matches.Count;
        while (lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            long mEnd = matches[mid].Offset + matches[mid].Length - 1;
            if (mEnd >= startOffset) {
                result = mid;
                hi = mid - 1;
            } else {
                lo = mid + 1;
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the index of the first match whose start offset is at or after <paramref name="anchorOffset"/>.
    /// Returns <c>-1</c> when no such match exists.
    /// </summary>
    internal static int FindFirstMatchAtOrAfterOffset(List<SearchResult> matches, long anchorOffset)
    {
        int lo = 0, hi = matches.Count - 1;
        int result = -1;
        while (lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            long midOffset = matches[mid].Offset;
            if (midOffset >= anchorOffset) {
                result = mid;
                hi = mid - 1;
            } else {
                lo = mid + 1;
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the index of the match whose start offset is closest to <paramref name="anchorOffset"/>.
    /// When two candidates are equidistant, the forward (higher offset) match is preferred.
    /// Returns <c>-1</c> when <paramref name="matches"/> is empty.
    /// </summary>
    internal static int FindClosestMatchIndexByOffset(List<SearchResult> matches, long anchorOffset)
    {
        if (matches.Count == 0)
            return -1;

        int lo = 0, hi = matches.Count - 1;
        int firstAtOrAfter = matches.Count;
        while (lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            long midOffset = matches[mid].Offset;
            if (midOffset >= anchorOffset) {
                firstAtOrAfter = mid;
                hi = mid - 1;
            } else {
                lo = mid + 1;
            }
        }

        if (firstAtOrAfter <= 0)
            return 0;
        if (firstAtOrAfter >= matches.Count)
            return matches.Count - 1;

        int previous = firstAtOrAfter - 1;
        int next = firstAtOrAfter;
        long previousDistance = Math.Abs(anchorOffset - matches[previous].Offset);
        long nextDistance = Math.Abs(matches[next].Offset - anchorOffset);
        return nextDistance <= previousDistance ? next : previous;
    }
}
