using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

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

    View content = new()
    {
      X = 1,
      Y = 0,
      Width = Dim.Fill(2),
      Height = Dim.Fill(0),
      CanFocus = false,
    };

    content.DrawingContent += (_, _) =>
    {
      content.SetAttributeForRole(VisualRole.Normal);
      int vpW = content.Viewport.Width;
      int vpH = content.Viewport.Height;

      for (int row = 0; row < vpH; row++)
      {
        content.Move(0, row);
        for (int col = 0; col < vpW; col++)
          content.AddRune(' ');
      }

      Attribute nameAttr = new(Color.BrightCyan, Color.Black);
      Attribute valueAttr = new(Color.White, Color.Black);

      int lineCount = Math.Min(lines.Length, vpH);
      for (int row = 0; row < lineCount; row++)
      {
        string line = lines[row];
        int colCount = Math.Min(line.Length, vpW);
        int sepIndex = line.IndexOf(" : ", StringComparison.Ordinal);

        content.Move(0, row);
        for (int col = 0; col < colCount; col++)
        {
          if (sepIndex >= 0 && col < sepIndex)
            content.SetAttribute(nameAttr);
          else
            content.SetAttribute(valueAttr);
          content.AddRune(line[col]);
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
