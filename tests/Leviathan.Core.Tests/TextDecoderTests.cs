using Leviathan.Core.Text;

using System.Text;

namespace Leviathan.Core.Tests;

public class Utf8TextDecoderTests
{
    private readonly Utf8TextDecoder _decoder = new();

    [Fact]
    public void DecodeRune_Ascii_ReturnsSingleByte()
    {
        ReadOnlySpan<byte> data = [0x41];
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune('A'), rune);
        Assert.Equal(1, len);
    }

    [Fact]
    public void DecodeRune_MultiByte_ReturnsCorrectRune()
    {
        ReadOnlySpan<byte> data = [0xC3, 0xA9]; // é
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune('é'), rune);
        Assert.Equal(2, len);
    }

    [Fact]
    public void DecodeRune_InvalidSequence_ReturnsReplacement()
    {
        ReadOnlySpan<byte> data = [0xFF];
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(Rune.ReplacementChar, rune);
        Assert.Equal(1, len);
    }

    [Fact]
    public void AlignToCharBoundary_OnContinuation_SnapsBack()
    {
        ReadOnlySpan<byte> data = [0xC3, 0xA9, 0x41]; // é A
        int aligned = _decoder.AlignToCharBoundary(data, 1);

        Assert.Equal(0, aligned);
    }

    [Fact]
    public void EncodeRune_Ascii_WritesOneByte()
    {
        Span<byte> output = stackalloc byte[4];
        int written = _decoder.EncodeRune(new Rune('A'), output);

        Assert.Equal(1, written);
        Assert.Equal(0x41, output[0]);
    }

    [Fact]
    public void EncodeRune_MultiByte_WritesCorrectBytes()
    {
        Span<byte> output = stackalloc byte[4];
        int written = _decoder.EncodeRune(new Rune('é'), output);

        Assert.Equal(2, written);
        Assert.Equal(0xC3, output[0]);
        Assert.Equal(0xA9, output[1]);
    }

    [Fact]
    public void IsNewline_Lf_ReturnsTrue()
    {
        ReadOnlySpan<byte> data = [0x0A];
        bool result = _decoder.IsNewline(data, 0, out int nlLen);

        Assert.True(result);
        Assert.Equal(1, nlLen);
    }

    [Fact]
    public void IsNewline_Cr_ReturnsTrue()
    {
        ReadOnlySpan<byte> data = [0x0D];
        bool result = _decoder.IsNewline(data, 0, out int nlLen);

        Assert.True(result);
        Assert.Equal(1, nlLen);
    }

    [Fact]
    public void IsNewline_Regular_ReturnsFalse()
    {
        ReadOnlySpan<byte> data = [0x41];
        bool result = _decoder.IsNewline(data, 0, out int _);

        Assert.False(result);
    }

    [Fact]
    public void EncodeString_ReturnsUtf8Bytes()
    {
        byte[] result = _decoder.EncodeString("café");
        byte[] expected = Encoding.UTF8.GetBytes("café");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Encoding_ReturnsUtf8()
    {
        Assert.Equal(TextEncoding.Utf8, _decoder.Encoding);
    }

    [Fact]
    public void MinCharBytes_ReturnsOne()
    {
        Assert.Equal(1, _decoder.MinCharBytes);
    }
}

public class Utf16LeTextDecoderTests
{
    private readonly Utf16LeTextDecoder _decoder = new();

