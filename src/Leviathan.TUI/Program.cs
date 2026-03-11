using Hex1b;
using Hex1b.Events;
using Hex1b.Input;
using Hex1b.Widgets;

using Leviathan.Core;
using Leviathan.Core.Search;
using Leviathan.Core.Text;
using Leviathan.TUI;
using Leviathan.TUI.Views;
using Leviathan.TUI.Widgets;

// ─── Console encoding ───
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

// ─── Application state ───
AppState state = new();
HexViewController hexView = new(state);
TextViewController textView = new(state);
CommandPalette palette = new(state);
FileBrowserController fileBrowser = new(state);
Hex1bApp? appRef = null;

// Apply settings
state.BytesPerRowSetting = state.Settings.BytesPerRow;
state.WordWrap = state.Settings.WordWrap;

// Open file from CLI argument
if (args.Length > 0 && File.Exists(args[0]))
  state.OpenFile(args[0]);

// ─── Register commands ───
RegisterCommands();

// ─── Terminal title ───
void UpdateTitle()
{
  string title = state.CurrentFilePath is null
      ? "Leviathan — Large File Editor"
      : state.IsModified
          ? $"● {Path.GetFileName(state.CurrentFilePath)} — Leviathan"
          : $"{Path.GetFileName(state.CurrentFilePath)} — Leviathan";
  Console.Write($"\x1b]0;{title}\x07");
}

// ─── Build UI ───
await using var terminal = Hex1bTerminal.CreateBuilder()
    .WithMouse()
    .WithHex1bApp((app, options) => {
      appRef = app;
      return ctx => {
        UpdateTitle();

        int width = Console.WindowWidth;
        int height = Console.WindowHeight;

        // Modal overlays (highest priority first)
        if (state.ShowSaveError)
          return BuildSaveErrorView(ctx, width);

        if (state.ShowUnsavedDialog)
          return BuildUnsavedDialogView(ctx);

        if (state.ShowCommandPalette)
          return BuildCommandPaletteView(ctx, width, height);

        if (state.ShowFileOpenDialog)
          return BuildFileOpenView(ctx);

        if (state.ShowFileBrowser)
          return BuildFileBrowserView(ctx, width, height);

        if (state.ShowGotoDialog)
          return BuildGotoView(ctx);

        return BuildMainView(ctx, width, height);
      };
    })
    .Build();

await terminal.RunAsync();

// Clean up
state.CancelSearch();
state.Document?.Dispose();

// ─── View builders ───

