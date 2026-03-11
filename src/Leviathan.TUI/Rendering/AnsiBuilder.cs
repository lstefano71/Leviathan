using System.Text;
using Hex1b.Theming;
using Leviathan.Core.Search;

namespace Leviathan.TUI.Rendering;

/// <summary>
/// Builds ANSI-colored terminal strings for hex1b rendering.
/// SurfaceRenderContext parses embedded ANSI codes into SurfaceCell properties.
/// </summary>
internal static class AnsiBuilder
{
    private const string Reset = "\x1b[0m";

    // Hex View palette
    internal static readonly Hex1bColor OffsetColor = Hex1bColor.FromRgb(128, 178, 255);
    internal static readonly Hex1bColor HexColor = Hex1bColor.FromRgb(230, 230, 230);
    internal static readonly Hex1bColor HexZeroColor = Hex1bColor.FromRgb(102, 102, 102);
    internal static readonly Hex1bColor AsciiColor = Hex1bColor.FromRgb(153, 230, 153);
    internal static readonly Hex1bColor SeparatorColor = Hex1bColor.FromRgb(77, 77, 77);

    // Text View palette
    internal static readonly Hex1bColor TextColor = Hex1bColor.FromRgb(230, 230, 230);
    internal static readonly Hex1bColor LineNumColor = Hex1bColor.FromRgb(128, 153, 179);
    internal static readonly Hex1bColor ControlColor = Hex1bColor.FromRgb(102, 102, 102);
    internal static readonly Hex1bColor WrapIndicatorColor = Hex1bColor.FromRgb(77, 128, 179);

    // Selection / cursor
    internal static readonly Hex1bColor SelectionBg = Hex1bColor.FromRgb(51, 102, 204);
    internal static readonly Hex1bColor CursorBg = Hex1bColor.FromRgb(230, 153, 26);

    // Search match highlighting
    internal static readonly Hex1bColor MatchBg = Hex1bColor.FromRgb(100, 100, 40);
    internal static readonly Hex1bColor ActiveMatchBg = Hex1bColor.FromRgb(180, 140, 30);

    // Status bar
    internal static readonly Hex1bColor StatusFg = Hex1bColor.FromRgb(180, 180, 180);
    internal static readonly Hex1bColor StatusBg = Hex1bColor.FromRgb(30, 30, 40);
    internal static readonly Hex1bColor AccentFg = Hex1bColor.FromRgb(100, 180, 255);

    // Cache foreground ANSI escape codes to avoid repeated allocations
    private static readonly string OffsetFg = OffsetColor.ToForegroundAnsi();
    private static readonly string HexFg = HexColor.ToForegroundAnsi();
    private static readonly string HexZeroFg = HexZeroColor.ToForegroundAnsi();
    private static readonly string AsciiFg = AsciiColor.ToForegroundAnsi();
    private static readonly string SepFg = SeparatorColor.ToForegroundAnsi();
    private static readonly string TextFg = TextColor.ToForegroundAnsi();
    private static readonly string LineNumFg = LineNumColor.ToForegroundAnsi();
    private static readonly string CtrlFg = ControlColor.ToForegroundAnsi();
    private static readonly string WrapFg = WrapIndicatorColor.ToForegroundAnsi();
    private static readonly string SelBg = SelectionBg.ToBackgroundAnsi();
    private static readonly string CurBg = CursorBg.ToBackgroundAnsi();
    private static readonly string MatchBgAnsi = MatchBg.ToBackgroundAnsi();
    private static readonly string ActiveMatchBgAnsi = ActiveMatchBg.ToBackgroundAnsi();

    private static readonly char[] HexChars = "0123456789ABCDEF".ToCharArray();