    [Fact]
    public void DecodeRune_BmpChar_ReturnsTwoBytes()
    {
        ReadOnlySpan<byte> data = [0x41, 0x00]; // 'A' in UTF-16 LE
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune('A'), rune);
        Assert.Equal(2, len);
    }

    [Fact]
    public void DecodeRune_SupplementaryChar_ReturnsFourBytes()
    {
        // U+1F600 (😀): high surrogate 0xD83D, low surrogate 0xDE00
        ReadOnlySpan<byte> data = [0x3D, 0xD8, 0x00, 0xDE];
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune(0x1F600), rune);
        Assert.Equal(4, len);
    }

    [Fact]
    public void DecodeRune_LoneSurrogate_ReturnsReplacement()
    {
        // High surrogate 0xD800 without a following low surrogate (end of data)
        ReadOnlySpan<byte> data = [0x00, 0xD8];
        (Rune rune, int _) = _decoder.DecodeRune(data, 0);

        Assert.Equal(Rune.ReplacementChar, rune);
    }

    [Fact]
    public void DecodeRune_InsufficientBytes_ReturnsReplacement()
    {
        ReadOnlySpan<byte> data = [0x41]; // only 1 byte
        (Rune rune, int _) = _decoder.DecodeRune(data, 0);

        Assert.Equal(Rune.ReplacementChar, rune);
    }

    [Fact]
    public void AlignToCharBoundary_OddOffset_SnapsToEven()
    {
        ReadOnlySpan<byte> data = [0x41, 0x00, 0x42, 0x00];
        int aligned = _decoder.AlignToCharBoundary(data, 3);

        Assert.Equal(2, aligned);
    }

    [Fact]
    public void AlignToCharBoundary_LowSurrogate_BacksUpToHigh()
    {
        // Surrogate pair for U+1F600: high 0xD83D at [0..1], low 0xDE00 at [2..3]
        ReadOnlySpan<byte> data = [0x3D, 0xD8, 0x00, 0xDE];
        int aligned = _decoder.AlignToCharBoundary(data, 2);

        Assert.Equal(0, aligned);
    }

    [Fact]
    public void EncodeRune_BmpChar_WritesTwoLeBytes()
    {
        Span<byte> output = stackalloc byte[4];
        int written = _decoder.EncodeRune(new Rune('A'), output);

        Assert.Equal(2, written);
        Assert.Equal(0x41, output[0]);
        Assert.Equal(0x00, output[1]);
    }

    [Fact]
    public void EncodeRune_Supplementary_WritesFourLeBytes()
    {
        Span<byte> output = stackalloc byte[4];
        int written = _decoder.EncodeRune(new Rune(0x1F600), output);

        Assert.Equal(4, written);
        Assert.Equal(0x3D, output[0]);
        Assert.Equal(0xD8, output[1]);
        Assert.Equal(0x00, output[2]);
        Assert.Equal(0xDE, output[3]);
    }

    [Fact]
    public void IsNewline_Utf16Lf_ReturnsTrue()
    {
        ReadOnlySpan<byte> data = [0x0A, 0x00];
        bool result = _decoder.IsNewline(data, 0, out int nlLen);

        Assert.True(result);
        Assert.Equal(2, nlLen);
    }

    [Fact]
    public void IsNewline_Utf16Cr_ReturnsTrue()
    {
        ReadOnlySpan<byte> data = [0x0D, 0x00];
        bool result = _decoder.IsNewline(data, 0, out int nlLen);

        Assert.True(result);
        Assert.Equal(2, nlLen);
    }

    [Fact]
    public void IsNewline_NonNewline_ReturnsFalse()
    {
        ReadOnlySpan<byte> data = [0x41, 0x00];
        bool result = _decoder.IsNewline(data, 0, out int _);

        Assert.False(result);
    }

    [Fact]
    public void IsNewline_InsufficientBytes_ReturnsFalse()
    {
        ReadOnlySpan<byte> data = [0x0A]; // only 1 byte
        bool result = _decoder.IsNewline(data, 0, out int _);

        Assert.False(result);
    }

    [Fact]
    public void EncodeString_ReturnsUtf16LeBytes()
    {
        byte[] result = _decoder.EncodeString("AB");
        byte[] expected = [0x41, 0x00, 0x42, 0x00];

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Encoding_ReturnsUtf16Le()
    {
        Assert.Equal(TextEncoding.Utf16Le, _decoder.Encoding);
    }

    [Fact]
    public void MinCharBytes_ReturnsTwo()
    {
        Assert.Equal(2, _decoder.MinCharBytes);
    }
}

