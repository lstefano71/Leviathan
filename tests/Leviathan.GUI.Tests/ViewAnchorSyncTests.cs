using Leviathan.GUI.Helpers;

using System.Text;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for linked-tab anchor synchronization logic.
/// </summary>
public sealed class ViewAnchorSyncTests
{
    [Fact]
    public void CaptureAndMap_HexToText_UsesVisibleHexCursorAnchor()
    {
        string path = CreateTempFile(".bin", CreateSequentialBytes(1024));
        AppState state = new();
        try {
            state.OpenFile(path);
            state.HexBaseOffset = 128;
            state.HexCursorOffset = 511;

            long anchorOffset = ViewAnchorSync.CaptureSourceAnchorOffset(state, ViewMode.Hex, null);
            long mappedOffset = ViewAnchorSync.MapAnchorToTargetOffset(state, ViewMode.Text, anchorOffset);

            Assert.Equal(511, anchorOffset);
            Assert.Equal(511, mappedOffset);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void CaptureAndMap_HexToText_FallsBackToHexTopWhenCursorIsOffscreen()
    {
        string path = CreateTempFile(".bin", CreateSequentialBytes(1024));
        AppState state = new();
        try {
            state.OpenFile(path);
            state.HexBaseOffset = 128;
            state.HexCursorOffset = 900;

            long anchorOffset = ViewAnchorSync.CaptureSourceAnchorOffset(state, ViewMode.Hex, null);
            long mappedOffset = ViewAnchorSync.MapAnchorToTargetOffset(state, ViewMode.Text, anchorOffset);

            Assert.Equal(128, anchorOffset);
            Assert.Equal(128, mappedOffset);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void CaptureAndMap_TextToHex_UsesVisibleTextCursorAnchor()
    {
        string path = CreateTempFile(".txt", Encoding.UTF8.GetBytes("line0\r\nline1\r\nline2\r\nline3\r\n"));
        AppState state = new();
        try {
            state.OpenFile(path);
            state.TextTopOffset = 14;
            state.TextCursorOffset = 22;

            long anchorOffset = ViewAnchorSync.CaptureSourceAnchorOffset(state, ViewMode.Text, null);
            long mappedOffset = ViewAnchorSync.MapAnchorToTargetOffset(state, ViewMode.Hex, anchorOffset);

            Assert.Equal(22, anchorOffset);
            Assert.Equal(22, mappedOffset);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void CaptureAndMap_TextToHex_FallsBackToTextTopWhenCursorIsAboveViewport()
    {
        string path = CreateTempFile(".txt", Encoding.UTF8.GetBytes("line0\r\nline1\r\nline2\r\nline3\r\n"));
        AppState state = new();
        try {
            state.OpenFile(path);
            state.TextTopOffset = 20;
            state.TextCursorOffset = 10;

            long anchorOffset = ViewAnchorSync.CaptureSourceAnchorOffset(state, ViewMode.Text, null);
            long mappedOffset = ViewAnchorSync.MapAnchorToTargetOffset(state, ViewMode.Hex, anchorOffset);

            Assert.Equal(20, anchorOffset);
            Assert.Equal(20, mappedOffset);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void CaptureSourceAnchorOffset_CsvMode_UsesRowOffsetProvider()
    {
        string path = CreateTempFile(".bin", CreateSequentialBytes(2048));
        AppState state = new();
        try {
            state.OpenFile(path);
            state.CsvTopRowIndex = 7;
            long requestedRow = -1;

            long anchorOffset = ViewAnchorSync.CaptureSourceAnchorOffset(state, ViewMode.Csv, rowIndex => {
                requestedRow = rowIndex;
                return 777;
            });

            Assert.Equal(7, requestedRow);
            Assert.Equal(777, anchorOffset);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    [Fact]
    public void CaptureSourceAnchorOffset_CsvMode_UsesVisibleCursorRowWhenAvailable()
    {
        string path = CreateTempFile(".bin", CreateSequentialBytes(2048));
        AppState state = new();
        try {
            state.OpenFile(path);
            state.CsvTopRowIndex = 7;
            state.CsvCursorRow = 10;
            long requestedRow = -1;

            long anchorOffset = ViewAnchorSync.CaptureSourceAnchorOffset(state, ViewMode.Csv, rowIndex => {
                requestedRow = rowIndex;
                return rowIndex * 100;
            });

            Assert.Equal(10, requestedRow);
            Assert.Equal(1000, anchorOffset);
        } finally {
            state.CloseFile();
            File.Delete(path);
        }
    }

    private static byte[] CreateSequentialBytes(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i & 0xFF);

        return bytes;
    }

    private static string CreateTempFile(string extension, byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        return path;
    }
}