Hex1bWidget BuildMainView(RootContext ctx, int width, int height)
{
  Hex1bWidget interactable = ctx.Interactable(ic => {
    Hex1bWidget content;

    if (state.Document is not null) {
      string[] rows = state.ActiveView == ViewMode.Hex
          ? hexView.RenderRows(width, height)
          : textView.RenderRows(width, height);

      content = ic.VStack(v => {
        Hex1bWidget[] lines = new Hex1bWidget[rows.Length];
        for (int i = 0; i < rows.Length; i++)
          lines[i] = v.Text(rows[i]);
        return lines;
      }).FillHeight();
    } else {
      List<string> mru = state.Settings.RecentFiles;
      content = ic.VStack(v => {
        List<Hex1bWidget> lines =
        [
            v.Text(""),
                    v.Text("  Leviathan — Terminal Hex + Text Editor"),
                    v.Text("")
        ];

        if (mru.Count > 0) {
          lines.Add(v.Text("  Recent files:"));
          for (int idx = 0; idx < mru.Count; idx++) {
            int num = idx + 1;
            string shortcut = num <= 9 ? num.ToString() : " ";
            lines.Add(v.Text($"    [{shortcut}] {mru[idx]}"));
          }
          lines.Add(v.Text(""));
        }

        lines.Add(v.Text("  Open a file:  Ctrl+O  or  pass a file path as argument"));
        lines.Add(v.Text("  Command palette:  Ctrl+P"));
        lines.Add(v.Text("  Quit:  Ctrl+Q"));
        return lines.ToArray();
      }).FillHeight();
    }

    Hex1bWidget statusBar = ic.Text(BuildStatusBarText(width));

    if (state.ShowFindBar) {
      Hex1bWidget findBar = ic.Text(BuildFindBarText());
      return ic.VStack(v => [content, findBar, statusBar]);
    }

    return ic.VStack(v => [content, statusBar]);
  }).WithInputBindings(bindings => {
    // Global shortcuts
    bindings.Ctrl().Key(Hex1bKey.Q).Action(() => GuardUnsavedChanges(() => appRef?.RequestStop()), "Quit");
    bindings.Ctrl().Key(Hex1bKey.P).Action(() => {
      if (state.ShowCommandPalette)
        palette.Close();
      else
        palette.Open();
    }, "Toggle Command Palette");
    bindings.Ctrl().Key(Hex1bKey.O).Action(() => GuardUnsavedChanges(PromptOpenFile), "Open File");
    bindings.Ctrl().Key(Hex1bKey.S).Action(() => SaveFile(), "Save");
    bindings.Ctrl().Key(Hex1bKey.G).Action(() => OpenGoto(), "Goto");
    bindings.Ctrl().Key(Hex1bKey.F).Action(() => ToggleFindBar(), "Find");
    bindings.Key(Hex1bKey.F3).Action(() => FindNext(), "Find Next");
    bindings.Key(Hex1bKey.F5).Action(() => { state.ActiveView = ViewMode.Hex; }, "Hex View");
    bindings.Key(Hex1bKey.F6).Action(() => { state.ActiveView = ViewMode.Text; }, "Text View");
    bindings.Key(Hex1bKey.Escape).Action(() => {
      if (state.ShowFindBar) state.ShowFindBar = false;
    }, "Close");

    // When find bar is active, intercept character input for the search field
    if (state.ShowFindBar)
      BindFindBarInput(bindings);
    else if (state.Document is null)
      BindWelcomeInput(bindings);
    else if (state.ActiveView == ViewMode.Hex)
      BindHexNavigation(bindings);
    else
      BindTextNavigation(bindings);
  });

  // Wrap with PastableWidget to handle terminal bracketed paste (Ctrl+V)
  return ctx.Pastable(interactable)
      .OnPaste(async (PasteEventArgs e) => {
        string pastedText = await e.Paste.ReadToEndAsync();
        if (string.IsNullOrEmpty(pastedText)) return;

        if (state.ActiveView == ViewMode.Hex)
          hexView.Paste(pastedText);
        else
          textView.Paste(pastedText);

        appRef?.Invalidate();
      })
      .WithMaxSize(10 * 1024 * 1024);
}

Hex1bWidget BuildCommandPaletteView(RootContext ctx, int width, int height)
{
  IReadOnlyList<Command> filtered = palette.FilteredCommands;
  int sel = palette.SelectedIndex;

  return ctx.Interactable(ic =>
      ic.VStack(v => {
        List<Hex1bWidget> items = [];
        string header = $" > {palette.Query}█";
        items.Add(v.Text(header));
        items.Add(v.Text(new string('─', Math.Min(60, width))));

        int maxShow = Math.Min(filtered.Count, height - 4);
        for (int i = 0; i < maxShow; i++) {
          Command cmd = filtered[i];
          string prefix = i == sel ? " ▸ " : "   ";
          string shortcutPad = cmd.Shortcut.Length > 0
                ? cmd.Shortcut.PadLeft(Math.Max(0, 50 - cmd.Category.Length - cmd.Name.Length - 4))
                : "";
          string line = $"{prefix}[{cmd.Category}] {cmd.Name}  {shortcutPad}";
          items.Add(v.Text(line));
        }

        return items.ToArray();
      })
  ).WithInputBindings(bindings => {
    bindings.Key(Hex1bKey.Escape).Action(() => palette.Close(), "Close Palette");
    bindings.Ctrl().Key(Hex1bKey.P).Action(() => palette.Close(), "Close Palette");
    bindings.Key(Hex1bKey.UpArrow).Action(() => palette.MoveUp(), "Up");
    bindings.Key(Hex1bKey.DownArrow).Action(() => palette.MoveDown(), "Down");
    bindings.Key(Hex1bKey.Enter).Action(() => palette.Execute(), "Execute");
    bindings.Key(Hex1bKey.Backspace).Action(() => {
      if (palette.Query.Length > 0)
        palette.Query = palette.Query[..^1];
    }, "Delete Char");

    bindings.AnyCharacter().Action(text => { if (IsSafeText(text)) palette.Query += text; }, "Type");
  });
}

