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
}
