using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests for hex formatting helpers.
/// </summary>
public sealed class HexFormatterTests
{
    [Fact]
    public void FormatByte_Zero_Returns00()
    {
        Span<char> buf = stackalloc char[2];
        HexFormatter.FormatByte(0x00, buf);
        Assert.Equal('0', buf[0]);
        Assert.Equal('0', buf[1]);
    }

    [Fact]
    public void FormatByte_FF_ReturnsFF()
    {
        Span<char> buf = stackalloc char[2];
        HexFormatter.FormatByte(0xFF, buf);
        Assert.Equal('F', buf[0]);
        Assert.Equal('F', buf[1]);
    }

    [Fact]
    public void FormatByte_Midrange_ReturnsCorrect()
    {
        Span<char> buf = stackalloc char[2];
        HexFormatter.FormatByte(0xA5, buf);
        Assert.Equal('A', buf[0]);
        Assert.Equal('5', buf[1]);
    }

    [Fact]
    public void FormatOffset_SmallFile_Returns8Digits()
    {
        string result = HexFormatter.FormatOffset(0x1234, 8);
        Assert.Equal("00001234", result);
    }

    [Fact]
    public void FormatOffset_LargeFile_Returns16Digits()
    {
        string result = HexFormatter.FormatOffset(0x123456789ABCL, 16);
        Assert.Equal("0000123456789ABC", result);
    }

    [Fact]
    public void AddressDigits_SmallFile_Returns8()
    {
        Assert.Equal(8, HexFormatter.AddressDigits(0xFFFF_FFFFL));
    }

    [Fact]
    public void AddressDigits_LargeFile_Returns16()
    {
        Assert.Equal(16, HexFormatter.AddressDigits(0x1_0000_0000L));
    }

    [Fact]
    public void ParseHexDigit_ValidDigits_ReturnsCorrect()
    {
        Assert.Equal(0, HexFormatter.ParseHexDigit('0'));
        Assert.Equal(9, HexFormatter.ParseHexDigit('9'));
        Assert.Equal(10, HexFormatter.ParseHexDigit('A'));
        Assert.Equal(15, HexFormatter.ParseHexDigit('F'));
        Assert.Equal(10, HexFormatter.ParseHexDigit('a'));
        Assert.Equal(15, HexFormatter.ParseHexDigit('f'));
    }

    [Fact]
    public void ParseHexDigit_Invalid_ReturnsNegative()
    {
        Assert.Equal(-1, HexFormatter.ParseHexDigit('G'));
        Assert.Equal(-1, HexFormatter.ParseHexDigit(' '));
        Assert.Equal(-1, HexFormatter.ParseHexDigit('z'));
    }
}
