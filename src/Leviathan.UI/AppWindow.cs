using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.OpenGL3;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

using System.Numerics;

namespace Leviathan.UI;

/// <summary>
/// Manages the application window, OpenGL context, and ImGui lifecycle.
/// Bridges Silk.NET input events into ImGui IO.
/// </summary>
public sealed class AppWindow : IDisposable
{
    private IWindow _window = null!;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiContextPtr _imguiContext;
    private readonly Action<float> _renderCallback;
    private bool _disposed;
    private bool _closeConfirmed;

    /// <summary>
    /// Callback invoked when the user requests closing the window (X button, Alt+F4).
    /// Set <see cref="IWindow.IsClosing"/> to <c>false</c> inside the handler to cancel the close.
    /// If null, the window closes immediately.
    /// </summary>
    public Action? OnCloseRequested { get; set; }

    /// <summary>
    /// Callback invoked when the user drops one or more files onto the window from the OS shell.
    /// The array contains the full paths of the dropped files.
    /// </summary>
    public Action<string[]>? OnFileDrop { get; set; }

    public AppWindow(Action<float> renderCallback)
    {
        _renderCallback = renderCallback;
    }

    public void Run()
    {
        GlfwWindowing.RegisterPlatform();

        var opts = WindowOptions.Default with {
            Size = new Vector2D<int>(1280, 800),
            Title = "Leviathan — Large File Editor",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            VSync = true,
        };

        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.FramebufferResize += OnResize;
        _window.Closing += OnClosing;
        _window.FileDrop += OnFileDropped;
        _window.Run();
    }

    private unsafe void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();

        // Create ImGui context
        _imguiContext = ImGui.CreateContext();
        ImGui.SetCurrentContext(_imguiContext);

        var io = ImGui.GetIO();

        // Load a system monospace font with broad Unicode coverage
        LoadFonts();

        // Initialize OpenGL3 backend
        ImGuiImplOpenGL3.SetCurrentContext(_imguiContext);
        ImGuiImplOpenGL3.Init("#version 330");

        // Setup display size
        var fbSize = _window.FramebufferSize;
        io.DisplaySize = new Vector2(fbSize.X, fbSize.Y);

