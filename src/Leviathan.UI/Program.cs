using Hexa.NET.ImGui;

using Leviathan.Core;
using Leviathan.UI;
using Leviathan.UI.Windows;

using NativeFileDialogSharp;

using System.Collections.Concurrent;

// ─── Settings & State ────────────────────────────────────────────
Settings settings = Settings.Load();

string? filePath = args.Length > 0 ? args[0] : null;
Document? document = null;
HexView? hexView = null;
TextView? textView = null;
FindWindow findWindow = new();
string? currentFilePath = null;

// Active view mode: 0 = Hex, 1 = Text
int activeView = 0; // 0=Hex, 1=Text

// Unsaved-changes dialog state
const string UnsavedChangesPopupId = "Unsaved Changes";
const string SaveErrorPopupId = "Save Error";
bool showUnsavedDialog = false;
bool showSaveError = false;
string saveErrorMessage = "";
Action? pendingAction = null;
string lastWindowTitle = "";
AppWindow? appWindow = null;

// Cross-thread UI requests (drained on render thread)
ConcurrentQueue<Action> uiActions = new();

void EnqueueUiAction(Action action)
{
  uiActions.Enqueue(action);
}

void DrainUiActions()
{
  while (uiActions.TryDequeue(out Action? action))
    action();
}

void OpenFile(string path)
{
  document?.Dispose();
  document = new Document(path);
  currentFilePath = path;
  hexView = new HexView(document) { BytesPerRowSetting = settings.BytesPerRow };
  textView = new TextView(document) { WordWrap = settings.WordWrap };
  settings.AddRecent(path);
  Console.WriteLine($"Opening: {path} ({document.Length:N0} bytes)");
}

/// <summary>
/// Wraps an action that may discard unsaved changes.
/// If the document is dirty, marks dialog state; otherwise executes immediately.
/// </summary>
void GuardUnsavedChanges(Action action)
{
  if (document is not null && document.IsModified) {
    pendingAction = action;
    showUnsavedDialog = true;
  } else {
    action();
  }
}

if (filePath is not null && File.Exists(filePath)) {
  OpenFile(filePath);
}

appWindow = new AppWindow(deltaTime => {
  DrainUiActions();

  // ── Update window title with dirty indicator ──
  UpdateWindowTitle();

  RenderMainMenuBar();
  RenderStatusBar();
  RenderMainContent(deltaTime);

  // ── Find bar (floating, rendered on top of everything else) ──
  findWindow.Render(document, hexView, textView, activeView, settings);

  // ── Unsaved-changes confirmation dialog ──
  RenderUnsavedDialog();

  // ── Save error popup ──
  RenderSaveErrorDialog();
});

// ── Close interception ──────────────────────────────────────────
appWindow.OnCloseRequested = () => {
  EnqueueUiAction(() => GuardUnsavedChanges(() => appWindow!.ConfirmClose()));
};

// ── Helpers ──────────────────────────────────────────────────────

void RenderMainMenuBar()
{
  if (!ImGui.BeginMainMenuBar())
    return;

  if (ImGui.BeginMenu("File"u8)) {
    if (ImGui.MenuItem("Open..."u8, "Ctrl+O"u8)) {
      GuardUnsavedChanges(() => {
        DialogResult result = Dialog.FileOpen();
        if (result.IsOk && result.Path is not null)
          OpenFile(result.Path);
      });
    }

    if (document is not null && ImGui.MenuItem("Save"u8, "Ctrl+S"u8)) {
      string? savePath = currentFilePath ?? document.FilePath;
      if (savePath is null) {
        DialogResult saveResult = Dialog.FileSave();
        if (saveResult.IsOk && saveResult.Path is not null)
          TrySave(saveResult.Path);
      } else {
        TrySave(savePath);
      }
    }

    if (document is not null && ImGui.MenuItem("Save As..."u8)) {
      DialogResult saveResult = Dialog.FileSave();
      if (saveResult.IsOk && saveResult.Path is not null)
        TrySave(saveResult.Path);
    }

    ImGui.Separator();

    if (settings.RecentFiles.Count > 0) {
      for (int i = 0; i < settings.RecentFiles.Count; i++) {
        string recentPath = settings.RecentFiles[i];
        if (ImGui.MenuItem($"{i + 1}. {recentPath}")) {
          if (File.Exists(recentPath)) {
            string capturedPath = recentPath;
            GuardUnsavedChanges(() => OpenFile(capturedPath));
          }
        }
      }
      ImGui.Separator();
    }

    if (ImGui.MenuItem("Quit"u8)) {
      GuardUnsavedChanges(() => appWindow!.ConfirmClose());
    }

    ImGui.EndMenu();
  }

  if (ImGui.BeginMenu("View"u8)) {
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

  if (document is not null && ImGui.BeginMenu("Search"u8)) {
    if (ImGui.MenuItem("Find..."u8, "Ctrl+F"u8))
      findWindow.Open();
    if (ImGui.MenuItem("Find Next"u8, "F3"u8) && findWindow.IsOpen)
      findWindow.FindNext(GetCurrentOffset(), hexView, textView, activeView);
    if (ImGui.MenuItem("Find Previous"u8, "Shift+F3"u8) && findWindow.IsOpen)
      findWindow.FindPrevious(GetCurrentOffset(), hexView, textView, activeView);
    ImGui.EndMenu();
  }

  ImGui.EndMainMenuBar();

  ImGuiIOPtr kbIo = ImGui.GetIO();
  if (!kbIo.WantTextInput) {
    if (ImGui.IsKeyPressed(ImGuiKey.F5)) activeView = 0;
    if (ImGui.IsKeyPressed(ImGuiKey.F6)) activeView = 1;
    if (ImGui.IsKeyPressed(ImGuiKey.S) && kbIo.KeyCtrl && document is not null) {
      string? savePath = currentFilePath ?? document.FilePath;
      if (savePath is null) {
        DialogResult r = Dialog.FileSave();
        if (r.IsOk && r.Path is not null) TrySave(r.Path);
      } else {
        TrySave(savePath);
      }
    }
    if (ImGui.IsKeyPressed(ImGuiKey.F) && kbIo.KeyCtrl && document is not null)
      findWindow.Open();
    if (ImGui.IsKeyPressed(ImGuiKey.F3) && findWindow.IsOpen)
      findWindow.FindNext(GetCurrentOffset(), hexView, textView, activeView);
    if (ImGui.IsKeyPressed(ImGuiKey.F4) && kbIo.KeyShift && findWindow.IsOpen)
      findWindow.FindPrevious(GetCurrentOffset(), hexView, textView, activeView);
  }
}

void RenderStatusBar()
{
  ImGuiIOPtr io = ImGui.GetIO();
  ImGuiViewportPtr viewport = ImGui.GetMainViewport();
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
}

void RenderMainContent(float deltaTime)
{
  ImGuiViewportPtr viewport = ImGui.GetMainViewport();
  float menuBarHeight = ImGui.GetFrameHeight();
  float statusBarHeight = ImGui.GetFrameHeight();

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
        textView.Render(deltaTime);
      } else {
        ImGui.TextUnformatted("Open a file to begin (File > Open or pass file path as argument)"u8);
      }
    }
  }
  ImGui.End();
}

