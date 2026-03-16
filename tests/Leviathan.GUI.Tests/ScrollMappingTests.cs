using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for scroll mapping helpers.
/// </summary>
public sealed class ScrollMappingTests
{
    [Fact]
    public void ScrollToRow_Beginning_ReturnsZero()
    {
        long row = ScrollMapping.ScrollToRow(0.0, 1000.0, 5000);
        Assert.Equal(0, row);
    }

    [Fact]
    public void ScrollToRow_End_ReturnsLastRow()
    {
        long row = ScrollMapping.ScrollToRow(1000.0, 1000.0, 5000);
        Assert.Equal(4999, row);
    }

    [Fact]
    public void ScrollToRow_Middle_ReturnsMiddleRow()
    {
        long row = ScrollMapping.ScrollToRow(500.0, 1000.0, 5000);
        Assert.Equal(2499, row);
    }

    [Fact]
    public void ScrollToRow_ZeroMaxScroll_ReturnsZero()
    {
        long row = ScrollMapping.ScrollToRow(0.0, 0.0, 5000);
        Assert.Equal(0, row);
    }

    [Fact]
    public void RowToScroll_Beginning_ReturnsZero()
    {
        double scroll = ScrollMapping.RowToScroll(0, 1000.0, 5000);
        Assert.Equal(0.0, scroll, 0.001);
    }

    [Fact]
    public void RowToScroll_End_ReturnsMax()
    {
        double scroll = ScrollMapping.RowToScroll(4999, 1000.0, 5000);
        Assert.Equal(1000.0, scroll, 0.001);
    }

    [Fact]
    public void ComputeVirtualHeight_Small_ReturnsNatural()
    {
        double height = ScrollMapping.ComputeVirtualHeight(100, 16.0);
        Assert.Equal(1600.0, height);
    }

    [Fact]
    public void ComputeVirtualHeight_Large_CapsAtMax()
    {
        double height = ScrollMapping.ComputeVirtualHeight(1_000_000, 16.0);
        Assert.Equal(ScrollMapping.MaxVirtualHeight, height);
    }

    [Fact]
    public void OffsetToRow_Aligned_ReturnsCorrect()
    {
        Assert.Equal(2, ScrollMapping.OffsetToRow(32, 16));
    }

    [Fact]
    public void OffsetToRow_Unaligned_Floors()
    {
        Assert.Equal(2, ScrollMapping.OffsetToRow(40, 16));
    }

    [Fact]
    public void RowToOffset_ReturnsCorrect()
    {
        Assert.Equal(32, ScrollMapping.RowToOffset(2, 16));
    }

    [Fact]
    public void Roundtrip_ScrollToRowToScroll()
    {
        long totalRows = 100_000;
        double maxScroll = 10_000.0;

        for (int i = 0; i <= 100; i++)
        {
            double scroll = i * 100.0;
            long row = ScrollMapping.ScrollToRow(scroll, maxScroll, totalRows);
            double backScroll = ScrollMapping.RowToScroll(row, maxScroll, totalRows);
            Assert.InRange(backScroll, scroll - 1.0, scroll + 1.0);
        }
    }
}
