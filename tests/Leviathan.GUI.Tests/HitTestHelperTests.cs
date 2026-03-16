using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for hit-testing helpers.
/// </summary>
public sealed class HitTestHelperTests
{
    private const double CharWidth = 8.0;
    private const double LineHeight = 16.0;
    private const int BytesPerRow = 16;
    private const int VisibleRows = 40;
    private const long BaseOffset = 0;
    private const long FileLength = 1024;

    [Fact]
    public void HexHitTest_NegativeY_ReturnsNegative()
    {
        long result = HitTestHelper.HexHitTest(100, -5, CharWidth, LineHeight,
            BytesPerRow, VisibleRows, BaseOffset, FileLength);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void HexHitTest_BeyondVisibleRows_ReturnsNegative()
    {
        long result = HitTestHelper.HexHitTest(100, VisibleRows * LineHeight + 5, CharWidth, LineHeight,
            BytesPerRow, VisibleRows, BaseOffset, FileLength);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void HexHitTest_FirstRow_ReturnsBaseOffset()
    {
        // Click in the first row, hex area
        double addressWidth = HitTestHelper.AddressColumnWidth(FileLength, CharWidth);
        long result = HitTestHelper.HexHitTest(addressWidth + CharWidth, 5, CharWidth, LineHeight,
            BytesPerRow, VisibleRows, BaseOffset, FileLength);
        Assert.True(result >= 0);
        Assert.True(result < BytesPerRow);
    }

    [Fact]
    public void HexHitTest_SecondRow_ReturnsOffset()
    {
        double addressWidth = HitTestHelper.AddressColumnWidth(FileLength, CharWidth);
        long result = HitTestHelper.HexHitTest(addressWidth + CharWidth, LineHeight + 1, CharWidth, LineHeight,
            BytesPerRow, VisibleRows, BaseOffset, FileLength);
        Assert.True(result >= BytesPerRow);
        Assert.True(result < 2 * BytesPerRow);
    }

    [Fact]
    public void HexColumnsWidth_16Bytes_ReturnsCorrect()
    {
        // 16 bytes * 3 chars + 2 groups = 50 chars
        double width = HitTestHelper.HexColumnsWidth(16, CharWidth);
        Assert.Equal(50 * CharWidth, width);
    }

    [Fact]
    public void AddressColumnWidth_SmallFile_Returns10Chars()
    {
        // 8 digits + 2 padding = 10
        double width = HitTestHelper.AddressColumnWidth(1024, CharWidth);
        Assert.Equal(10 * CharWidth, width);
    }

    [Fact]
    public void AddressColumnWidth_LargeFile_Returns18Chars()
    {
        // 16 digits + 2 padding = 18
        double width = HitTestHelper.AddressColumnWidth(0x1_0000_0000L, CharWidth);
        Assert.Equal(18 * CharWidth, width);
    }
}
