using Leviathan.GUI.Widgets;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests CSV detail panel refresh-key signature logic.
/// </summary>
public sealed class CsvDetailPanelTests
{
    [Fact]
    public void ComputeHiddenColumnsSignature_SameSetDifferentOrder_Matches()
    {
        HashSet<int> first = [1, 4, 7];
        HashSet<int> second = [7, 1, 4];

        int firstSignature = CsvDetailPanel.ComputeHiddenColumnsSignature(first);
        int secondSignature = CsvDetailPanel.ComputeHiddenColumnsSignature(second);

        Assert.Equal(firstSignature, secondSignature);
    }

    [Fact]
    public void ComputeHiddenColumnsSignature_DifferentSets_Differs()
    {
        HashSet<int> first = [1, 4, 7];
        HashSet<int> second = [1, 4, 8];

        int firstSignature = CsvDetailPanel.ComputeHiddenColumnsSignature(first);
        int secondSignature = CsvDetailPanel.ComputeHiddenColumnsSignature(second);

        Assert.NotEqual(firstSignature, secondSignature);
    }
}
