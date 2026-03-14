using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Color = Terminal.Gui.Drawing.Color;

namespace Leviathan.TUI2.Widgets;

/// <summary>
/// Modal window that displays a single CSV record in vertical key:value layout.
/// Opened by pressing F2 in the CSV view. Read-only, dismissible with Esc/Enter.
/// </summary>
internal sealed class CsvRecordDetailDialog : Window
{
  internal CsvRecordDetailDialog(long rowNumber, (string Name, string Value)[] fields)
  {
    Title = $"Record #{rowNumber + 1}";
    Width = Dim.Percent(80);
    Height = Dim.Percent(80);

    string[] lines = FormatLines(fields);

    int maxLineWidth = 0;
    foreach (string line in lines)
    {
      if (line.Length > maxLineWidth)
        maxLineWidth = line.Length;
    }

    View content = new()
    {
      X = 1,
      Y = 0,
      Width = Dim.Fill(2),
      Height = Dim.Fill(0),
      CanFocus = true,
    };

    content.SetContentSize(new Size(maxLineWidth, lines.Length));
    content.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar
                              | ViewportSettingsFlags.HasHorizontalScrollBar;

    content.MouseEvent += (_, e) =>
    {
      if (e.Flags.HasFlag(MouseFlags.WheeledUp)) { content.ScrollVertical(-3); e.Handled = true; }
      else if (e.Flags.HasFlag(MouseFlags.WheeledDown)) { content.ScrollVertical(3); e.Handled = true; }
    };

    content.KeyDown += (_, e) =>
    {
      if (e == Key.CursorUp) { content.ScrollVertical(-1); e.Handled = true; }
      else if (e == Key.CursorDown) { content.ScrollVertical(1); e.Handled = true; }
      else if (e == Key.CursorLeft) { content.ScrollHorizontal(-1); e.Handled = true; }
      else if (e == Key.CursorRight) { content.ScrollHorizontal(1); e.Handled = true; }
      else if (e == Key.PageUp) { content.ScrollVertical(-content.Viewport.Height); e.Handled = true; }
      else if (e == Key.PageDown) { content.ScrollVertical(content.Viewport.Height); e.Handled = true; }
      else if (e == Key.Home)
      {
        content.Viewport = content.Viewport with { Location = new Point(0, 0) };
        e.Handled = true;
      }
      else if (e == Key.End)
      {
        int maxY = Math.Max(0, lines.Length - content.Viewport.Height);
        content.Viewport = content.Viewport with { Location = new Point(content.Viewport.Location.X, maxY) };
        e.Handled = true;
      }
    };

    content.DrawingContent += (_, _) =>
    {
      content.SetAttributeForRole(VisualRole.Normal);
      int vpW = content.Viewport.Width;
      int vpH = content.Viewport.Height;
      int scrollX = content.Viewport.Location.X;
      int scrollY = content.Viewport.Location.Y;

      for (int row = 0; row < vpH; row++)
      {
        content.Move(0, row);
        for (int col = 0; col < vpW; col++)
          content.AddRune(' ');
      }

      Attribute nameAttr = new(Color.BrightCyan, Color.Black);
      Attribute valueAttr = new(Color.White, Color.Black);

      for (int row = 0; row < vpH; row++)
      {
        int contentRow = scrollY + row;
        if (contentRow >= lines.Length) break;

        string line = lines[contentRow];
        int sepIndex = line.IndexOf(" : ", StringComparison.Ordinal);

        content.Move(0, row);
        for (int col = 0; col < vpW; col++)
        {
          int contentCol = scrollX + col;
          if (contentCol >= line.Length) break;

          if (sepIndex >= 0 && contentCol < sepIndex)
            content.SetAttribute(nameAttr);
          else
            content.SetAttribute(valueAttr);
          content.AddRune(line[contentCol]);
        }
      }
    };

    KeyDown += (_, e) =>
    {
      if (e == Key.Esc || e == Key.Enter)
      {
        RequestStop();
        e.Handled = true;
      }
    };

    Add(content);
  }

  private static string[] FormatLines((string Name, string Value)[] fields)
  {
    if (fields.Length == 0)
      return ["(empty record)"];

    int maxNameLen = 0;
    foreach ((string name, _) in fields)
    {
      if (name.Length > maxNameLen)
        maxNameLen = name.Length;
    }

    List<string> lines = [];
    foreach ((string name, string value) in fields)
    {
      string prefix = name.PadRight(maxNameLen) + " : ";
      string[] valueLines = value.Split('\n');
      for (int i = 0; i < valueLines.Length; i++)
      {
        string vline = valueLines[i].TrimEnd('\r');
        if (i == 0)
          lines.Add(prefix + vline);
        else
          lines.Add(new string(' ', prefix.Length) + vline);
      }
      lines.Add("");
    }

    return [.. lines];
  }
}