Hex1bWidget BuildGotoView(RootContext ctx)
{
  string label = state.ActiveView == ViewMode.Hex
      ? "Goto Offset (hex):"
      : "Goto Line:";

  return ctx.Interactable(ic =>
      ic.VStack(v => [
          v.Text(""),
            v.Text($"  {label}"),
            v.Text($"  > {state.GotoInput}█"),
            v.Text(""),
            v.Text("  Press Enter to confirm, Ctrl+G or Escape to cancel"),
      ])
  ).WithInputBindings(bindings => {
    bindings.Key(Hex1bKey.Escape).Action(() => {
      state.ShowGotoDialog = false;
      state.GotoInput = "";
    }, "Cancel");
    bindings.Ctrl().Key(Hex1bKey.G).Action(() => {
      state.ShowGotoDialog = false;
      state.GotoInput = "";
    }, "Cancel");
    bindings.Key(Hex1bKey.Enter).Action(() => {
      ExecuteGoto();
      state.ShowGotoDialog = false;
      state.GotoInput = "";
    }, "Confirm");
    bindings.Key(Hex1bKey.Backspace).Action(() => {
      if (state.GotoInput.Length > 0)
        state.GotoInput = state.GotoInput[..^1];
    }, "Delete");

    bindings.AnyCharacter().Action(text => { if (IsSafeText(text)) state.GotoInput += text; }, "Type");
  });
}

Hex1bWidget BuildFileOpenView(RootContext ctx)
{
  return ctx.Interactable(ic =>
      ic.VStack(v => [
          v.Text(""),
            v.Text("  Open File:"),
            v.Text($"  > {state.FileOpenInput}█"),
            v.Text(""),
            v.Text("  Type the full path and press Enter. Escape to cancel."),
      ])
  ).WithInputBindings(bindings => {
    bindings.Key(Hex1bKey.Escape).Action(() => {
      state.ShowFileOpenDialog = false;
      state.FileOpenInput = "";
    }, "Cancel");
    bindings.Key(Hex1bKey.Enter).Action(() => {
      string path = state.FileOpenInput.Trim().Trim('"');
      state.ShowFileOpenDialog = false;
      state.FileOpenInput = "";
      if (File.Exists(path))
        state.OpenFile(path);
    }, "Open");
    bindings.Key(Hex1bKey.Backspace).Action(() => {
      if (state.FileOpenInput.Length > 0)
        state.FileOpenInput = state.FileOpenInput[..^1];
    }, "Delete");

    bindings.AnyCharacter().Action(text => { if (IsSafeText(text)) state.FileOpenInput += text; }, "Type");
  });
}

Hex1bWidget BuildUnsavedDialogView(RootContext ctx)
{
  string fileName = state.CurrentFilePath is not null
      ? Path.GetFileName(state.CurrentFilePath)
      : "Untitled";

  return ctx.Interactable(ic =>
      ic.VStack(v => [
          v.Text(""),
            v.Text($"  The file '{fileName}' has unsaved changes."),
            v.Text(""),
            v.Text("  [S] Save      [D] Don't Save      [Esc] Cancel"),
            v.Text(""),
      ])
  ).WithInputBindings(bindings => {
    bindings.Key(Hex1bKey.Escape).Action(() => {
      state.ShowUnsavedDialog = false;
      state.PendingAction = null;
    }, "Cancel");
    // Explicit key bindings as primary (work even without focus)
    bindings.Key(Hex1bKey.S).Action(() => {
      SaveFile();
      state.ShowUnsavedDialog = false;
      Action? action = state.PendingAction;
      state.PendingAction = null;
      action?.Invoke();
    }, "Save");
    bindings.Key(Hex1bKey.D).Action(() => {
      state.ShowUnsavedDialog = false;
      Action? action = state.PendingAction;
      state.PendingAction = null;
      action?.Invoke();
    }, "Don't Save");
  });
}

Hex1bWidget BuildSaveErrorView(RootContext ctx, int width)
{
  return ctx.Interactable(ic =>
      ic.VStack(v => [
          v.Text(""),
            v.Text("  Save Failed"),
            v.Text(""),
            v.Text($"  {state.SaveErrorMessage}"),
            v.Text(""),
            v.Text("  Press any key to dismiss."),
            v.Text(""),
      ])
  ).WithInputBindings(bindings => {
    bindings.Key(Hex1bKey.Escape).Action(() => { state.ShowSaveError = false; }, "Dismiss");
    bindings.Key(Hex1bKey.Enter).Action(() => { state.ShowSaveError = false; }, "Dismiss");
    bindings.AnyCharacter().Action(text => { if (IsSafeText(text)) state.ShowSaveError = false; }, "Dismiss");
  });
}

