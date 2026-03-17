using Leviathan.Core.Text;

using System.Text;

namespace Leviathan.Core.Tests;

public sealed class EncodingIntegrationTests
{
    private static string CreateTempFile(byte[] content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Detect_Utf16LeFile_AutoDetectsCorrectly()
    {
        // UTF-16 LE BOM (FF FE) + "Hello" in UTF-16 LE
        byte[] content =
        [
            0xFF, 0xFE,                                                     // BOM
            0x48, 0x00, 0x65, 0x00, 0x6C, 0x00, 0x6C, 0x00, 0x6F, 0x00  // Hello
        ];
        string path = CreateTempFile(content);

        try {
            using Document doc = new(path);
            Span<byte> sample = stackalloc byte[(int)Math.Min(8192, doc.Length)];
            int read = doc.Read(0, sample);

            (TextEncoding enc, int bomLen) = EncodingDetector.Detect(sample[..read]);

            Assert.Equal(TextEncoding.Utf16Le, enc);
            Assert.Equal(2, bomLen);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Detect_Utf8BomFile_AutoDetectsCorrectly()
    {
        // UTF-8 BOM (EF BB BF) + "Hello"
        byte[] content = [0xEF, 0xBB, 0xBF, 0x48, 0x65, 0x6C, 0x6C, 0x6F];
        string path = CreateTempFile(content);

        try {
            using Document doc = new(path);
            Span<byte> sample = stackalloc byte[(int)Math.Min(8192, doc.Length)];
            int read = doc.Read(0, sample);

            (TextEncoding enc, int bomLen) = EncodingDetector.Detect(sample[..read]);

            Assert.Equal(TextEncoding.Utf8, enc);
            Assert.Equal(3, bomLen);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Detect_Win1252File_FallsBackCorrectly()
    {
        // Windows-1252 bytes with 0x80–0x9F specials (no BOM, invalid as UTF-8)
        byte[] content = [0x80, 0x85, 0x92, 0x93, 0xE9, 0xF1, 0x80, 0x85, 0x92, 0x93, 0xE9, 0xF1];
        string path = CreateTempFile(content);

        try {
            using Document doc = new(path);
            Span<byte> sample = stackalloc byte[(int)Math.Min(8192, doc.Length)];
            int read = doc.Read(0, sample);

            (TextEncoding enc, int bomLen) = EncodingDetector.Detect(sample[..read]);

            Assert.Equal(TextEncoding.Windows1252, enc);
            Assert.Equal(0, bomLen);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundTrip_Utf16Le_DecodeEncode_SameBytes()
    {
        Utf16LeTextDecoder decoder = new();

        // "Héllo" in UTF-16 LE: H(48 00) é(E9 00) l(6C 00) l(6C 00) o(6F 00)
        byte[] input = [0x48, 0x00, 0xE9, 0x00, 0x6C, 0x00, 0x6C, 0x00, 0x6F, 0x00];
        Span<byte> reencoded = stackalloc byte[input.Length];

        int writePos = 0;
        int readPos = 0;
        while (readPos < input.Length) {
            (Rune rune, int byteLen) = decoder.DecodeRune(input, readPos);
            int written = decoder.EncodeRune(rune, reencoded[writePos..]);
            readPos += byteLen;
            writePos += written;
        }

        Assert.Equal(input.Length, writePos);
        Assert.True(reencoded.SequenceEqual(input));
    }

    [Fact]
    public void RoundTrip_Win1252_DecodeEncode_SameBytes()
    {
        Windows1252TextDecoder decoder = new();

        // 0x80 (€), 0x93 ("), 0x41 (A), 0x42 (B)
        byte[] input = [0x80, 0x93, 0x41, 0x42];
        Span<byte> reencoded = stackalloc byte[input.Length];

        int writePos = 0;
        int readPos = 0;
        while (readPos < input.Length) {
            (Rune rune, int byteLen) = decoder.DecodeRune(input, readPos);
            int written = decoder.EncodeRune(rune, reencoded[writePos..]);
            readPos += byteLen;
            writePos += written;
        }

        Assert.Equal(input.Length, writePos);
        Assert.True(reencoded.SequenceEqual(input));
    }

    [Fact]
    public void Document_ReadAndDecode_Utf16Le_CorrectText()
    {
        Utf16LeTextDecoder decoder = new();

        // UTF-16 LE BOM + "Test"
        byte[] content =
        [
            0xFF, 0xFE,                                     // BOM
            0x54, 0x00, 0x65, 0x00, 0x73, 0x00, 0x74, 0x00 // Test
        ];
        string path = CreateTempFile(content);

        try {
            using Document doc = new(path);
            Span<byte> buf = stackalloc byte[(int)doc.Length];
            int read = doc.Read(0, buf);

            // Skip BOM (2 bytes), decode runes
            StringBuilder sb = new();
            int offset = 2;
            while (offset < read) {
                (Rune rune, int byteLen) = decoder.DecodeRune(buf[..read], offset);
                sb.Append(rune.ToString());
                offset += byteLen;
            }

            Assert.Equal("Test", sb.ToString());
        } finally {
            File.Delete(path);
        }
    }
}
