using System.Text;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for AppState file-open defaults.
/// </summary>
public sealed class AppStateTests
{
    [Fact]
    public void OpenFile_CsvExtension_DefaultsToCsvView()
    {
        string path = CreateTempFile(".csv", "a,b\r\n1,2\r\n"u8.ToArray());
        AppState state = new();
        try {
            state.OpenFile(path);
            Assert.Equal(ViewMode.Csv, state.ActiveView);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenFile_TextExtension_DefaultsToTextView()
    {
        string path = CreateTempFile(".txt", Encoding.UTF8.GetBytes("hello\nworld"));
        AppState state = new();
        try {
            state.OpenFile(path);
            Assert.Equal(ViewMode.Text, state.ActiveView);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenFile_BinaryContent_DefaultsToHexView()
    {
        string path = CreateTempFile(".bin", [0x00, 0xFF, 0x10, 0xA1, 0x00, 0x7F]);
        AppState state = new();
        try {
            state.OpenFile(path);
            Assert.Equal(ViewMode.Hex, state.ActiveView);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenFile_ReadOnlyAlreadyEnabled_PreservesReadOnlyState()
    {
        string path = CreateTempFile(".txt", Encoding.UTF8.GetBytes("hello\nworld"));
        AppState state = new() { IsReadOnly = true };
        try {
            state.OpenFile(path);
            Assert.True(state.IsReadOnly);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void CloseFile_ResetsHorizontalScrollState()
    {
        string path = CreateTempFile(".csv", "a,b,c\r\n1,2,3\r\n"u8.ToArray());
        AppState state = new();
        try {
            state.OpenFile(path);
            state.TextHorizontalScroll = 12;
            state.CsvHorizontalScroll = 7;

            state.CloseFile();

            Assert.Equal(0, state.TextHorizontalScroll);
            Assert.Equal(0, state.CsvHorizontalScroll);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidateSearchResults_NoQuery_DoesNotRequestRestart()
    {
        AppState state = new();
        int callbacks = 0;
        state.SearchRestartRequested = () => callbacks++;
        state.InvalidateSearchResults();
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void InvalidateSearchResults_WithActiveSearchStatus_RequestsRestart()
    {
        AppState state = new() {
            FindInput = "needle",
            SearchStatus = "No matches"
        };
        int callbacks = 0;
        state.SearchRestartRequested = () => callbacks++;
        state.InvalidateSearchResults();
        Assert.Equal(1, callbacks);
    }

    private static string CreateTempFile(string extension, byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        return path;
    }
}