Hex1bWidget BuildFileBrowserView(RootContext ctx, int width, int height)
{
  string[] rows = fileBrowser.RenderRows(width, height);

  return ctx.Interactable(ic =>
      ic.VStack(v => {
        Hex1bWidget[] items = new Hex1bWidget[rows.Length];
        for (int i = 0; i < rows.Length; i++)
          items[i] = v.Text(rows[i]);
        return items;
      })
  ).WithInputBindings(bindings => {
    int visibleRows = Math.Max(1, height - 6);

    bindings.Key(Hex1bKey.Escape).Action(() => {
      state.ShowFileBrowser = false;
    }, "Close Browser");

    bindings.Ctrl().Key(Hex1bKey.O).Action(() => {
      state.ShowFileBrowser = false;
    }, "Close Browser");

    bindings.Key(Hex1bKey.UpArrow).Action(() => fileBrowser.MoveUp(visibleRows), "Up");
    bindings.Key(Hex1bKey.DownArrow).Action(() => fileBrowser.MoveDown(visibleRows), "Down");
    bindings.Key(Hex1bKey.PageUp).Action(() => fileBrowser.PageUp(visibleRows), "Page Up");
    bindings.Key(Hex1bKey.PageDown).Action(() => fileBrowser.PageDown(visibleRows), "Page Down");

    bindings.Key(Hex1bKey.Enter).Action(() => {
      string? selectedPath = fileBrowser.Activate();
      if (selectedPath is not null) {
        state.ShowFileBrowser = false;
        state.OpenFile(selectedPath);
      }
    }, "Open");

    bindings.Key(Hex1bKey.Backspace).Action(() => {
      if (fileBrowser.Filter.Length > 0)
        fileBrowser.Filter = fileBrowser.Filter[..^1];
      else
        fileBrowser.GoUp();
    }, "Backspace / Go Up");

    bindings.AnyCharacter().Action(text => {
      if (IsSafeText(text)) fileBrowser.Filter += text;
    }, "Filter");
  });
}

// ─── Status bar formatting ───

string BuildStatusBarText(int width)
{
  string bar;
  if (state.Document is null) {
    bar = " No file open";
  } else if (state.ActiveView == ViewMode.Hex) {
    string mod = state.IsModified ? "● " : "";
    bar = $" {mod}[HEX] Size: {state.FileLength:N0} bytes | Offset: {state.HexCursorOffset:X} | {state.BytesPerRow} B/row";
  } else {
    string mod = state.IsModified ? "● " : "";
    string enc = state.Decoder.Encoding switch {
      TextEncoding.Utf8 => "UTF-8",
      TextEncoding.Utf16Le => "UTF-16 LE",
      TextEncoding.Windows1252 => "W-1252",
      _ => "UTF-8"
    };
    string wrap = state.WordWrap ? "Wrap" : "NoWrap";
    bar = $" {mod}[TEXT] Size: {state.FileLength:N0} bytes | Offset: {state.TextCursorOffset:X} | {enc} | {wrap}";
  }

  if (state.SearchResults.Count > 0 && state.CurrentMatchIndex >= 0)
    bar += $" | Match {state.CurrentMatchIndex + 1}/{state.SearchResults.Count}";
  else if (state.IsSearching)
    bar += " | Searching…";

  return bar.PadRight(width);
}

string BuildFindBarText()
{
  string mode = state.FindHexMode ? "HEX" : "TXT";
  string cs = state.FindCaseSensitive ? "Aa" : "aa";
  string status = state.SearchStatus;
  return $" Find [{mode}] [{cs}]: {state.FindInput}█  {status}  (Enter=find, F3=next, Tab=mode, Ctrl+F=close)";
}

// ─── Find bar input bindings ───

void BindFindBarInput(InputBindingsBuilder bindings)
{
  bindings.Key(Hex1bKey.Enter).Action(() => StartSearch(), "Search");
  bindings.Key(Hex1bKey.F3).Action(() => FindNext(), "Next Match");
  bindings.Key(Hex1bKey.Tab).Action(() => {
    state.FindHexMode = !state.FindHexMode;
  }, "Toggle Mode");
  bindings.Ctrl().Key(Hex1bKey.I).Action(() => {
    state.FindCaseSensitive = !state.FindCaseSensitive;
  }, "Toggle Case");
  bindings.Key(Hex1bKey.Backspace).Action(() => {
    if (state.FindInput.Length > 0)
      state.FindInput = state.FindInput[..^1];
  }, "Delete");
  bindings.AnyCharacter().Action(text => { if (IsSafeText(text)) state.FindInput += text; }, "Type");
}

// ─── Welcome screen bindings (MRU number keys) ───