    /// <summary>
    /// Formats a hex view offset like "00000000:00000010".
    /// </summary>
    internal static string FormatOffset(long offset)
    {
        Span<char> buf = stackalloc char[17];
        uint hi = (uint)(offset >> 32);
        uint lo = (uint)(offset & 0xFFFFFFFF);
        for (int i = 7; i >= 0; i--) { buf[i] = HexChars[(int)(hi & 0xF)]; hi >>= 4; }
        buf[8] = ':';
        for (int i = 16; i >= 9; i--) { buf[i] = HexChars[(int)(lo & 0xF)]; lo >>= 4; }
        return new string(buf);
    }

    /// <summary>
    /// Builds one complete hex view row with ANSI color codes.
    /// </summary>
    internal static string BuildHexRow(
        long offset,
        ReadOnlySpan<byte> rowBytes,
        int bytesPerRow,
        long cursorOffset,
        long selStart,
        long selEnd,
        List<SearchResult>? visibleMatches = null,
        SearchResult? activeMatch = null)
    {
        StringBuilder sb = new(bytesPerRow * 5 + 40);

        // Offset column
        sb.Append(OffsetFg);
        sb.Append(FormatOffset(offset));
        sb.Append(Reset);
        sb.Append(' ');

        // Hex column
        for (int col = 0; col < bytesPerRow; col++)
        {
            if (col > 0 && col % 8 == 0)
                sb.Append(' ');

            long byteOffset = offset + col;
            if (col < rowBytes.Length)
            {
                byte b = rowBytes[col];
                string? bg = GetByteBg(byteOffset, cursorOffset, selStart, selEnd, visibleMatches, activeMatch);

                if (bg is not null)
                    sb.Append(bg);

                sb.Append(b == 0 ? HexZeroFg : HexFg);
                sb.Append(HexChars[b >> 4]);
                sb.Append(HexChars[b & 0xF]);
                sb.Append(Reset);
                sb.Append(' ');
            }
            else
            {
                sb.Append("   ");
            }
        }

        // Separator
        sb.Append(SepFg);
        sb.Append('│');
        sb.Append(Reset);

        // ASCII column
        for (int col = 0; col < rowBytes.Length; col++)
        {
            long byteOffset = offset + col;
            byte b = rowBytes[col];
            string? bg = GetByteBg(byteOffset, cursorOffset, selStart, selEnd, visibleMatches, activeMatch);

            if (bg is not null)
                sb.Append(bg);

            sb.Append(AsciiFg);
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
            sb.Append(Reset);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a text view line with ANSI color codes, including cursor, selection, and search match highlighting.
    /// </summary>
    internal static string BuildTextLine(
        ReadOnlySpan<char> text,
        long lineNumber,
        bool isHardLine,
        long lineStartOffset,
        int lineByteLen,
        long cursorOffset,
        long selStart,
        long selEnd,
        int tabWidth,
        bool isLastVisibleLine = true,
        List<int>? charByteOffsets = null,
        List<SearchResult>? visibleMatches = null,
        SearchResult? activeMatch = null)
    {
        StringBuilder sb = new(text.Length + 80);

        // Line number gutter
        if (isHardLine && lineNumber >= 0)
        {
            sb.Append(LineNumFg);
            string num = lineNumber.ToString();
            int pad = 8 - num.Length;
            for (int i = 0; i < pad; i++) sb.Append(' ');
            sb.Append(num);
            sb.Append(Reset);
            sb.Append(' ');
        }
        else if (!isHardLine)
        {
            sb.Append(WrapFg);
            sb.Append("       ↪");
            sb.Append(Reset);
            sb.Append(' ');
        }
        else
        {
            sb.Append("         ");
        }

        // Text content — character by character with cursor/selection/match highlights
        bool hasOffsets = charByteOffsets is not null && charByteOffsets.Count == text.Length;
        long lineEnd = lineStartOffset + lineByteLen;
        long lastDisplayedDocOffset = -1;

        for (int ci = 0; ci < text.Length; ci++)
        {
            char c = text[ci];

            // Compute the document byte offset for this display char
            long charDocOffset = hasOffsets
                ? lineStartOffset + charByteOffsets![ci]
                : -1;

            if (charDocOffset >= 0)
                lastDisplayedDocOffset = charDocOffset;

            // Determine background: cursor > selection > active match > match > none
            string? bg = null;
            if (hasOffsets && charDocOffset == cursorOffset)
            {
                bg = CurBg;
            }
            else if (hasOffsets && selStart >= 0 && charDocOffset >= selStart && charDocOffset <= selEnd)
            {
                bg = SelBg;
            }
            else if (hasOffsets && visibleMatches is not null && charDocOffset >= 0)
            {
                // Check active match first (higher priority highlight)
                if (activeMatch is not null &&
                    charDocOffset >= activeMatch.Value.Offset &&
                    charDocOffset < activeMatch.Value.Offset + activeMatch.Value.Length)
                {
                    bg = ActiveMatchBgAnsi;
                }
                else
                {
                    for (int m = 0; m < visibleMatches.Count; m++)
                    {
                        SearchResult match = visibleMatches[m];
                        if (charDocOffset >= match.Offset && charDocOffset < match.Offset + match.Length)
                        {
                            bg = MatchBgAnsi;
                            break;
                        }
                        if (match.Offset > charDocOffset) break;
                    }
                }
            }

            // Determine foreground and char to display
            if (bg is not null)
                sb.Append(bg);

            if (c < 0x20 && c != '\t')
            {
                sb.Append(CtrlFg);
                sb.Append('.');
            }
            else if (c == '\t')
            {
                sb.Append(TextFg);
                for (int i = 0; i < tabWidth; i++) sb.Append(' ');
            }
            else
            {
                sb.Append(TextFg);
                sb.Append(c);
            }

            if (bg is not null)
                sb.Append(Reset);
        }

        // Show block cursor past the last displayed character.
        // Case 1: cursor is on a non-displayed byte within this line (e.g. newline char
        //         that DecodeLineToDisplay skips). Cursor is in [lineStartOffset, lineEnd)
        //         but past the last displayed char offset.
        // Case 2: cursor == lineEnd AND this is the last visible line (true EOF — no more
        //         lines after this one). This handles the cursor-past-end-of-file position.
        if (hasOffsets && cursorOffset >= lineStartOffset)
        {
            bool cursorOnHiddenByte = cursorOffset > lastDisplayedDocOffset && cursorOffset < lineEnd;
            bool cursorAtEof = cursorOffset == lineEnd && isLastVisibleLine;
            if (cursorOnHiddenByte || cursorAtEof)
            {
                sb.Append(CurBg);
                sb.Append(TextFg);
                sb.Append(' ');
                sb.Append(Reset);
            }
        }

        sb.Append(Reset);
        return sb.ToString();
    }

    /// <summary>
    /// Returns the appropriate background ANSI code for a byte at the given offset,
    /// considering cursor, selection, active match, and other matches. Returns null if no highlight needed.
    /// </summary>
    private static string? GetByteBg(
        long byteOffset,
        long cursorOffset,
        long selStart,
        long selEnd,
        List<SearchResult>? visibleMatches,
        SearchResult? activeMatch)
    {
        if (byteOffset == cursorOffset)
            return CurBg;
        if (selStart >= 0 && byteOffset >= selStart && byteOffset <= selEnd)
            return SelBg;
        if (visibleMatches is null || visibleMatches.Count == 0)
            return null;

        if (activeMatch is not null &&
            byteOffset >= activeMatch.Value.Offset &&
            byteOffset < activeMatch.Value.Offset + activeMatch.Value.Length)
            return ActiveMatchBgAnsi;

        for (int i = 0; i < visibleMatches.Count; i++)
        {
            SearchResult m = visibleMatches[i];
            if (byteOffset >= m.Offset && byteOffset < m.Offset + m.Length)
                return MatchBgAnsi;
            if (m.Offset > byteOffset)
                break;
        }

        return null;
    }
}
