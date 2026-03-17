using Leviathan.GUI.Views;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests selection state transitions for text pointer interactions.
/// </summary>
public sealed class TextViewControlSelectionTests
{
    [Fact]
    public void ResolvePointerSelectionAnchor_NoShift_UsesHitOffset()
    {
        long anchor = TextViewControl.ResolvePointerSelectionAnchor(20, 40, 55, shiftPressed: false);
        Assert.Equal(55, anchor);
    }

    [Fact]
    public void ResolvePointerSelectionAnchor_ShiftWithExistingAnchor_PreservesAnchor()
    {
        long anchor = TextViewControl.ResolvePointerSelectionAnchor(20, 40, 55, shiftPressed: true);
        Assert.Equal(20, anchor);
    }

    [Fact]
    public void ResolvePointerSelectionAnchor_ShiftWithoutAnchor_UsesCursor()
    {
        long anchor = TextViewControl.ResolvePointerSelectionAnchor(-1, 40, 55, shiftPressed: true);
        Assert.Equal(40, anchor);
    }

    [Fact]
    public void ResolvePointerSelectionAnchor_ShiftWithoutAnchorOrCursor_UsesHitOffset()
    {
        long anchor = TextViewControl.ResolvePointerSelectionAnchor(-1, -1, 55, shiftPressed: true);
        Assert.Equal(55, anchor);
    }

    [Fact]
    public void ShouldClearSelectionAfterPointerRelease_NoShiftAndNoDrag_ReturnsTrue()
    {
        bool shouldClear = TextViewControl.ShouldClearSelectionAfterPointerRelease(shiftPressed: false, selectionExtended: false);
        Assert.True(shouldClear);
    }

    [Fact]
    public void ShouldClearSelectionAfterPointerRelease_DragSelection_ReturnsFalse()
    {
        bool shouldClear = TextViewControl.ShouldClearSelectionAfterPointerRelease(shiftPressed: false, selectionExtended: true);
        Assert.False(shouldClear);
    }

    [Fact]
    public void ShouldClearSelectionAfterPointerRelease_ShiftSelection_ReturnsFalse()
    {
        bool shouldClear = TextViewControl.ShouldClearSelectionAfterPointerRelease(shiftPressed: true, selectionExtended: false);
        Assert.False(shouldClear);
    }

    [Fact]
    public void TryGetSelectionDeleteRange_ValidRange_ReturnsInclusiveLength()
    {
        bool ok = TextViewControl.TryGetSelectionDeleteRange(10, 14, out long start, out long length);
        Assert.True(ok);
        Assert.Equal(10, start);
        Assert.Equal(5, length);
    }

    [Fact]
    public void TryGetSelectionDeleteRange_InvalidRange_ReturnsFalse()
    {
        bool ok = TextViewControl.TryGetSelectionDeleteRange(-1, 2, out long start, out long length);
        Assert.False(ok);
        Assert.Equal(0, start);
        Assert.Equal(0, length);
    }
}
