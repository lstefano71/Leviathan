using Leviathan.Core.Text;
using Leviathan.GUI.Views;

using System.Text;

namespace Leviathan.GUI.Tests;

/// <summary>
/// Tests word-based keyboard navigation helpers used by TextViewControl.
/// </summary>
public sealed class TextViewControlWordNavigationTests
{
    [Fact]
    public void FindNextWordBoundary_FromInsideWord_JumpsToNextWordStart()
    {
        TestRuneReader reader = new("alpha beta", new Utf8TextDecoder());

        long next = TextViewControl.FindNextWordBoundary(1, 0, reader.Length, reader.ReadAt);

        Assert.Equal(6, next);
    }

    [Fact]
    public void FindPreviousWordBoundary_FromSecondWordStart_JumpsToFirstWordStart()
    {
        TestRuneReader reader = new("alpha beta", new Utf8TextDecoder());

        long previous = TextViewControl.FindPreviousWordBoundary(6, 0, reader.ReadBefore);

        Assert.Equal(0, previous);
    }

    [Fact]
    public void FindNextWordBoundary_SkipsPunctuationAndWhitespace()
    {
        TestRuneReader reader = new("one,\t two", new Utf8TextDecoder());

        long next = TextViewControl.FindNextWordBoundary(0, 0, reader.Length, reader.ReadAt);

        Assert.Equal(6, next);
    }

    [Fact]
    public void TryGetWordDeleteRange_Backward_ReturnsCurrentWordSpan()
    {
        TestRuneReader reader = new("alpha beta", new Utf8TextDecoder());

        bool ok = TextViewControl.TryGetWordDeleteRange(
            cursorOffset: reader.Length,
            bomLength: 0,
            fileLength: reader.Length,
            deleteBackward: true,
            readRuneAt: reader.ReadAt,
            readRuneBefore: reader.ReadBefore,
            out long deleteStart,
            out long deleteLength);

        Assert.True(ok);
        Assert.Equal(6, deleteStart);
        Assert.Equal(4, deleteLength);
    }

    [Fact]
    public void TryGetWordDeleteRange_ForwardFromSeparator_DeletesSeparatorAndNextWord()
    {
        TestRuneReader reader = new("alpha beta gamma", new Utf8TextDecoder());

        bool ok = TextViewControl.TryGetWordDeleteRange(
            cursorOffset: 5,
            bomLength: 0,
            fileLength: reader.Length,
            deleteBackward: false,
            readRuneAt: reader.ReadAt,
            readRuneBefore: reader.ReadBefore,
            out long deleteStart,
            out long deleteLength);

        Assert.True(ok);
        Assert.Equal(5, deleteStart);
        Assert.Equal(5, deleteLength);
    }

    [Fact]
    public void FindNextWordBoundary_Utf16_DoesNotSplitSurrogatePairs()
    {
        ITextDecoder decoder = new Utf16LeTextDecoder();
        TestRuneReader reader = new("A 😀 B", decoder);
        long expectedBOffset = decoder.EncodeString("A 😀 ").LongLength;

        long next = TextViewControl.FindNextWordBoundary(0, 0, reader.Length, reader.ReadAt);

        Assert.Equal(expectedBOffset, next);
    }

    private sealed class TestRuneReader
    {
        private readonly byte[] _bytes;
        private readonly ITextDecoder _decoder;

        internal TestRuneReader(string text, ITextDecoder decoder)
        {
            _decoder = decoder;
            _bytes = decoder.EncodeString(text);
        }

        internal long Length => _bytes.LongLength;

        internal (bool Success, Rune Rune, int ByteLength) ReadAt(long offset)
        {
            if (offset < 0 || offset >= _bytes.LongLength)
                return (false, Rune.ReplacementChar, 0);

            int start = (int)offset;
            (Rune rune, int byteLength) = _decoder.DecodeRune(_bytes, start);
            if (byteLength <= 0 || start + byteLength > _bytes.Length)
                return (false, Rune.ReplacementChar, 0);

            return (true, rune, byteLength);
        }

        internal (bool Success, long RuneStart, Rune Rune, int ByteLength) ReadBefore(long offset)
        {
            long clamped = Math.Clamp(offset, 0, _bytes.LongLength);
            if (clamped <= 0)
                return (false, 0, Rune.ReplacementChar, 0);

            int probe = (int)clamped - 1;
            while (probe >= 0) {
                int start = _decoder.AlignToCharBoundary(_bytes, probe);
                if (start < 0 || start >= clamped) {
                    probe--;
                    continue;
                }

                (Rune rune, int byteLength) = _decoder.DecodeRune(_bytes, start);
                if (byteLength <= 0 || start + byteLength > clamped) {
                    probe = start - 1;
                    continue;
                }

                return (true, start, rune, byteLength);
            }

            return (false, 0, Rune.ReplacementChar, 0);
        }
    }
}