void BindWelcomeInput(InputBindingsBuilder bindings)
{
  bindings.AnyCharacter().Action(text => {
    if (text.Length != 1) return;
    char c = text[0];
    if (c is >= '1' and <= '9') {
      int idx = c - '1';
      List<string> mru = state.Settings.RecentFiles;
      if (idx < mru.Count && File.Exists(mru[idx]))
        state.OpenFile(mru[idx]);
    }
  }, "Open Recent");
}

// ─── Navigation bindings ───

void BindHexNavigation(InputBindingsBuilder bindings)
{
  bindings.Key(Hex1bKey.LeftArrow).Action(() => hexView.MoveLeft(false), "Left");
  bindings.Key(Hex1bKey.RightArrow).Action(() => hexView.MoveRight(false), "Right");
  bindings.Key(Hex1bKey.UpArrow).Action(() => hexView.MoveUp(false), "Up");
  bindings.Key(Hex1bKey.DownArrow).Action(() => hexView.MoveDown(false), "Down");
  bindings.Key(Hex1bKey.PageUp).Action(() => hexView.PageUp(false), "Page Up");
  bindings.Key(Hex1bKey.PageDown).Action(() => hexView.PageDown(false), "Page Down");
  bindings.Key(Hex1bKey.Home).Action(() => hexView.Home(false), "Home");
  bindings.Key(Hex1bKey.End).Action(() => hexView.End(false), "End");
  bindings.Ctrl().Key(Hex1bKey.Home).Action(() => hexView.CtrlHome(false), "Start");
  bindings.Ctrl().Key(Hex1bKey.End).Action(() => hexView.CtrlEnd(false), "End of File");

  bindings.Shift().Key(Hex1bKey.LeftArrow).Action(() => hexView.MoveLeft(true), "Select Left");
  bindings.Shift().Key(Hex1bKey.RightArrow).Action(() => hexView.MoveRight(true), "Select Right");
  bindings.Shift().Key(Hex1bKey.UpArrow).Action(() => hexView.MoveUp(true), "Select Up");
  bindings.Shift().Key(Hex1bKey.DownArrow).Action(() => hexView.MoveDown(true), "Select Down");

  bindings.Key(Hex1bKey.Backspace).Action(() => hexView.BackspaceAtCursor(), "Backspace");
  bindings.Key(Hex1bKey.Delete).Action(() => hexView.DeleteAtCursor(), "Delete");

  bindings.Ctrl().Key(Hex1bKey.C).Action(() => {
    string? hex = hexView.CopySelection();
    if (hex is not null) appRef?.CopyToClipboard(hex);
  }, "Copy");
  bindings.Ctrl().Key(Hex1bKey.X).Action(() => {
    string? hex = hexView.CopySelection();
    if (hex is not null) {
      appRef?.CopyToClipboard(hex);
      hexView.DeleteAtCursor();
    }
  }, "Cut");
  bindings.Ctrl().Key(Hex1bKey.A).Action(() => hexView.SelectAll(), "Select All");

  bindings.AnyCharacter().Action(text => {
    if (text.Length != 1) return;
    char c = text[0];
    int digit = c switch {
      >= '0' and <= '9' => c - '0',
      >= 'a' and <= 'f' => c - 'a' + 10,
      >= 'A' and <= 'F' => c - 'A' + 10,
      _ => -1
    };
    if (digit >= 0) hexView.InputHexDigit(digit);
  }, "Hex Input");

  // Mouse
  bindings.Mouse(MouseButton.ScrollUp).Action(() => hexView.ScrollUp(3), "Scroll Up");
  bindings.Mouse(MouseButton.ScrollDown).Action(() => hexView.ScrollDown(3), "Scroll Down");
  bindings.Mouse(MouseButton.Left).Action(ctx => {
    hexView.ClickAtPosition(ctx.MouseY, ctx.MouseX);
  }, "Click");
  bindings.Drag(MouseButton.Left).Action((startX, startY) => {
    hexView.ClickAtPosition(startY, startX);
    state.HexSelectionAnchor = state.HexCursorOffset;
    return new DragHandler(
        onMove: (ctx, deltaX, deltaY) => {
          hexView.ClickAtPosition(startY + deltaY, startX + deltaX, extend: true);
        });
  }, "Drag Select");
}

