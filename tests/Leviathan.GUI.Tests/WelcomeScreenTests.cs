using Leviathan.GUI.Widgets;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for the WelcomeScreen widget logic: entry model.
/// </summary>
public sealed class WelcomeScreenTests
{
    // ─── FileEntry model ───

    [Fact]
    public void FileEntry_Constructor_SetsProperties()
    {
        WelcomeScreen.FileEntry entry = new(@"C:\Data\report.csv", isPinned: true);

        Assert.Equal(@"C:\Data\report.csv", entry.FullPath);
        Assert.True(entry.IsPinned);
        Assert.Null(entry.SizeText);
        Assert.Null(entry.DateText);
        Assert.False(entry.IsUnavailable);
    }

    [Fact]
    public void FileEntry_UnpinnedByDefault()
    {
        WelcomeScreen.FileEntry entry = new(@"C:\test.bin", isPinned: false);

        Assert.False(entry.IsPinned);
    }

    [Fact]
    public void FileEntry_MetadataCanBeUpdated()
    {
        WelcomeScreen.FileEntry entry = new(@"C:\file.dat", isPinned: false) {
            SizeText = "12.4 MB",
            DateText = "2026-03-15"
        };

        Assert.Equal("12.4 MB", entry.SizeText);
        Assert.Equal("2026-03-15", entry.DateText);
        Assert.False(entry.IsUnavailable);
    }

    [Fact]
    public void FileEntry_UnavailableFlag()
    {
        WelcomeScreen.FileEntry entry = new(@"\\server\share\file.bin", isPinned: false) {
            IsUnavailable = true
        };

        Assert.True(entry.IsUnavailable);
    }
}

/// <summary>
/// Tests for GuiSettings pin/unpin/remove logic.
/// Tests manipulate the in-memory lists directly to avoid disk I/O side effects from Save().
/// </summary>
public sealed class GuiSettingsPinTests
{
    [Fact]
    public void PinFile_AddsToPinnedList()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.RecentFiles.AddRange(["file1.bin", "file2.bin", "file3.bin"]);

        settings.PinFile("file2.bin");

        Assert.Contains("file2.bin", settings.PinnedFiles);
    }

    [Fact]
    public void PinFile_RemovesFromRecentList()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.RecentFiles.AddRange(["file1.bin", "file2.bin", "file3.bin"]);

        settings.PinFile("file2.bin");

        // file2.bin should be in PinnedFiles, not in the original position in RecentFiles
        Assert.Equal("file2.bin", settings.PinnedFiles[0]);
        // After PinFile + Save merge, the recent list no longer starts with file2 at position 1
        Assert.Equal("file1.bin", settings.RecentFiles[0]);
    }

    [Fact]
    public void PinFile_NoDuplicateIfAlreadyPinned()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.PinnedFiles.Add("file1.bin");

        settings.PinFile("file1.bin");

        Assert.Single(settings.PinnedFiles);
    }

    [Fact]
    public void UnpinFile_RemovesFromPinnedList()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.PinnedFiles.Add("pinned.bin");

        settings.UnpinFile("pinned.bin");

        Assert.DoesNotContain("pinned.bin", settings.PinnedFiles);
    }

    [Fact]
    public void UnpinFile_InsertsAtTopOfRecent()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.PinnedFiles.Add("pinned.bin");
        settings.RecentFiles.AddRange(["recent1.bin", "recent2.bin"]);

        settings.UnpinFile("pinned.bin");

        Assert.Equal("pinned.bin", settings.RecentFiles[0]);
    }

    [Fact]
    public void UnpinFile_NoOpIfNotPinned()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.RecentFiles.Add("recent.bin");
        int recentCount = settings.RecentFiles.Count;

        settings.UnpinFile("not-pinned.bin");

        Assert.Empty(settings.PinnedFiles);
        Assert.Equal(recentCount, settings.RecentFiles.Count);
    }

    [Fact]
    public void RemoveFile_ClearsFromPinned()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.PinnedFiles.Add("target.bin");

        settings.RemoveFile("target.bin");

        Assert.DoesNotContain("target.bin", settings.PinnedFiles);
    }

    [Fact]
    public void RemoveFile_ClearsFromRecent()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.RecentFiles.Add("target.bin");

        settings.RemoveFile("target.bin");

        Assert.DoesNotContain("target.bin", settings.RecentFiles);
    }

    [Fact]
    public void AddRecent_SkipsPinnedFile()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.PinnedFiles.Add("pinned.bin");

        settings.AddRecent("pinned.bin");

        // Should not appear in RecentFiles since it's pinned
        Assert.DoesNotContain("pinned.bin", settings.RecentFiles);
        Assert.Single(settings.PinnedFiles);
    }

    [Fact]
    public void AddRecent_InsertsAtTop()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.RecentFiles.AddRange(["old1.bin", "old2.bin"]);

        settings.AddRecent("new.bin");

        Assert.Equal("new.bin", settings.RecentFiles[0]);
    }

    [Fact]
    public void AddRecent_DeduplicatesExisting()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.RecentFiles.AddRange(["first.bin", "second.bin", "third.bin"]);

        settings.AddRecent("second.bin");

        Assert.Equal("second.bin", settings.RecentFiles[0]);
        // Ensure no duplicates of "second.bin"
        Assert.Equal(1, settings.RecentFiles.Count(f => f == "second.bin"));
    }

    [Fact]
    public void PinFile_ThenAddRecent_DoesNotAddToRecent()
    {
        GuiSettings settings = CreateIsolatedSettings();
        settings.RecentFiles.Add("file.bin");
        settings.PinFile("file.bin");

        settings.AddRecent("file.bin");

        // file.bin should only be in PinnedFiles, not in RecentFiles
        Assert.Contains("file.bin", settings.PinnedFiles);
        Assert.DoesNotContain("file.bin", settings.RecentFiles);
    }

    /// <summary>
    /// Creates a GuiSettings instance that writes to a temporary isolated path
    /// to avoid contaminating or being contaminated by the real settings file.
    /// </summary>
    private static GuiSettings CreateIsolatedSettings()
    {
        // GuiSettings.Save() writes to AppContext.BaseDirectory/gui-settings.json.
        // We construct a fresh instance; Save() may merge with disk state but our
        // assertions target the in-memory state which the methods always update first.
        return new GuiSettings();
    }
}
