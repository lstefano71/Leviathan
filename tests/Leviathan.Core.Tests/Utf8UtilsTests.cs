using Leviathan.Core.Text;

namespace Leviathan.Core.Tests;

public class Utf8UtilsTests
{
  [Fact]
  public void AlignToCharBoundary_AtAscii_ReturnsUnchanged()
  {
    byte[] data = "Hello"u8.ToArray();
    int result = Utf8Utils.AlignToCharBoundary(data, 2);
    Assert.Equal(2, result);
  }

  [Fact]
  public void AlignToCharBoundary_AtContinuationByte_RewindsToStart()
  {
    // UTF-8 for U+00E9 (é) = C3 A9
    byte[] data = [0x41, 0xC3, 0xA9, 0x42]; // "AéB"
    // Offset 2 = 0xA9, a continuation byte → should rewind to offset 1 (0xC3)
    int result = Utf8Utils.AlignToCharBoundary(data, 2);
    Assert.Equal(1, result);
  }

  [Fact]
  public void AlignToCharBoundary_At3ByteContinuation_RewindsToStart()
  {
    // UTF-8 for U+2603 (☃) = E2 98 83
    byte[] data = [0x41, 0xE2, 0x98, 0x83, 0x42]; // "A☃B"
    // Offset 3 = 0x83, continuation → should rewind to offset 1 (0xE2)
    int result = Utf8Utils.AlignToCharBoundary(data, 3);
    Assert.Equal(1, result);
  }

  [Fact]
  public void AlignToCharBoundary_At4ByteContinuation_RewindsToStart()
  {
    // UTF-8 for U+1F600 (😀) = F0 9F 98 80
    byte[] data = [0x41, 0xF0, 0x9F, 0x98, 0x80, 0x42]; // "A😀B"
    // Offset 4 = 0x80, continuation → should rewind to offset 1 (0xF0)
    int result = Utf8Utils.AlignToCharBoundary(data, 4);
    Assert.Equal(1, result);
  }

  [Fact]
  public void AlignToCharBoundary_AtZero_ReturnsZero()
  {
    byte[] data = [0xC3, 0xA9];
    int result = Utf8Utils.AlignToCharBoundary(data, 0);
    Assert.Equal(0, result);
  }

  [Fact]
  public void DecodeRune_Ascii_Returns1Byte()
  {
    byte[] data = "A"u8.ToArray();
    var (rune, len) = Utf8Utils.DecodeRune(data, 0);
    Assert.Equal('A', (char)rune.Value);
    Assert.Equal(1, len);
  }

  [Fact]
  public void DecodeRune_TwoByteChar_Returns2Bytes()
  {
    byte[] data = [0xC3, 0xA9]; // é
    var (rune, len) = Utf8Utils.DecodeRune(data, 0);
    Assert.Equal(0xE9, rune.Value);
    Assert.Equal(2, len);
  }

  [Fact]
  public void DecodeRune_ThreeByteChar_Returns3Bytes()
  {
    byte[] data = [0xE2, 0x98, 0x83]; // ☃
    var (rune, len) = Utf8Utils.DecodeRune(data, 0);
    Assert.Equal(0x2603, rune.Value);
    Assert.Equal(3, len);
  }

  [Fact]
  public void DecodeRune_FourByteChar_Returns4Bytes()
  {
    byte[] data = [0xF0, 0x9F, 0x98, 0x80]; // 😀
    var (rune, len) = Utf8Utils.DecodeRune(data, 0);
    Assert.Equal(0x1F600, rune.Value);
    Assert.Equal(4, len);
  }

  [Fact]
  public void DecodeRune_InvalidByte_ReturnsReplacement()
  {
    byte[] data = [0xFF];
    var (rune, len) = Utf8Utils.DecodeRune(data, 0);
    Assert.Equal(System.Text.Rune.ReplacementChar, rune);
    Assert.Equal(1, len);
  }

  [Fact]
  public void DecodeRune_PastEnd_ReturnsZeroLength()
  {
    byte[] data = [0x41];
    var (rune, len) = Utf8Utils.DecodeRune(data, 1);
    Assert.Equal(0, len);
  }

  [Fact]
  public void MeasureColumns_PureAscii_ReturnsLength()
  {
    byte[] data = "Hello"u8.ToArray();
    Assert.Equal(5, Utf8Utils.MeasureColumns(data));
  }

  [Fact]
  public void MeasureColumns_WithTab_ExpandsToTabWidth()
  {
    byte[] data = "A\tB"u8.ToArray();
    // 'A' + tab(4) + 'B' = 6
    Assert.Equal(6, Utf8Utils.MeasureColumns(data, tabWidth: 4));
  }

  [Fact]
  public void MeasureColumns_MultiByteChars_CountAsOneColumn()
  {
    // "Aé" = 41 C3 A9 → 2 columns
    byte[] data = [0x41, 0xC3, 0xA9];
    Assert.Equal(2, Utf8Utils.MeasureColumns(data));
  }

  [Fact]
  public void RuneColumnWidth_CJK_ReturnsTwo()
  {
    // U+4E2D = 中 (CJK)
    var rune = new System.Text.Rune(0x4E2D);
    Assert.Equal(2, Utf8Utils.RuneColumnWidth(rune));
  }
}