void BindTextNavigation(InputBindingsBuilder bindings)
{
  bindings.Key(Hex1bKey.LeftArrow).Action(() => textView.MoveCursorLeft(false), "Left");
  bindings.Key(Hex1bKey.RightArrow).Action(() => textView.MoveCursorRight(false), "Right");
  bindings.Key(Hex1bKey.UpArrow).Action(() => textView.MoveCursorUp(false), "Up");
  bindings.Key(Hex1bKey.DownArrow).Action(() => textView.MoveCursorDown(false), "Down");
  bindings.Key(Hex1bKey.PageUp).Action(() => textView.PageUp(false), "Page Up");
  bindings.Key(Hex1bKey.PageDown).Action(() => textView.PageDown(false), "Page Down");
  bindings.Key(Hex1bKey.Home).Action(() => textView.Home(false), "Home");
  bindings.Key(Hex1bKey.End).Action(() => textView.End(false), "End");
  bindings.Ctrl().Key(Hex1bKey.Home).Action(() => textView.CtrlHome(false), "Start");
  bindings.Ctrl().Key(Hex1bKey.End).Action(() => textView.CtrlEnd(false), "End of File");

  bindings.Shift().Key(Hex1bKey.LeftArrow).Action(() => textView.MoveCursorLeft(true), "Select Left");
  bindings.Shift().Key(Hex1bKey.RightArrow).Action(() => textView.MoveCursorRight(true), "Select Right");
  bindings.Shift().Key(Hex1bKey.UpArrow).Action(() => textView.MoveCursorUp(true), "Select Up");
  bindings.Shift().Key(Hex1bKey.DownArrow).Action(() => textView.MoveCursorDown(true), "Select Down");

  bindings.Key(Hex1bKey.Backspace).Action(() => textView.Backspace(), "Backspace");
  bindings.Key(Hex1bKey.Delete).Action(() => textView.Delete(), "Delete");
  bindings.Key(Hex1bKey.Enter).Action(() => textView.InsertNewline(), "Enter");

  bindings.Ctrl().Key(Hex1bKey.C).Action(() => {
    string? text = textView.CopySelection();
    if (text is not null) appRef?.CopyToClipboard(text);
  }, "Copy");
  bindings.Ctrl().Key(Hex1bKey.X).Action(() => {
    string? text = textView.CopySelection();
    if (text is not null) {
      appRef?.CopyToClipboard(text);
      textView.Delete();
    }
  }, "Cut");
  bindings.Ctrl().Key(Hex1bKey.A).Action(() => textView.SelectAll(), "Select All");

  bindings.AnyCharacter().Action(text => {
    if (!IsSafeText(text)) return;
    foreach (char ch in text)
      textView.InsertChar(ch);
  }, "Insert Text");

  // Mouse
  bindings.Mouse(MouseButton.ScrollUp).Action(() => textView.ScrollUp(3), "Scroll Up");
  bindings.Mouse(MouseButton.ScrollDown).Action(() => textView.ScrollDown(3), "Scroll Down");
  bindings.Mouse(MouseButton.Left).Action(ctx => {
    int textAreaCols = Math.Max(1, Console.WindowWidth - 9);
    textView.ClickAtPosition(ctx.MouseY, ctx.MouseX, textAreaCols);
  }, "Click");
  bindings.Drag(MouseButton.Left).Action((startX, startY) => {
    int textAreaCols = Math.Max(1, Console.WindowWidth - 9);
    textView.ClickAtPosition(startY, startX, textAreaCols);
    state.TextSelectionAnchor = state.TextCursorOffset;
    return new DragHandler(
        onMove: (ctx, deltaX, deltaY) => {
          textView.ClickAtPosition(startY + deltaY, startX + deltaX, textAreaCols, extend: true);
        });
  }, "Drag Select");
}

// ─── Actions ───

void GuardUnsavedChanges(Action action)
{
  if (state.IsModified) {
    state.PendingAction = action;
    state.ShowUnsavedDialog = true;
  } else {
    action();
  }
}

void PromptOpenFile()
{
  fileBrowser.Open();
  state.ShowFileBrowser = true;
}

void SaveFile()
{
  if (state.Document is null) return;
  if (state.CurrentFilePath is not null)
    state.TrySave(state.CurrentFilePath);
}

void ToggleFindBar()
{
  state.ShowFindBar = !state.ShowFindBar;
  if (!state.ShowFindBar)
    state.CancelSearch();
}

void OpenGoto()
{
  state.GotoInput = "";
  state.ShowGotoDialog = true;
}

