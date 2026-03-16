using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Leviathan.GUI.Helpers;

namespace Leviathan.GUI.Views;

/// <summary>
/// High-performance hex editor control. Renders [Address] [Hex bytes] | [ASCII]
/// via Avalonia DrawingContext — no XAML, pure custom rendering.
/// </summary>
internal sealed class HexViewControl : Control
{
    private static readonly Typeface MonoTypeface = new("Consolas, Courier New, monospace");
    private const double FontSize = 14;
    private const double LinePadding = 2;

    private readonly AppState _state;
    private readonly byte[] _readBuffer = new byte[65536];
    private ViewTheme _theme = ViewTheme.Resolve();

    /// <summary>Lookup table for zero-alloc byte-to-hex conversion.</summary>
    private static ReadOnlySpan<byte> HexChars => "0123456789ABCDEF"u8;

    public HexViewControl(AppState state)
    {
        _state = state;
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) =>
        {
            _theme = ViewTheme.Resolve();
            InvalidateVisual();
        };
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_state.Document is null) return;

        Rect bounds = Bounds;
        ViewTheme theme = _theme;

        // Paint control background
        context.FillRectangle(theme.Background, bounds);

        double charWidth = MeasureCharWidth();
        double lineHeight = FontSize + LinePadding;

        int bytesPerRow = _state.BytesPerRow;
        int visibleRows = Math.Max(1, (int)(bounds.Height / lineHeight));
        _state.VisibleRows = visibleRows;

        // Determine address column width (grows for large files)
        int addressDigits = _state.FileLength > 0xFFFF_FFFFL ? 16 : 8;
        double addressWidth = (addressDigits + 2) * charWidth; // "XXXXXXXX  "

        // Hex column: 3 chars per byte (XX + space), extra space every 8 bytes
        int groupCount = (bytesPerRow + 7) / 8;
        double hexWidth = (bytesPerRow * 3 + groupCount) * charWidth;

        // Separator
        double separatorX = addressWidth + hexWidth;

        // ASCII column
        double asciiX = separatorX + 3 * charWidth;

        // Draw separator line
        IPen separatorPen = theme.GridLinePen;
        context.DrawLine(separatorPen, new Point(separatorX + charWidth, 0),
            new Point(separatorX + charWidth, bounds.Height));

        // Read visible data
        long startOffset = _state.HexBaseOffset;
        int totalBytes = visibleRows * bytesPerRow;
        int maxRead = (int)Math.Min(totalBytes, _state.FileLength - startOffset);
        if (maxRead <= 0) return;

        int readLen = Math.Min(maxRead, _readBuffer.Length);
        _state.Document.Read(startOffset, _readBuffer.AsSpan(0, readLen));

        // Selection range
        long selStart = _state.HexSelStart;
        long selEnd = _state.HexSelEnd;

        IBrush textBrush = theme.TextPrimary;
        IBrush addressBrush = theme.TextSecondary;
        IBrush asciiBrush = theme.TextMuted;
        IBrush selectionBrush = theme.SelectionHighlight;
        IBrush cursorBrush = theme.CursorHighlight;

        for (int row = 0; row < visibleRows; row++)
        {
            long rowOffset = startOffset + (long)row * bytesPerRow;
            if (rowOffset >= _state.FileLength) break;

            double y = row * lineHeight;
            int rowStart = row * bytesPerRow;
            int rowBytes = Math.Min(bytesPerRow, readLen - rowStart);
            if (rowBytes <= 0) break;

            // Address column
            string address = addressDigits == 16
                ? rowOffset.ToString("X16")
                : rowOffset.ToString("X8");
            FormattedText addressText = new(address, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoTypeface, FontSize, addressBrush);
            context.DrawText(addressText, new Point(charWidth, y));

            // Hex bytes
            for (int col = 0; col < rowBytes; col++)
            {
                byte b = _readBuffer[rowStart + col];
                long byteOffset = rowOffset + col;
                int groupSep = col / 8;
                double hexX = addressWidth + (col * 3 + groupSep) * charWidth;

                // Selection/cursor highlight
                if (byteOffset == _state.HexCursorOffset)
                {
                    context.FillRectangle(cursorBrush,
                        new Rect(hexX, y, charWidth * 2, lineHeight));
                }
                else if (selStart >= 0 && byteOffset >= selStart && byteOffset <= selEnd)
                {
                    context.FillRectangle(selectionBrush,
                        new Rect(hexX, y, charWidth * 2, lineHeight));
                }

                char hi = (char)HexChars[b >> 4];
                char lo = (char)HexChars[b & 0xF];
                string hexPair = new string([hi, lo]);

                FormattedText hexText = new(hexPair, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, FontSize, textBrush);
                context.DrawText(hexText, new Point(hexX, y));
            }

            // ASCII column
            for (int col = 0; col < rowBytes; col++)
            {
                byte b = _readBuffer[rowStart + col];
                long byteOffset = rowOffset + col;
                double ax = asciiX + col * charWidth;

                // Selection/cursor highlight in ASCII
                if (byteOffset == _state.HexCursorOffset)
                {
                    context.FillRectangle(cursorBrush,
                        new Rect(ax, y, charWidth, lineHeight));
                }
                else if (selStart >= 0 && byteOffset >= selStart && byteOffset <= selEnd)
                {
                    context.FillRectangle(selectionBrush,
                        new Rect(ax, y, charWidth, lineHeight));
                }

                char ch = b >= 0x20 && b < 0x7F ? (char)b : '.';
                FormattedText asciiText = new(ch.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoTypeface, FontSize, asciiBrush);
                context.DrawText(asciiText, new Point(ax, y));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double MeasureCharWidth()
    {
        FormattedText measurement = new("0", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, MonoTypeface, FontSize, Brushes.White);
        return measurement.Width;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_state.Document is null) { base.OnKeyDown(e); return; }

        long fileLen = _state.FileLength;
        int bytesPerRow = _state.BytesPerRow;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        long oldCursor = _state.HexCursorOffset;
        long newCursor = oldCursor;

        switch (e.Key)
        {
            case Key.Right:
                newCursor = Math.Min(oldCursor + 1, fileLen - 1);
                _state.NibbleLow = false;
                break;
            case Key.Left:
                newCursor = Math.Max(oldCursor - 1, 0);
                _state.NibbleLow = false;
                break;
            case Key.Down:
                newCursor = Math.Min(oldCursor + bytesPerRow, fileLen - 1);
                break;
            case Key.Up:
                newCursor = Math.Max(oldCursor - bytesPerRow, 0);
                break;
            case Key.PageDown:
                newCursor = Math.Min(oldCursor + (long)_state.VisibleRows * bytesPerRow, fileLen - 1);
                break;
            case Key.PageUp:
                newCursor = Math.Max(oldCursor - (long)_state.VisibleRows * bytesPerRow, 0);
                break;
            case Key.Home:
                newCursor = ctrl ? 0 : oldCursor - (oldCursor % bytesPerRow);
                break;
            case Key.End:
                newCursor = ctrl ? fileLen - 1 : Math.Min(oldCursor - (oldCursor % bytesPerRow) + bytesPerRow - 1, fileLen - 1);
                break;
            default:
                // Hex digit editing
                int hexDigit = GetHexDigit(e.Key);
                if (hexDigit >= 0 && _state.HexCursorOffset >= 0)
                {
                    InsertHexNibble(hexDigit);
                    e.Handled = true;
                    InvalidateVisual();
                    return;
                }
                base.OnKeyDown(e);
                return;
        }

        // Selection handling
        if (shift)
        {
            if (_state.HexSelectionAnchor < 0)
                _state.HexSelectionAnchor = oldCursor;
        }
        else
        {
            _state.HexSelectionAnchor = -1;
        }

        _state.HexCursorOffset = newCursor;
        EnsureCursorVisible();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (_state.Document is null) return;

        Point pos = e.GetPosition(this);
        long offset = HitTest(pos);
        if (offset >= 0)
        {
            _state.HexCursorOffset = offset;
            _state.HexSelectionAnchor = -1;
            _state.NibbleLow = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_state.Document is null) return;

        int rows = e.Delta.Y > 0 ? -3 : 3;
        long newBase = _state.HexBaseOffset + (long)rows * _state.BytesPerRow;
        newBase = Math.Max(0, newBase);
        newBase = Math.Min(newBase, Math.Max(0, _state.FileLength - (long)_state.VisibleRows * _state.BytesPerRow));
        // Align to row boundary
        newBase -= newBase % _state.BytesPerRow;
        _state.HexBaseOffset = newBase;
        InvalidateVisual();
    }

    /// <summary>Navigates to a specific byte offset.</summary>
    internal void GotoOffset(long offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _state.FileLength - 1));
        _state.HexCursorOffset = offset;
        _state.HexSelectionAnchor = -1;
        _state.NibbleLow = false;
        EnsureCursorVisible();
        InvalidateVisual();
    }

    private void EnsureCursorVisible()
    {
        long cursor = _state.HexCursorOffset;
        int bytesPerRow = _state.BytesPerRow;
        long baseOffset = _state.HexBaseOffset;
        int visibleRows = _state.VisibleRows;

        if (cursor < baseOffset)
        {
            _state.HexBaseOffset = (cursor / bytesPerRow) * bytesPerRow;
        }
        else if (cursor >= baseOffset + (long)visibleRows * bytesPerRow)
        {
            _state.HexBaseOffset = ((cursor / bytesPerRow) - visibleRows + 1) * bytesPerRow;
        }
    }

    private long HitTest(Point point)
    {
        double charWidth = MeasureCharWidth();
        double lineHeight = FontSize + LinePadding;
        int bytesPerRow = _state.BytesPerRow;

        int row = (int)(point.Y / lineHeight);
        if (row < 0 || row >= _state.VisibleRows) return -1;

        int addressDigits = _state.FileLength > 0xFFFF_FFFFL ? 16 : 8;
        double addressWidth = (addressDigits + 2) * charWidth;

        double hexX = point.X - addressWidth;
        if (hexX >= 0)
        {
            int groupCount = (bytesPerRow + 7) / 8;
            double totalHexWidth = (bytesPerRow * 3 + groupCount) * charWidth;

            if (hexX < totalHexWidth)
            {
                // Hit in hex area — approximate column
                int approxCol = (int)(hexX / (3 * charWidth));
                approxCol = Math.Clamp(approxCol, 0, bytesPerRow - 1);
                long offset = _state.HexBaseOffset + (long)row * bytesPerRow + approxCol;
                return Math.Min(offset, _state.FileLength - 1);
            }
        }

        return _state.HexBaseOffset + (long)row * bytesPerRow;
    }

    private void InsertHexNibble(int digit)
    {
        if (_state.Document is null || _state.HexCursorOffset < 0) return;

        long offset = _state.HexCursorOffset;
        Span<byte> current = stackalloc byte[1];
        if (offset < _state.FileLength)
            _state.Document.Read(offset, current);
        else
            current[0] = 0;

        byte value;
        if (!_state.NibbleLow)
        {
            value = (byte)((digit << 4) | (current[0] & 0x0F));
            _state.NibbleLow = true;
        }
        else
        {
            value = (byte)((current[0] & 0xF0) | digit);
            _state.NibbleLow = false;
        }

        if (offset < _state.FileLength)
        {
            _state.Document.Delete(offset, 1);
            _state.Document.Insert(offset, [value]);
        }
        else
        {
            _state.Document.Insert(offset, [value]);
        }

        if (!_state.NibbleLow)
        {
            _state.HexCursorOffset = Math.Min(offset + 1, _state.Document.Length - 1);
        }
    }

    private static int GetHexDigit(Key key) => key switch
    {
        Key.D0 or Key.NumPad0 => 0,
        Key.D1 or Key.NumPad1 => 1,
        Key.D2 or Key.NumPad2 => 2,
        Key.D3 or Key.NumPad3 => 3,
        Key.D4 or Key.NumPad4 => 4,
        Key.D5 or Key.NumPad5 => 5,
        Key.D6 or Key.NumPad6 => 6,
        Key.D7 or Key.NumPad7 => 7,
        Key.D8 or Key.NumPad8 => 8,
        Key.D9 or Key.NumPad9 => 9,
        Key.A => 10,
        Key.B => 11,
        Key.C => 12,
        Key.D => 13,
        Key.E => 14,
        Key.F => 15,
        _ => -1
    };
}
