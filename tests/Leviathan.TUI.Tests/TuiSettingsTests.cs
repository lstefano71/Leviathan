namespace Leviathan.TUI.Tests;

public class TuiSettingsTests
{
    [Fact]
    public void AddRecent_NewFile_InsertsAtFront()
    {
        TuiSettings settings = new();
        settings.RecentFiles.AddRange(["a.txt", "b.txt", "c.txt"]);

        settings.AddRecent("d.txt");

        Assert.Equal("d.txt", settings.RecentFiles[0]);
        Assert.Equal(4, settings.RecentFiles.Count);
    }

    [Fact]
    public void AddRecent_ExistingFile_DoesNotMoveToFront()
    {
        TuiSettings settings = new();
        settings.RecentFiles.AddRange(["a.txt", "b.txt", "c.txt"]);

        settings.AddRecent("b.txt");

        // "b.txt" should remain at index 1, not move to index 0
        Assert.Equal("a.txt", settings.RecentFiles[0]);
        Assert.Equal("b.txt", settings.RecentFiles[1]);
        Assert.Equal("c.txt", settings.RecentFiles[2]);
        Assert.Equal(3, settings.RecentFiles.Count);
    }

    [Fact]
    public void AddRecent_CapsAtMax()
    {
        TuiSettings settings = new();
        for (int i = 0; i < 10; i++)
            settings.RecentFiles.Add($"file{i}.txt");

        settings.AddRecent("new.txt");

        Assert.Equal(10, settings.RecentFiles.Count);
        Assert.Equal("new.txt", settings.RecentFiles[0]);
    }
}
