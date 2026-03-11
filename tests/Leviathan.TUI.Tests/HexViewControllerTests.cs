using Leviathan.TUI.Views;

namespace Leviathan.TUI.Tests;

public class HexViewControllerTests
{
  private static string CreateTempFile(byte[] content)
  {
    string path = Path.GetTempFileName();
    File.WriteAllBytes(path, content);
    return path;
  }

  // ─── CopySelection ───

  [Fact]
  public void CopySelection_NoSelection_ReturnsNull()
  {
    byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      HexViewController ctrl = new(state);
      string? result = ctrl.CopySelection();
      Assert.Null(result);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void CopySelection_WithSelection_ReturnsHexString()
  {
    byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexSelectionAnchor = 0;
      state.HexCursorOffset = 3;
      HexViewController ctrl = new(state);

      string? result = ctrl.CopySelection();

      Assert.NotNull(result);
      Assert.Equal("DE AD BE EF", result);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void CopySelection_PartialSelection_ReturnsCorrectSubset()
  {
    byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexSelectionAnchor = 1;
      state.HexCursorOffset = 3;
      HexViewController ctrl = new(state);

      string? result = ctrl.CopySelection();

      Assert.Equal("02 03 04", result);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void CopySelection_SingleByte_ReturnsOneHexPair()
  {
    byte[] data = [0xFF, 0x00];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexSelectionAnchor = 0;
      state.HexCursorOffset = 0;
      HexViewController ctrl = new(state);

      string? result = ctrl.CopySelection();

      Assert.Equal("FF", result);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  // ─── Paste ───

  [Fact]
  public void Paste_ValidHexString_InsertsBytes()
  {
    byte[] data = [0x01, 0x02];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexCursorOffset = 1;
      HexViewController ctrl = new(state);

      ctrl.Paste("DE AD BE EF");

      Assert.Equal(6, state.Document!.Length);
      Span<byte> buf = stackalloc byte[6];
      state.Document.Read(0, buf);
      Assert.Equal((byte)0x01, buf[0]);
      Assert.Equal((byte)0xDE, buf[1]);
      Assert.Equal((byte)0xAD, buf[2]);
      Assert.Equal((byte)0xBE, buf[3]);
      Assert.Equal((byte)0xEF, buf[4]);
      Assert.Equal((byte)0x02, buf[5]);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void Paste_CompactHexString_InsertsBytes()
  {
    byte[] data = [0x00];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexCursorOffset = 0;
      HexViewController ctrl = new(state);

      ctrl.Paste("DEADBEEF");

      Assert.Equal(5, state.Document!.Length);
      Span<byte> buf = stackalloc byte[5];
      state.Document.Read(0, buf);
      Assert.Equal((byte)0xDE, buf[0]);
      Assert.Equal((byte)0xAD, buf[1]);
      Assert.Equal((byte)0xBE, buf[2]);
      Assert.Equal((byte)0xEF, buf[3]);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void Paste_DashSeparatedHex_InsertsBytes()
  {
    byte[] data = [0x00];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexCursorOffset = 0;
      HexViewController ctrl = new(state);

      ctrl.Paste("CA-FE");

      Assert.Equal(3, state.Document!.Length);
      Span<byte> buf = stackalloc byte[3];
      state.Document.Read(0, buf);
      Assert.Equal((byte)0xCA, buf[0]);
      Assert.Equal((byte)0xFE, buf[1]);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void Paste_InvalidHex_DoesNotModifyDocument()
  {
    byte[] data = [0x01, 0x02];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexCursorOffset = 0;
      HexViewController ctrl = new(state);

      ctrl.Paste("GHIJ");

      Assert.Equal(2, state.Document!.Length);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void Paste_OddNibble_DoesNotModifyDocument()
  {
    byte[] data = [0x01, 0x02];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexCursorOffset = 0;
      HexViewController ctrl = new(state);

      ctrl.Paste("DEA");

      Assert.Equal(2, state.Document!.Length);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void Paste_WithSelection_ReplacesSelectedBytes()
  {
    byte[] data = [0x01, 0x02, 0x03, 0x04];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexSelectionAnchor = 1;
      state.HexCursorOffset = 2;
      HexViewController ctrl = new(state);

      ctrl.Paste("FF");

      // Original: 01 02 03 04 → delete [1..2] → 01 04 → insert FF at 1 → 01 FF 04
      Assert.Equal(3, state.Document!.Length);
      Span<byte> buf = stackalloc byte[3];
      state.Document.Read(0, buf);
      Assert.Equal((byte)0x01, buf[0]);
      Assert.Equal((byte)0xFF, buf[1]);
      Assert.Equal((byte)0x04, buf[2]);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  // ─── SelectAll ───

  [Fact]
  public void SelectAll_SetsAnchorToZeroAndCursorToEnd()
  {
    byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.HexCursorOffset = 2;
      HexViewController ctrl = new(state);

      ctrl.SelectAll();

      Assert.Equal(0, state.HexSelectionAnchor);
      Assert.Equal(4, state.HexCursorOffset); // Length - 1
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void SelectAll_EmptyDocument_DoesNothing()
  {
    AppState state = new();
    HexViewController ctrl = new(state);

    ctrl.SelectAll();

    Assert.Equal(-1, state.HexSelectionAnchor);
  }

  // ─── Roundtrip: Copy → Paste ───

  [Fact]
  public void CopyPaste_Roundtrip_PreservesBytes()
  {
    byte[] data = [0xCA, 0xFE, 0xBA, 0xBE];
    string path = CreateTempFile(data);
    AppState srcState = new();
    AppState dstState = new();
    string? targetPath = null;
    try {
      srcState.OpenFile(path);
      srcState.HexSelectionAnchor = 0;
      srcState.HexCursorOffset = 3;
      HexViewController srcCtrl = new(srcState);

      string? copied = srcCtrl.CopySelection();
      Assert.NotNull(copied);

      byte[] target = [0x00, 0x00];
      targetPath = CreateTempFile(target);
      dstState.OpenFile(targetPath);
      dstState.HexCursorOffset = 1;
      HexViewController dstCtrl = new(dstState);
      dstCtrl.Paste(copied);

      Assert.Equal(6, dstState.Document!.Length);
      Span<byte> buf = stackalloc byte[6];
      dstState.Document.Read(0, buf);
      Assert.Equal((byte)0x00, buf[0]);
      Assert.Equal((byte)0xCA, buf[1]);
      Assert.Equal((byte)0xFE, buf[2]);
      Assert.Equal((byte)0xBA, buf[3]);
      Assert.Equal((byte)0xBE, buf[4]);
      Assert.Equal((byte)0x00, buf[5]);
    } finally {
      srcState.Document?.Dispose();
      dstState.Document?.Dispose();
      File.Delete(path);
      if (targetPath is not null) File.Delete(targetPath);
    }
  }

  // ─── HexColToByteIndex ───

  [Fact]
  public void HexColToByteIndex_FirstByte_ReturnsZero()
  {
    Assert.Equal(0, HexViewController.HexColToByteIndex(0, 16));
  }

  [Fact]
  public void HexColToByteIndex_SecondByte_ReturnsOne()
  {
    Assert.Equal(1, HexViewController.HexColToByteIndex(3, 16));
  }

  [Fact]
  public void HexColToByteIndex_NinthByte_CrossesGroupBoundary()
  {
    // After 8 bytes: 8*3=24 + 1 group sep = 25 → byte 8
    Assert.Equal(8, HexViewController.HexColToByteIndex(25, 16));
  }

  [Fact]
  public void HexColToByteIndex_PastEnd_ReturnsLastByte()
  {
    Assert.Equal(15, HexViewController.HexColToByteIndex(200, 16));
  }

  // ─── ClickAtPosition ───

  [Fact]
  public void ClickAtPosition_HexArea_MovesCursor()
  {
    byte[] data = new byte[32];
    for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.BytesPerRow = 16;
      HexViewController ctrl = new(state);

      // Click at row 0, col 21 → hex area byte 1 (col 18 = byte 0, col 21 = byte 1)
      ctrl.ClickAtPosition(0, 21);
      Assert.Equal(1, state.HexCursorOffset);
      Assert.Equal(-1, state.HexSelectionAnchor);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void ClickAtPosition_SecondRow_MovesCursorToCorrectByte()
  {
    byte[] data = new byte[64];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.BytesPerRow = 16;
      HexViewController ctrl = new(state);

      // Click at row 1, col 18 → first byte of second row = byte 16
      ctrl.ClickAtPosition(1, 18);
      Assert.Equal(16, state.HexCursorOffset);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }

  [Fact]
  public void ClickAtPosition_AsciiArea_MovesCursor()
  {
    byte[] data = new byte[32];
    string path = CreateTempFile(data);
    AppState state = new();
    try {
      state.OpenFile(path);
      state.BytesPerRow = 16;
      HexViewController ctrl = new(state);

      // ASCII area starts at: 18 (offset) + 16*3 + floor(15/8) + 1 = 18 + 48 + 1 + 1 = 68
      int asciiStart = 18 + 16 * 3 + (16 - 1) / 8 + 1;
      ctrl.ClickAtPosition(0, asciiStart + 5);
      Assert.Equal(5, state.HexCursorOffset);
    } finally { state.Document?.Dispose(); File.Delete(path); }
  }
}
