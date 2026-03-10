using Hexa.NET.ImGui;

using Leviathan.Core;
using Leviathan.UI;

using NativeFileDialogSharp;

// ─── Settings & State ────────────────────────────────────────────
var settings = Settings.Load();

string? filePath = args.Length > 0 ? args[0] : null;
Document? document = null;
HexView? hexView = null;

void OpenFile(string path)
{
  document?.Dispose();
  document = new Document(path);
  hexView = new HexView(document) { BytesPerRowSetting = settings.BytesPerRow };
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
      ImGui.EndMenu();
    }
    if (ImGui.BeginMenu("Navigate"u8)) {
      if (ImGui.MenuItem("Goto Offset..."u8, "Ctrl+G"u8)) {
        hexView?.OpenGotoDialog();
      }
      ImGui.EndMenu();
    }
    ImGui.EndMainMenuBar();
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
      ImGui.Text($"Size: {document.Length:N0} bytes | Offset: {hexView?.BaseOffset ?? 0:X} | {hexView?.CurrentBytesPerRow ?? 16} B/row | {io.Framerate:F0} FPS");
    } else {
      ImGui.Text($"No file open | {io.Framerate:F0} FPS");
    }
  }
  ImGui.End();

  // Main content area — Hex View
  float menuBarHeight = ImGui.GetFrameHeight();
  ImGui.SetNextWindowPos(new System.Numerics.Vector2(0, menuBarHeight));
  ImGui.SetNextWindowSize(new System.Numerics.Vector2(viewport.Size.X, viewport.Size.Y - menuBarHeight - statusBarHeight));

  if (ImGui.Begin("##HexView"u8, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
      ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
      ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) {
    if (hexView is not null) {
      hexView.Render();
    } else {
      ImGui.TextUnformatted("Open a file to begin (File > Open or pass file path as argument)"u8);
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