        // Wire up input
        foreach (var keyboard in _input.Keyboards) {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }
        foreach (var mouse in _input.Mice) {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }
    }

    private unsafe void OnRender(double deltaTime)
    {
        var fbSize = _window.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fbSize.X, (uint)fbSize.Y);
        _gl.ClearColor(0.1f, 0.1f, 0.12f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        // Update ImGui display size
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(fbSize.X, fbSize.Y);
        io.DeltaTime = (float)deltaTime;

        ImGuiImplOpenGL3.NewFrame();
        ImGui.NewFrame();

        // Invoke the application's UI rendering
        _renderCallback((float)deltaTime);

        ImGui.Render();
        ImGuiImplOpenGL3.RenderDrawData(ImGui.GetDrawData());
    }

    private void OnResize(Vector2D<int> size)
    {
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
    }

    private void OnClosing()
    {
        if (_closeConfirmed)
            return;

        if (OnCloseRequested is not null) {
            _window.IsClosing = false;
            OnCloseRequested();
        }
    }

    private void OnFileDropped(string[] paths)
    {
        OnFileDrop?.Invoke(paths);
    }

    /// <summary>
    /// Sets the window title bar text.
    /// </summary>
    public void SetTitle(string title) => _window.Title = title;

    /// <summary>
    /// Confirms and performs the close, bypassing the <see cref="OnCloseRequested"/> callback.
    /// </summary>
    public void ConfirmClose()
    {
        _closeConfirmed = true;
        _window.Close();
    }

    // ─── Input Bridging ──────────────────────────────────────────────

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        var io = ImGui.GetIO();
        var imKey = MapKey(key);
        if (imKey != ImGuiKey.None)
            io.AddKeyEvent(imKey, true);
        UpdateModifiers(keyboard);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        var io = ImGui.GetIO();
        var imKey = MapKey(key);
        if (imKey != ImGuiKey.None)
            io.AddKeyEvent(imKey, false);
        UpdateModifiers(keyboard);
    }

    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        var io = ImGui.GetIO();
        io.AddInputCharacter(character);
    }

    private static void UpdateModifiers(IKeyboard keyboard)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift, keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt, keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight));
        io.AddKeyEvent(ImGuiKey.ModSuper, keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight));
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        var io = ImGui.GetIO();
        int btn = button switch {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1
        };
        if (btn >= 0)
            io.AddMouseButtonEvent(btn, true);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        var io = ImGui.GetIO();
        int btn = button switch {
            MouseButton.Left => 0,
            MouseButton.Right => 1,
            MouseButton.Middle => 2,
            _ => -1
        };
        if (btn >= 0)
            io.AddMouseButtonEvent(btn, false);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(position.X, position.Y);
    }

    private void OnScroll(IMouse mouse, ScrollWheel scroll)
    {
        var io = ImGui.GetIO();
        io.AddMouseWheelEvent(scroll.X, scroll.Y);
    }

    private static ImGuiKey MapKey(Key key) => key switch {
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Insert => ImGuiKey.Insert,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Space => ImGuiKey.Space,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.A => ImGuiKey.A,
        Key.C => ImGuiKey.C,
        Key.V => ImGuiKey.V,
        Key.X => ImGuiKey.X,
        Key.Y => ImGuiKey.Y,
        Key.Z => ImGuiKey.Z,
        Key.Number0 => ImGuiKey.Key0,
        Key.Number1 => ImGuiKey.Key1,
        Key.Number2 => ImGuiKey.Key2,
        Key.Number3 => ImGuiKey.Key3,
        Key.Number4 => ImGuiKey.Key4,
        Key.Number5 => ImGuiKey.Key5,
        Key.Number6 => ImGuiKey.Key6,
        Key.Number7 => ImGuiKey.Key7,
        Key.Number8 => ImGuiKey.Key8,
        Key.Number9 => ImGuiKey.Key9,
        Key.B => ImGuiKey.B,
        Key.D => ImGuiKey.D,
        Key.E => ImGuiKey.E,
        Key.F => ImGuiKey.F,
        Key.S => ImGuiKey.S,
        Key.G => ImGuiKey.G,
        Key.F1 => ImGuiKey.F1,
        Key.F2 => ImGuiKey.F2,
        Key.F3 => ImGuiKey.F3,
        Key.F5 => ImGuiKey.F5,
        Key.F12 => ImGuiKey.F12,
        Key.ControlLeft => ImGuiKey.LeftCtrl,
        Key.ControlRight => ImGuiKey.RightCtrl,
        Key.ShiftLeft => ImGuiKey.LeftShift,
        Key.ShiftRight => ImGuiKey.RightShift,
        Key.AltLeft => ImGuiKey.LeftAlt,
        Key.AltRight => ImGuiKey.RightAlt,
        Key.KeypadEnter => ImGuiKey.KeypadEnter,
        Key.Keypad0 => ImGuiKey.Keypad0,
        Key.Keypad1 => ImGuiKey.Keypad1,
        Key.Keypad2 => ImGuiKey.Keypad2,
        Key.Keypad3 => ImGuiKey.Keypad3,
        Key.Keypad4 => ImGuiKey.Keypad4,
        Key.Keypad5 => ImGuiKey.Keypad5,
        Key.Keypad6 => ImGuiKey.Keypad6,
        Key.Keypad7 => ImGuiKey.Keypad7,
        Key.Keypad8 => ImGuiKey.Keypad8,
        Key.Keypad9 => ImGuiKey.Keypad9,
        Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
        Key.F4 => ImGuiKey.F4,
        Key.F6 => ImGuiKey.F6,
        _ => ImGuiKey.None
    };

    /// <summary>
    /// Loads Consolas (or falls back to ImGui default) with broad Unicode glyph ranges.
    /// </summary>
    private static unsafe void LoadFonts()
    {
        const float fontSize = 15.0f;
        string primaryFontPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            "consola.ttf");

        var io = ImGui.GetIO();
        var atlas = io.Fonts;

        if (!File.Exists(primaryFontPath))
            return; // fall back to ImGui default font

        // Pairs of (first, last) codepoints, terminated by 0
        uint* ranges = stackalloc uint[]
        {
      0x0020, 0x00FF, // Basic Latin + Latin-1 Supplement
      0x0100, 0x024F, // Latin Extended-A + B
      0x0370, 0x03FF, // Greek and Coptic
      0x0400, 0x052F, // Cyrillic + Cyrillic Supplement
      0x1E00, 0x1EFF, // Latin Extended Additional
      0x2000, 0x206F, // General Punctuation
      0x2100, 0x214F, // Letterlike Symbols
      0x2150, 0x218F, // Number Forms
      0x2190, 0x21FF, // Arrows
      0x2200, 0x22FF, // Mathematical Operators (∆∇∧∨∩∪⊂⊃⊥⊤…)
      0x2300, 0x23FF, // Miscellaneous Technical (incl. APL: ⌶-⍺, ⎕)
      0x2500, 0x257F, // Box Drawing
      0x2580, 0x259F, // Block Elements
      0x25A0, 0x25FF, // Geometric Shapes (◇○…)
      0x2600, 0x26FF, // Miscellaneous Symbols
      0x2700, 0x27BF, // Dingbats
      0x27C0, 0x27EF, // Miscellaneous Mathematical Symbols-A
      0x27F0, 0x27FF, // Supplemental Arrows-A
      0x2900, 0x297F, // Supplemental Arrows-B
      0x2980, 0x29FF, // Miscellaneous Mathematical Symbols-B
      0x2A00, 0x2AFF, // Supplemental Mathematical Operators
      0xFFFD, 0xFFFD, // Replacement character
      0,              // Terminator
    };

        atlas.AddFontFromFileTTF(primaryFontPath, fontSize, (ImFontConfig*)null, ranges);

        // Merge symbol/math fallback fonts so glyphs missing in Consolas still render.
        // ImGui has no OS-level font fallback, so we merge additional fonts into one atlas.
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] fallbackFontCandidates =
        [
          Path.Combine(fontsDir, "seguisym.ttf"),
      Path.Combine(fontsDir, "cambria.ttc"),
    ];

        uint* fallbackRanges = stackalloc uint[]
        {
      0x2200, 0x22FF, // Mathematical Operators (includes U+22A5 ⊥)
      0x2300, 0x23FF, // Miscellaneous Technical (APL symbols)
      0x27C0, 0x27EF, // Miscellaneous Mathematical Symbols-A
      0x2980, 0x29FF, // Miscellaneous Mathematical Symbols-B
      0x2A00, 0x2AFF, // Supplemental Mathematical Operators
      0,
    };

        var fallbackConfig = ImGui.ImFontConfig();
        try {
            fallbackConfig.MergeMode = true;
            fallbackConfig.PixelSnapH = true;
            fallbackConfig.RasterizerDensity = 1.0f;

            foreach (string fallbackFontPath in fallbackFontCandidates) {
                if (!File.Exists(fallbackFontPath))
                    continue;

                atlas.AddFontFromFileTTF(fallbackFontPath, fontSize, fallbackConfig, fallbackRanges);
                break;
            }
        } finally {
            fallbackConfig.Destroy();
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ImGuiImplOpenGL3.Shutdown();
        ImGui.DestroyContext(_imguiContext);

        _input?.Dispose();
        _gl?.Dispose();
    }
}
