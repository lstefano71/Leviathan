using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for stable theme command labels used by menu and command palette.
/// </summary>
public sealed class ThemeCommandsTests
{
    [Fact]
    public void UtilityCommandOrder_ReturnsExpectedStableEntries()
    {
        IReadOnlyList<string> commands = ThemeCommands.UtilityCommandOrder;

        Assert.Equal(3, commands.Count);
        Assert.Equal("Import Theme...", commands[0]);
        Assert.Equal("Export Current Theme...", commands[1]);
        Assert.Equal("Theme Editor...", commands[2]);
    }

    [Fact]
    public void UtilityCommandOrder_HasNoDuplicates()
    {
        IReadOnlyList<string> commands = ThemeCommands.UtilityCommandOrder;

        int distinctCount = commands.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(commands.Count, distinctCount);
    }
}
