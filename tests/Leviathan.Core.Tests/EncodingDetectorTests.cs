using Leviathan.Core.Text;

namespace Leviathan.Core.Tests;

public class EncodingDetectorTests
{
    [Fact]
    public void Detect_Utf8Bom_ReturnsUtf8WithBomLength3()
    {
        ReadOnlySpan<byte> sample = [0xEF, 0xBB, 0xBF, 0x48, 0x65, 0x6C, 0x6C, 0x6F];
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.Equal(TextEncoding.Utf8, enc);
        Assert.Equal(3, bom);
    }

    [Fact]
    public void Detect_Utf16LeBom_ReturnsUtf16LeWithBomLength2()
    {
        ReadOnlySpan<byte> sample = [0xFF, 0xFE, 0x48, 0x00, 0x65, 0x00];
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.Equal(TextEncoding.Utf16Le, enc);
        Assert.Equal(2, bom);
    }

    [Fact]
    public void Detect_EmptySample_ReturnsUtf8()
    {
        (TextEncoding enc, int bom) = EncodingDetector.Detect([]);

        Assert.Equal(TextEncoding.Utf8, enc);
        Assert.Equal(0, bom);
    }

    [Fact]
    public void Detect_PureAsciiNoBom_ReturnsUtf8()
    {
        ReadOnlySpan<byte> sample = "Hello World"u8;
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.Equal(TextEncoding.Utf8, enc);
        Assert.Equal(0, bom);
    }

    [Fact]
    public void Detect_Utf8MultiByte_ReturnsUtf8()
    {
        ReadOnlySpan<byte> sample = [0x63, 0x61, 0x66, 0xC3, 0xA9]; // "café"
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.Equal(TextEncoding.Utf8, enc);
        Assert.Equal(0, bom);
    }

    [Fact]
    public void Detect_Utf16LeNoBom_DetectsUtf16Le()
    {
        // "Hello" as UTF-16 LE (10 bytes, meets MinUtf16SampleSize of 8)
        ReadOnlySpan<byte> sample = [0x48, 0x00, 0x65, 0x00, 0x6C, 0x00, 0x6C, 0x00, 0x6F, 0x00];
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.Equal(TextEncoding.Utf16Le, enc);
        Assert.Equal(0, bom);
    }

    [Fact]
    public void Detect_HighBytes_ReturnsWindows1252()
    {
        // Repeated Windows-1252 specials: bytes in 0x80–0xFF with no valid UTF-8 pattern
        ReadOnlySpan<byte> sample = [0x80, 0x85, 0x92, 0x93, 0xE9, 0xF1, 0x80, 0x85, 0x92, 0x93, 0xE9, 0xF1];
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.Equal(TextEncoding.Windows1252, enc);
        Assert.Equal(0, bom);
    }

    [Fact]
    public void Detect_ShortSample_NoBom_ReturnsUtf8()
    {
        ReadOnlySpan<byte> sample = [0x41, 0x42]; // "AB"
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.Equal(TextEncoding.Utf8, enc);
        Assert.Equal(0, bom);
    }

    [Fact]
    public void Detect_FalseUtf16_NotTriggered()
    {
        // Zeros at even positions (not odd) — should not trigger UTF-16 LE
        ReadOnlySpan<byte> sample = [0x00, 0x41, 0x00, 0x42, 0x43, 0x44, 0x45, 0x46];
        (TextEncoding enc, int bom) = EncodingDetector.Detect(sample);

        Assert.NotEqual(TextEncoding.Utf16Le, enc);
    }
}