public class Windows1252TextDecoderTests
{
    private readonly Windows1252TextDecoder _decoder = new();

    [Fact]
    public void DecodeRune_Ascii_ReturnsSameCodePoint()
    {
        ReadOnlySpan<byte> data = [0x41];
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune('A'), rune);
        Assert.Equal(1, len);
    }

    [Fact]
    public void DecodeRune_Euro_ReturnsCorrectRune()
    {
        ReadOnlySpan<byte> data = [0x80]; // € in Windows-1252
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune(0x20AC), rune);
        Assert.Equal(1, len);
    }

    [Fact]
    public void DecodeRune_LeftDoubleQuote_ReturnsCorrectRune()
    {
        ReadOnlySpan<byte> data = [0x93]; // " in Windows-1252
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune(0x201C), rune);
        Assert.Equal(1, len);
    }

    [Fact]
    public void DecodeRune_UndefinedByte_ReturnsReplacement()
    {
        ReadOnlySpan<byte> data = [0x81]; // undefined in Windows-1252
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(Rune.ReplacementChar, rune);
        Assert.Equal(1, len);
    }

    [Fact]
    public void DecodeRune_Latin1Range_ReturnsSameCodePoint()
    {
        ReadOnlySpan<byte> data = [0xE9]; // é in Latin-1 range (0xA0–0xFF)
        (Rune rune, int len) = _decoder.DecodeRune(data, 0);

        Assert.Equal(new Rune(0xE9), rune);
        Assert.Equal(1, len);
    }

    [Fact]
    public void AlignToCharBoundary_AlwaysReturnsOffset()
    {
        ReadOnlySpan<byte> data = [0x41, 0x42, 0x43];
        int aligned = _decoder.AlignToCharBoundary(data, 2);

        Assert.Equal(2, aligned);
    }

    [Fact]
    public void EncodeRune_Ascii_ReturnsByte()
    {
        Span<byte> output = stackalloc byte[1];
        int written = _decoder.EncodeRune(new Rune('A'), output);

        Assert.Equal(1, written);
        Assert.Equal(0x41, output[0]);
    }

    [Fact]
    public void EncodeRune_Euro_Returns0x80()
    {
        Span<byte> output = stackalloc byte[1];
        int written = _decoder.EncodeRune(new Rune(0x20AC), output);

        Assert.Equal(1, written);
        Assert.Equal(0x80, output[0]);
    }

    [Fact]
    public void EncodeRune_Unrepresentable_ReturnsQuestionMark()
    {
        Span<byte> output = stackalloc byte[1];
        int written = _decoder.EncodeRune(new Rune(0x4E00), output); // CJK ideograph

        Assert.Equal(1, written);
        Assert.Equal((byte)'?', output[0]);
    }

    [Fact]
    public void IsNewline_Lf_ReturnsTrue()
    {
        ReadOnlySpan<byte> data = [0x0A];
        bool result = _decoder.IsNewline(data, 0, out int nlLen);

        Assert.True(result);
        Assert.Equal(1, nlLen);
    }

    [Fact]
    public void IsNewline_Regular_ReturnsFalse()
    {
        ReadOnlySpan<byte> data = [0x41];
        bool result = _decoder.IsNewline(data, 0, out int _);

        Assert.False(result);
    }

    [Fact]
    public void EncodeString_ReturnsW1252Bytes()
    {
        byte[] result = _decoder.EncodeString("€");
        byte[] expected = [0x80];

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Encoding_ReturnsWindows1252()
    {
        Assert.Equal(TextEncoding.Windows1252, _decoder.Encoding);
    }

    [Fact]
    public void MinCharBytes_ReturnsOne()
    {
        Assert.Equal(1, _decoder.MinCharBytes);
    }
}
