using Leviathan.Core.Search;
using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for search-highlight helper nearest-match selection.
/// </summary>
public sealed class SearchHighlightHelperTests
{
    [Fact]
    public void FindClosestMatchIndexByOffset_EmptyList_ReturnsMinusOne()
    {
        List<SearchResult> matches = [];
        int index = SearchHighlightHelper.FindClosestMatchIndexByOffset(matches, 100);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void FindClosestMatchIndexByOffset_AnchorBeforeAllMatches_ReturnsFirst()
    {
        List<SearchResult> matches = [
            new SearchResult(100, 3),
            new SearchResult(220, 2),
            new SearchResult(380, 4)
        ];

        int index = SearchHighlightHelper.FindClosestMatchIndexByOffset(matches, 10);
        Assert.Equal(0, index);
    }

    [Fact]
    public void FindClosestMatchIndexByOffset_AnchorAfterAllMatches_ReturnsLast()
    {
        List<SearchResult> matches = [
            new SearchResult(100, 3),
            new SearchResult(220, 2),
            new SearchResult(380, 4)
        ];

        int index = SearchHighlightHelper.FindClosestMatchIndexByOffset(matches, 1000);
        Assert.Equal(2, index);
    }

    [Fact]
    public void FindClosestMatchIndexByOffset_AnchorBetweenMatches_ReturnsNearest()
    {
        List<SearchResult> matches = [
            new SearchResult(100, 3),
            new SearchResult(220, 2),
            new SearchResult(380, 4)
        ];

        int index = SearchHighlightHelper.FindClosestMatchIndexByOffset(matches, 260);
        Assert.Equal(1, index);
    }

    [Fact]
    public void FindClosestMatchIndexByOffset_EqualDistance_PrefersForwardMatch()
    {
        List<SearchResult> matches = [
            new SearchResult(100, 1),
            new SearchResult(200, 1),
            new SearchResult(300, 1)
        ];

        int index = SearchHighlightHelper.FindClosestMatchIndexByOffset(matches, 250);
        Assert.Equal(2, index);
    }
}
