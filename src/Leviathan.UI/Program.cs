using Hexa.NET.ImGui;

using Leviathan.Core;
using Leviathan.UI;

using NativeFileDialogSharp;

// ─── Settings & State ────────────────────────────────────────────
var settings = Settings.Load();

string? filePath = args.Length > 0 ? args[0] : null;
Document? document = null;
HexView? hexView = null;
TextView? textView = null;

// Active view mode: 0 = Hex, 1 = Text
int activeView = 0; // 0=Hex, 1=Text

void OpenFile(string path)
{
  document?.Dispose();
  document = new Document(path);
  hexView = new HexView(document) { BytesPerRowSetting = settings.BytesPerRow };
  textView = new TextView(document) { WordWrap = settings.WordWrap };
  settings.AddRecent(path);
  Console.WriteLine($"Opening: {path} ({document.Length:N0} bytes)");
}

if (filePath is not null && File.Exists(filePath)) {
  OpenFile(filePath);
}

var appWindow = new AppWindow(deltaTime => {
  // Main menu bar
  if (ImGui.BeginMainMenuBar()) {
    if (ImGui.BeginMenu("File"u8)) {
      if (ImGui.MenuItem("Open..."u8, "Ctrl+O"u8)) {
        var result = Dialog.FileOpen();
        if (result.IsOk && result.Path is not null)
          OpenFile(result.Path);
      }
      if (document is not null && ImGui.MenuItem("Save"u8)) {
        document.SaveTo(document.FilePath!);
      }
      ImGui.Separator();

      // MRU list
      if (settings.RecentFiles.Count > 0) {
        for (int i = 0; i < settings.RecentFiles.Count; i++) {
          string recentPath = settings.RecentFiles[i];
          if (ImGui.MenuItem($"{i + 1}. {recentPath}")) {
            if (File.Exists(recentPath))
              OpenFile(recentPath);
          }
        }
        ImGui.Separator();
      }

      if (ImGui.MenuItem("Quit"u8)) { Environment.Exit(0); }
      ImGui.EndMenu();
    }
    if (ImGui.BeginMenu("View"u8)) {
      // ── View mode switching ──
      if (ImGui.MenuItem("Hex View"u8, "F5"u8, activeView == 0))
        activeView = 0;
      if (ImGui.MenuItem("Text View"u8, "F6"u8, activeView == 1))
        activeView = 1;
      ImGui.Separator();

      if (activeView == 0) {
        // Hex view columns
        bool isAuto = settings.BytesPerRow == 0;
        if (ImGui.MenuItem("Auto Column Width"u8, ""u8, isAuto)) {
          settings.BytesPerRow = 0;
          if (hexView is not null) hexView.BytesPerRowSetting = 0;
          settings.Save();
        }
        ImGui.Separator();
        foreach (int bpr in new[] { 8, 16, 24, 32, 48, 64 }) {
          bool selected = settings.BytesPerRow == bpr;
          if (ImGui.MenuItem($"{bpr} Bytes/Row", "", selected)) {
            settings.BytesPerRow = bpr;
            if (hexView is not null) hexView.BytesPerRowSetting = bpr;
            settings.Save();
          }
        }
      } else {
        // Text view options
        bool wrap = settings.WordWrap;
        if (ImGui.MenuItem("Word Wrap"u8, ""u8, wrap)) {
          settings.WordWrap = !wrap;
          if (textView is not null) textView.WordWrap = settings.WordWrap;
          settings.Save();
        }
      }
      ImGui.EndMenu();
    }
    if (ImGui.BeginMenu("Navigate"u8)) {
      if (activeView == 0) {
        if (ImGui.MenuItem("Goto Offset..."u8, "Ctrl+G"u8))
          hexView?.OpenGotoDialog();
      } else {
        if (ImGui.MenuItem("Goto Line..."u8, "Ctrl+G"u8))
          textView?.OpenGotoDialog();
      }
      ImGui.EndMenu();
    }
    ImGui.EndMainMenuBar();

    // ── Global keyboard shortcuts for view switching ──
    if (ImGui.IsKeyPressed(ImGuiKey.F5) && !ImGui.GetIO().WantTextInput)
      activeView = 0;
    if (ImGui.IsKeyPressed(ImGuiKey.F6) && !ImGui.GetIO().WantTextInput)
      activeView = 1;
  }

  // Status bar
  var io = ImGui.GetIO();
  var viewport = ImGui.GetMainViewport();
  float statusBarHeight = ImGui.GetFrameHeight();

  ImGui.SetNextWindowPos(new System.Numerics.Vector2(0, viewport.Size.Y - statusBarHeight));
  ImGui.SetNextWindowSize(new System.Numerics.Vector2(viewport.Size.X, statusBarHeight));
  if (ImGui.Begin("##StatusBar"u8, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
      ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings)) {
    if (document is not null) {
      string viewName = activeView == 0 ? "HEX" : "TEXT";
      if (activeView == 0) {
        ImGui.Text($"[{viewName}] Size: {document.Length:N0} bytes | Offset: {hexView?.BaseOffset ?? 0:X} | {hexView?.CurrentBytesPerRow ?? 16} B/row | {io.Framerate:F0} FPS");
      } else {
        string wrapLabel = textView?.WordWrap == true ? "Wrap" : "NoWrap";
        ImGui.Text($"[{viewName}] Size: {document.Length:N0} bytes | Offset: {textView?.TopDocOffset ?? 0:X} | {wrapLabel} | {io.Framerate:F0} FPS");
      }
    } else {
      ImGui.Text($"No file open | {io.Framerate:F0} FPS");
    }
  }
  ImGui.End();

  // Main content area
  float menuBarHeight = ImGui.GetFrameHeight();
  ImGui.SetNextWindowPos(new System.Numerics.Vector2(0, menuBarHeight));
  ImGui.SetNextWindowSize(new System.Numerics.Vector2(viewport.Size.X, viewport.Size.Y - menuBarHeight - statusBarHeight));

  string windowLabel = activeView == 0 ? "##HexView" : "##TextView";
  if (ImGui.Begin(windowLabel, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
      ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
      ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) {
    if (activeView == 0) {
      if (hexView is not null) {
        hexView.Render();
      } else {
        ImGui.TextUnformatted("Open a file to begin (File > Open or pass file path as argument)"u8);
      }
    } else {
      if (textView is not null) {
        textView.Render();
      } else {
        ImGui.TextUnformatted("Open a file to begin (File > Open or pass file path as argument)"u8);
      }
    }
  }
  ImGui.End();
});

try {
  appWindow.Run();
} finally {
  appWindow.Dispose();
  document?.Dispose();
}