void ExecuteGoto()
{
  string input = state.GotoInput.Trim();
  if (string.IsNullOrEmpty(input)) return;

  if (state.ActiveView == ViewMode.Hex) {
    string hex = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? input[2..] : input;
    if (long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long offset))
      hexView.GotoOffset(offset);
  } else {
    if (long.TryParse(input, out long line))
      textView.GotoLine(line);
  }
}

// ─── Search ───

void StartSearch()
{
  Document? doc = state.Document;
  if (doc is null || string.IsNullOrEmpty(state.FindInput)) return;

  state.CancelSearch();
  state.SearchResults.Clear();
  state.CurrentMatchIndex = -1;
  state.IsSearching = true;
  state.SearchStatus = "Searching…";

  byte[] pattern;
  try {
    pattern = BuildSearchPattern();
  } catch (FormatException) {
    state.SearchStatus = "Invalid pattern";
    state.IsSearching = false;
    return;
  }

  if (pattern.Length == 0) {
    state.SearchStatus = "";
    state.IsSearching = false;
    return;
  }

  state.Settings.AddFindHistory(state.FindInput);

  CancellationTokenSource cts = new();
  state.SearchCts = cts;
  CancellationToken ct = cts.Token;

  Task.Run(() => {
    try {
      foreach (SearchResult result in SearchEngine.FindAll(doc, pattern)) {
        if (ct.IsCancellationRequested) break;
        lock (state.SearchResults) {
          state.SearchResults.Add(result);
        }
      }
      state.SearchStatus = state.SearchResults.Count > 0
          ? $"{state.SearchResults.Count} matches"
          : "No matches";
    } catch (OperationCanceledException) { } catch {
      state.SearchStatus = "Search error";
    } finally {
      state.IsSearching = false;
      appRef?.Invalidate();
    }
  }, ct);

  // Jump to first match from current cursor position
  SearchResult? first = SearchEngine.FindNext(doc, pattern, state.CurrentCursorOffset);
  if (first is not null)
    JumpToMatch(first.Value);
}

void FindNext()
{
  Document? doc = state.Document;
  if (doc is null) return;

  // If we have cached results, navigate them
  if (state.SearchResults.Count > 0) {
    long cursor = state.CurrentCursorOffset;
    lock (state.SearchResults) {
      for (int i = 0; i < state.SearchResults.Count; i++) {
        if (state.SearchResults[i].Offset > cursor) {
          state.CurrentMatchIndex = i;
          JumpToMatch(state.SearchResults[i]);
          return;
        }
      }
      // Wrap around
      state.CurrentMatchIndex = 0;
      JumpToMatch(state.SearchResults[0]);
    }
    return;
  }

  // No cached results — do a quick FindNext
  if (string.IsNullOrEmpty(state.FindInput)) return;
  try {
    byte[] pattern = BuildSearchPattern();
    SearchResult? next = SearchEngine.FindNext(doc, pattern, state.CurrentCursorOffset + 1);
    if (next is not null)
      JumpToMatch(next.Value);
    else
      state.SearchStatus = "No more matches";
  } catch { }
}

void FindPrevious()
{
  Document? doc = state.Document;
  if (doc is null) return;

  if (state.SearchResults.Count > 0) {
    long cursor = state.CurrentCursorOffset;
    lock (state.SearchResults) {
      for (int i = state.SearchResults.Count - 1; i >= 0; i--) {
        if (state.SearchResults[i].Offset < cursor) {
          state.CurrentMatchIndex = i;
          JumpToMatch(state.SearchResults[i]);
          return;
        }
      }
      // Wrap around
      state.CurrentMatchIndex = state.SearchResults.Count - 1;
      JumpToMatch(state.SearchResults[^1]);
    }
    return;
  }

  if (string.IsNullOrEmpty(state.FindInput)) return;
  try {
    byte[] pattern = BuildSearchPattern();
    SearchResult? prev = SearchEngine.FindPrevious(doc, pattern, state.CurrentCursorOffset);
    if (prev is not null)
      JumpToMatch(prev.Value);
    else
      state.SearchStatus = "No more matches";
  } catch { }
}

byte[] BuildSearchPattern()
{
  if (state.FindHexMode)
    return SearchEngine.ParseHexPattern(state.FindInput.AsSpan());

  string query = state.FindCaseSensitive ? state.FindInput : state.FindInput.ToLowerInvariant();
  return state.Decoder.EncodeString(query);
}