long GetCurrentOffset() => activeView == 0
    ? (hexView?.SelectedOffset ?? 0)
    : (textView?.CursorOffset ?? 0);

bool TrySave(string path)
{
  try {
    document!.SaveTo(path);
    currentFilePath = path;
    settings.AddRecent(path);
    return true;
  } catch (Exception ex) {
    saveErrorMessage = ex.Message;
    showSaveError = true;
    return false;
  }
}

void UpdateWindowTitle()
{
  string title;
  if (document is null) {
    title = "Leviathan — Large File Editor";
  } else {
    string fileName = Path.GetFileName(currentFilePath ?? document.FilePath ?? "Untitled");
    title = document.IsModified
        ? $"● {fileName} — Leviathan"
        : $"{fileName} — Leviathan";
  }

  if (title != lastWindowTitle) {
    appWindow!.SetTitle(title);
    lastWindowTitle = title;
  }
}

void RenderUnsavedDialog()
{
  if (!showUnsavedDialog) return;

  Action? deferredAction = null;

  if (!ImGui.IsPopupOpen(UnsavedChangesPopupId))
    ImGui.OpenPopup(UnsavedChangesPopupId);

  ImGuiViewportPtr viewport = ImGui.GetMainViewport();
  System.Numerics.Vector2 center = new(
      viewport.Pos.X + viewport.Size.X * 0.5f,
      viewport.Pos.Y + viewport.Size.Y * 0.5f);
  ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));

  if (ImGui.BeginPopupModal(UnsavedChangesPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove)) {
    ImGui.Text("The file has unsaved changes."u8);
    ImGui.Text("Do you want to save before continuing?"u8);
    ImGui.Spacing();
    ImGui.Separator();
    ImGui.Spacing();

    if (ImGui.Button("Save"u8, new System.Numerics.Vector2(100, 0))) {
      string? savePath = currentFilePath ?? document?.FilePath;
      if (savePath is null) {
        DialogResult r = Dialog.FileSave();
        if (r.IsOk && r.Path is not null) {
          if (TrySave(r.Path))
            deferredAction = pendingAction;
        }
      } else {
        if (TrySave(savePath))
          deferredAction = pendingAction;
      }
      showUnsavedDialog = false;
      pendingAction = null;
      ImGui.CloseCurrentPopup();
    }

    ImGui.SameLine();
    if (ImGui.Button("Don't Save"u8, new System.Numerics.Vector2(100, 0))) {
      deferredAction = pendingAction;
      showUnsavedDialog = false;
      pendingAction = null;
      ImGui.CloseCurrentPopup();
    }

    ImGui.SameLine();
    if (ImGui.Button("Cancel"u8, new System.Numerics.Vector2(100, 0))) {
      showUnsavedDialog = false;
      pendingAction = null;
      ImGui.CloseCurrentPopup();
    }

    ImGui.EndPopup();
  }

  if (deferredAction is not null)
    EnqueueUiAction(deferredAction);
}

void RenderSaveErrorDialog()
{
  if (!showSaveError) return;

  if (!ImGui.IsPopupOpen(SaveErrorPopupId))
    ImGui.OpenPopup(SaveErrorPopupId);

  ImGuiViewportPtr viewport = ImGui.GetMainViewport();
  System.Numerics.Vector2 center = new(
      viewport.Pos.X + viewport.Size.X * 0.5f,
      viewport.Pos.Y + viewport.Size.Y * 0.5f);
  ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));

  if (ImGui.BeginPopupModal(SaveErrorPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove)) {
    ImGui.Text("Failed to save the file:"u8);
    ImGui.Spacing();
    ImGui.TextWrapped(saveErrorMessage);
    ImGui.Spacing();
    ImGui.Separator();
    ImGui.Spacing();

    if (ImGui.Button("OK"u8, new System.Numerics.Vector2(100, 0))) {
      showSaveError = false;
      saveErrorMessage = "";
      ImGui.CloseCurrentPopup();
    }

    ImGui.EndPopup();
  }
}

try {
  appWindow.Run();
} finally {
  appWindow.Dispose();
  document?.Dispose();
}
