using Leviathan.GUI.Views;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests selection state transitions for hex pointer interactions.
/// </summary>
public sealed class HexViewControlSelectionTests
{
    [Fact]
    public void ResolvePointerSelectionAnchor_NoShift_UsesHitOffset()
    {
        long anchor = HexViewControl.ResolvePointerSelectionAnchor(20, 40, 55, shiftPressed: false);
        Assert.Equal(55, anchor);
    }

    [Fact]
    public void ResolvePointerSelectionAnchor_ShiftWithExistingAnchor_PreservesAnchor()
    {
        long anchor = HexViewControl.ResolvePointerSelectionAnchor(20, 40, 55, shiftPressed: true);
        Assert.Equal(20, anchor);
    }

    [Fact]
    public void ResolvePointerSelectionAnchor_ShiftWithoutAnchor_UsesCursor()
    {
        long anchor = HexViewControl.ResolvePointerSelectionAnchor(-1, 40, 55, shiftPressed: true);
        Assert.Equal(40, anchor);
    }

    [Fact]
    public void ResolvePointerSelectionAnchor_ShiftWithoutAnchorOrCursor_UsesHitOffset()
    {
        long anchor = HexViewControl.ResolvePointerSelectionAnchor(-1, -1, 55, shiftPressed: true);
        Assert.Equal(55, anchor);
    }

    [Fact]
    public void ShouldClearSelectionAfterPointerRelease_NoShiftAndNoDrag_ReturnsTrue()
    {
        bool shouldClear = HexViewControl.ShouldClearSelectionAfterPointerRelease(shiftPressed: false, selectionExtended: false);
        Assert.True(shouldClear);
    }

    [Fact]
    public void ShouldClearSelectionAfterPointerRelease_DragSelection_ReturnsFalse()
    {
        bool shouldClear = HexViewControl.ShouldClearSelectionAfterPointerRelease(shiftPressed: false, selectionExtended: true);
        Assert.False(shouldClear);
    }

    [Fact]
    public void ShouldClearSelectionAfterPointerRelease_ShiftSelection_ReturnsFalse()
    {
        bool shouldClear = HexViewControl.ShouldClearSelectionAfterPointerRelease(shiftPressed: true, selectionExtended: false);
        Assert.False(shouldClear);
    }
}