void JumpToMatch(SearchResult match)
{
  if (state.ActiveView == ViewMode.Hex) {
    hexView.GotoOffset(match.Offset);
    state.HexSelectionAnchor = match.Offset;
    state.HexCursorOffset = match.Offset + match.Length - 1;
  } else {
    textView.GotoOffset(match.Offset);
    state.TextSelectionAnchor = match.Offset;
    state.TextCursorOffset = match.Offset + match.Length - 1;
  }
}

// ─── Commands ───

void RegisterCommands()
{
  palette.RegisterCommand("File", "Open File...", "Ctrl+O", () => GuardUnsavedChanges(PromptOpenFile));
  palette.RegisterCommand("File", "Save", "Ctrl+S", SaveFile);
  palette.RegisterCommand("File", "Quit", "Ctrl+Q", () => GuardUnsavedChanges(() => appRef?.RequestStop()));

  foreach (string recent in state.Settings.RecentFiles) {
    string path = recent;
    palette.RegisterCommand("File", $"Open: {Path.GetFileName(path)}", "",
        () => GuardUnsavedChanges(() => state.OpenFile(path)));
  }

  palette.RegisterCommand("View", "Hex View", "F5", () => state.ActiveView = ViewMode.Hex);
  palette.RegisterCommand("View", "Text View", "F6", () => state.ActiveView = ViewMode.Text);
  palette.RegisterCommand("View", "Auto Column Width", "", () => { state.BytesPerRowSetting = 0; state.Settings.BytesPerRow = 0; state.Settings.Save(); });
  foreach (int bpr in new[] { 8, 16, 24, 32, 48, 64 }) {
    int val = bpr;
    palette.RegisterCommand("View", $"{bpr} Bytes/Row", "", () => { state.BytesPerRowSetting = val; state.Settings.BytesPerRow = val; state.Settings.Save(); });
  }
  palette.RegisterCommand("View", "Toggle Word Wrap", "", () => { state.WordWrap = !state.WordWrap; state.Settings.WordWrap = state.WordWrap; state.Settings.Save(); });
  palette.RegisterCommand("View", "Encoding: UTF-8", "", () => state.SwitchEncoding(TextEncoding.Utf8));
  palette.RegisterCommand("View", "Encoding: UTF-16 LE", "", () => state.SwitchEncoding(TextEncoding.Utf16Le));
  palette.RegisterCommand("View", "Encoding: Windows-1252", "", () => state.SwitchEncoding(TextEncoding.Windows1252));

  palette.RegisterCommand("Navigate", "Goto Offset/Line", "Ctrl+G", OpenGoto);
  palette.RegisterCommand("Search", "Find", "Ctrl+F", () => ToggleFindBar());
  palette.RegisterCommand("Search", "Find Next", "F3", () => FindNext());
  palette.RegisterCommand("Search", "Find Previous", "Shift+F3", () => FindPrevious());

  palette.RegisterCommand("Edit", "Copy", "Ctrl+C", () => {
    string? text = state.ActiveView == ViewMode.Hex
        ? hexView.CopySelection()
        : textView.CopySelection();
    if (text is not null) appRef?.CopyToClipboard(text);
  });
  palette.RegisterCommand("Edit", "Cut", "Ctrl+X", () => {
    if (state.ActiveView == ViewMode.Hex) {
      string? hex = hexView.CopySelection();
      if (hex is not null) {
        appRef?.CopyToClipboard(hex);
        hexView.DeleteAtCursor();
      }
    } else {
      string? text = textView.CopySelection();
      if (text is not null) {
        appRef?.CopyToClipboard(text);
        textView.Delete();
      }
    }
  });
  palette.RegisterCommand("Edit", "Select All", "Ctrl+A", () => {
    if (state.ActiveView == ViewMode.Hex)
      hexView.SelectAll();
    else
      textView.SelectAll();
  });
}

/// <summary>
/// Returns true if the text is safe to use as typed input (no control characters).
/// Hex1b's IsPrintableText allows multi-char strings even when they contain control chars.
/// This guards against unrecognized escape sequences leaking into text fields.
/// </summary>
static bool IsSafeText(string text)
{
  if (text.Length == 0)
    return false;

  for (int i = 0; i < text.Length; i++) {
    if (text[i] < 0x20)
      return false;
  }

  // Reject ANSI escape sequence fragments (e.g. "[A" from Esc+Arrow)
  if (text.Length == 2 && text[0] == '[' && text[1] >= 'A' && text[1] <= 'Z')
    return false;
  // Reject CSI parameter fragments like "[1~", "[15~"
  if (text.Length >= 2 && text[0] == '[' && text[text.Length - 1] == '~')
    return false;

  return true;
}

