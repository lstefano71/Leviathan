using Leviathan.Core;
using Leviathan.TUI;
using Leviathan.TUI.Rendering;
using Leviathan.TUI.Views;

namespace Leviathan.TUI.Tests;

public class TextViewControllerTests
{
    private static string CreateTempFile(byte[] content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void SelectAll_SetsAnchorToZeroAndCursorToLength()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            state.TextCursorOffset = 5;
            TextViewController ctrl = new(state);

            ctrl.SelectAll();

            Assert.Equal(0, state.TextSelectionAnchor);
            Assert.Equal(data.Length - 1, state.TextCursorOffset);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void SelectAll_EmptyDocument_DoesNothing()
    {
        AppState state = new();
        TextViewController ctrl = new(state);

        ctrl.SelectAll();

        Assert.Equal(-1, state.TextSelectionAnchor);
    }

    [Fact]
    public void CopySelection_WithSelection_ReturnsText()
    {
        byte[] data = "Hello, World!"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            state.TextSelectionAnchor = 0;
            state.TextCursorOffset = 4;
            TextViewController ctrl = new(state);

            string? result = ctrl.CopySelection();

            Assert.NotNull(result);
            Assert.Equal("Hello", result);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void CopySelection_NoSelection_ReturnsNull()
    {
        byte[] data = "Hello"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            TextViewController ctrl = new(state);

            string? result = ctrl.CopySelection();

            Assert.Null(result);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void Paste_InsertsTextAtCursor()
    {
        byte[] data = "AB"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            state.TextCursorOffset = 1;
            TextViewController ctrl = new(state);

            ctrl.Paste("XY");

            Assert.Equal(4, state.Document!.Length);
            Span<byte> buf = stackalloc byte[4];
            state.Document.Read(0, buf);
            Assert.Equal("AXYB"u8, buf);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void SelectAll_ThenCopy_ReturnsEntireContent()
    {
        byte[] data = "Test data"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            TextViewController ctrl = new(state);

            ctrl.SelectAll();
            string? result = ctrl.CopySelection();

            Assert.NotNull(result);
            Assert.Equal("Test data", result);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    // ─── ClickAtPosition ───

    [Fact]
    public void ClickAtPosition_FirstRow_MovesCursorToCorrectOffset()
    {
        byte[] data = "Hello World\n"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            TextViewController ctrl = new(state);
            // Force a render to populate visual lines
            ctrl.RenderRows(80, 25);

            // Click at row 0, col 14 (gutter=9, so text col 5 → 'W' at byte 6)
            ctrl.ClickAtPosition(0, 14, 71);
            Assert.Equal(5, state.TextCursorOffset);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void ClickAtPosition_SecondRow_MovesCursorToSecondLine()
    {
        byte[] data = "Line1\nLine2\n"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            TextViewController ctrl = new(state);
            ctrl.RenderRows(80, 25);

            // Click row 1, col 9 (gutter=9 → text col 0 → first char of Line2 = byte 6)
            ctrl.ClickAtPosition(1, 9, 71);
            Assert.Equal(6, state.TextCursorOffset);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    // ─── ScrollUp / ScrollDown ───

    [Fact]
    public void ScrollDown_MovesTopOffsetWithoutMovingCursor()
    {
        byte[] data = "Line1\nLine2\nLine3\nLine4\n"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            TextViewController ctrl = new(state);
            state.TextCursorOffset = 0;

            ctrl.ScrollDown(1);

            // Top should have moved to Line2 (byte 6), cursor stays at 0
            Assert.Equal(6, state.TextTopOffset);
            Assert.Equal(0, state.TextCursorOffset);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void ScrollUp_MovesTopOffsetBack()
    {
        byte[] data = "Line1\nLine2\nLine3\n"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            TextViewController ctrl = new(state);
            state.TextTopOffset = 6; // Start at Line2

            ctrl.ScrollUp(1);

            Assert.Equal(0, state.TextTopOffset);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    // ─── Double-Caret Fix ───

    [Fact]
    public void RenderRows_SoftWrapBoundary_NoDuplicateCursorBlock()
    {
        // Create a line that will soft-wrap at ~20 cols
        // "AAAAA..." (25 chars) + newline → wraps at col 20 to a second visual line
        byte[] data = new byte[26];
        for (int i = 0; i < 25; i++) data[i] = (byte)'A';
        data[25] = (byte)'\n';
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            state.WordWrap = true;
            // Place cursor at byte 20 (the soft-wrap boundary)
            state.TextCursorOffset = 20;
            TextViewController ctrl = new(state);

            // Render with narrow width: gutter(9) + 20 = 29 → wrap at col 20
            string[] rows = ctrl.RenderRows(29, 10);
            Assert.True(rows.Length >= 2, "Expected at least 2 visual rows from wrapped line");

            // Count rows that contain the cursor background color (CursorBg RGB escape)
            // The cursor should appear in exactly one row, not both
            string cursorBg = AnsiBuilder.CursorBg.ToBackgroundAnsi();
            int cursorRowCount = 0;
            foreach (string row in rows)
            {
                if (row.Contains(cursorBg))
                    cursorRowCount++;
            }
            Assert.Equal(1, cursorRowCount);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void RenderRows_CursorAtStartOfLine_NoDuplicateCursorOnPreviousLine()
    {
        // File: "AB\nCD\n" — cursor at byte 3 ('C', start of second line)
        // lineEnd of line 0 (offset=0, byteLen=3) = 3, which equals cursor offset.
        // The cursor should only appear on line 1, not as a block on line 0.
        byte[] data = "AB\nCD\n"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            state.TextCursorOffset = 3; // on 'C'
            TextViewController ctrl = new(state);

            string[] rows = ctrl.RenderRows(80, 25);
            Assert.True(rows.Length >= 2, "Expected at least 2 rows");

            string cursorBg = AnsiBuilder.CursorBg.ToBackgroundAnsi();
            int cursorRowCount = 0;
            foreach (string row in rows)
            {
                if (row.Contains(cursorBg))
                    cursorRowCount++;
            }
            Assert.Equal(1, cursorRowCount);
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public void RenderRows_CursorOnNewline_ShowsBlockCursor()
    {
        // File: "AB\nCD\n" — cursor at byte 2 (the \n). DecodeLineToDisplay skips \n,
        // so the cursor is on a non-displayed byte. The block cursor should show on line 0.
        byte[] data = "AB\nCD\n"u8.ToArray();
        string path = CreateTempFile(data);
        AppState state = new();
        try
        {
            state.OpenFile(path);
            state.TextCursorOffset = 2; // on \n
            TextViewController ctrl = new(state);

            string[] rows = ctrl.RenderRows(80, 25);
            Assert.True(rows.Length >= 1, "Expected at least 1 row");

            string cursorBg = AnsiBuilder.CursorBg.ToBackgroundAnsi();
            bool cursorVisible = false;
            foreach (string row in rows)
            {
                if (row.Contains(cursorBg))
                {
                    cursorVisible = true;
                    break;
                }
            }
            Assert.True(cursorVisible, "Cursor on newline should still be visible as a block cursor");
        }
        finally { state.Document?.Dispose(); File.Delete(path); }
    }
}