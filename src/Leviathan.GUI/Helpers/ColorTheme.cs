using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Styling;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// A color theme defining the 13 color roles used by the custom view controls
/// (HexView, TextView, CsvView). Themes are either built-in or loaded from JSON
/// files in the <c>themes/</c> directory.
/// </summary>
internal sealed class ColorTheme
{
    // ── Identity ─────────────────────────────────────────────────────

    /// <summary>Machine identifier (e.g. "dark", "green-phosphor").</summary>
    public string Id { get; }

    /// <summary>Human-readable display name (e.g. "Green Phosphor").</summary>
    public string Name { get; }

    /// <summary>
    /// The Avalonia <see cref="ThemeVariant"/> to apply for chrome controls
    /// (menus, buttons, scrollbars) when this theme is active.
    /// </summary>
    public ThemeVariant BaseVariant { get; }

    // ── Color roles (IBrush) ─────────────────────────────────────────

    /// <summary>Main content text (hex bytes, text characters, cell values).</summary>
    public IBrush TextPrimary { get; }

    /// <summary>Secondary text (addresses, line numbers, ASCII sidebar).</summary>
    public IBrush TextSecondary { get; }

    /// <summary>Dimmed text (non-printable ASCII placeholders).</summary>
    public IBrush TextMuted { get; }

    /// <summary>View control background — drawn explicitly in Render().</summary>
    public IBrush Background { get; }

    /// <summary>Selection highlight (translucent overlay).</summary>
    public IBrush SelectionHighlight { get; }

    /// <summary>Cursor / active-cell highlight (translucent overlay).</summary>
    public IBrush CursorHighlight { get; }

    /// <summary>Separator and grid lines.</summary>
    public IBrush GridLine { get; }

    /// <summary>CSV header / column header background strip.</summary>
    public IBrush HeaderBackground { get; }

    /// <summary>CSV header text.</summary>
    public IBrush HeaderText { get; }

    /// <summary>Line-number gutter background.</summary>
    public IBrush GutterBackground { get; }

    /// <summary>Cursor bar / caret in text mode.</summary>
    public IBrush CursorBar { get; }

    /// <summary>Search match highlight (all matches).</summary>
    public IBrush MatchHighlight { get; }

    /// <summary>Active search match highlight (current match).</summary>
    public IBrush ActiveMatchHighlight { get; }

    // ── Cached pens (avoid allocation per access) ────────────────────

    /// <summary>Pen for separator / grid lines.</summary>
    public IPen GridLinePen { get; }

    /// <summary>Pen for gutter separator line.</summary>
    public IPen GutterPen { get; }

    // ── Constructor ──────────────────────────────────────────────────

    private ColorTheme(
        string id, string name, ThemeVariant baseVariant,
        IBrush textPrimary, IBrush textSecondary, IBrush textMuted,
        IBrush background, IBrush selectionHighlight, IBrush cursorHighlight,
        IBrush gridLine, IBrush headerBackground, IBrush headerText,
        IBrush gutterBackground, IBrush cursorBar,
        IBrush matchHighlight, IBrush activeMatchHighlight)
    {
        Id = id;
        Name = name;
        BaseVariant = baseVariant;
        TextPrimary = textPrimary;
        TextSecondary = textSecondary;
        TextMuted = textMuted;
        Background = background;
        SelectionHighlight = selectionHighlight;
        CursorHighlight = cursorHighlight;
        GridLine = gridLine;
        HeaderBackground = headerBackground;
        HeaderText = headerText;
        GutterBackground = gutterBackground;
        CursorBar = cursorBar;
        MatchHighlight = matchHighlight;
        ActiveMatchHighlight = activeMatchHighlight;
        GridLinePen = new Pen(gridLine, 1);
        GutterPen = new Pen(gridLine, 1);
    }

    // ── Built-in palettes ────────────────────────────────────────────

    /// <summary>Dark theme palette — light text on dark background (VS Code style).</summary>
    public static ColorTheme Dark { get; } = new(
        id: "dark", name: "Dark", baseVariant: ThemeVariant.Dark,
        textPrimary:          Brush(220, 220, 220),         // #DCDCDC
        textSecondary:        Brush(128, 128, 200),         // muted blue-purple
        textMuted:            Brush(120, 120, 120),         // dim gray
        background:           Brush(30, 30, 30),            // #1E1E1E
        selectionHighlight:   BrushA(80, 51, 153, 255),     // translucent blue
        cursorHighlight:      BrushA(120, 255, 200, 50),    // translucent gold
        gridLine:             BrushA(60, 128, 128, 128),    // faint gray
        headerBackground:     BrushA(40, 100, 100, 200),    // faint blue
        headerText:           Brush(200, 200, 255),         // light lavender
        gutterBackground:     BrushA(30, 128, 128, 128),    // near-transparent gray
        cursorBar:            BrushA(200, 220, 220, 220),   // bright caret
        matchHighlight:       BrushA(90, 255, 255, 0),      // translucent yellow
        activeMatchHighlight: BrushA(140, 255, 165, 0)      // translucent orange
    );

    /// <summary>Light theme palette — dark text on light background.</summary>
    public static ColorTheme Light { get; } = new(
        id: "light", name: "Light", baseVariant: ThemeVariant.Light,
        textPrimary:          Brush(30, 30, 30),            // near-black
        textSecondary:        Brush(80, 80, 140),           // muted blue
        textMuted:            Brush(160, 160, 160),         // mid gray
        background:           Brush(252, 252, 252),         // near-white
        selectionHighlight:   BrushA(60, 51, 120, 255),     // translucent blue
        cursorHighlight:      BrushA(80, 255, 180, 0),      // translucent amber
        gridLine:             BrushA(50, 80, 80, 80),       // faint dark gray
        headerBackground:     BrushA(30, 60, 60, 160),      // faint blue
        headerText:           Brush(40, 40, 120),           // dark blue
        gutterBackground:     BrushA(20, 80, 80, 80),       // near-transparent
        cursorBar:            BrushA(220, 30, 30, 30),      // dark caret
        matchHighlight:       BrushA(70, 255, 220, 0),      // translucent yellow
        activeMatchHighlight: BrushA(100, 255, 140, 0)      // translucent orange
    );

    /// <summary>IBM 5151 green phosphor — classic green-on-black terminal.</summary>
    public static ColorTheme GreenPhosphor { get; } = new(
        id: "green-phosphor", name: "Green Phosphor", baseVariant: ThemeVariant.Dark,
        textPrimary:          Brush(51, 255, 51),           // #33FF33
        textSecondary:        Brush(34, 170, 34),           // #22AA22
        textMuted:            Brush(17, 119, 17),           // #117711
        background:           Brush(10, 10, 10),            // #0A0A0A
        selectionHighlight:   BrushA(80, 51, 255, 51),      // translucent green
        cursorHighlight:      BrushA(120, 255, 200, 50),    // translucent gold
        gridLine:             BrushA(40, 51, 255, 51),      // faint green
        headerBackground:     BrushA(30, 51, 255, 51),      // faint green
        headerText:           Brush(102, 255, 102),         // #66FF66
        gutterBackground:     BrushA(20, 51, 255, 51),      // near-transparent green
        cursorBar:            BrushA(200, 51, 255, 51),     // bright green caret
        matchHighlight:       BrushA(90, 255, 255, 0),      // translucent yellow
        activeMatchHighlight: BrushA(140, 255, 165, 0)      // translucent orange
    );

    /// <summary>Amber phosphor — warm amber-on-dark terminal.</summary>
    public static ColorTheme AmberPhosphor { get; } = new(
        id: "amber-phosphor", name: "Amber Phosphor", baseVariant: ThemeVariant.Dark,
        textPrimary:          Brush(255, 176, 0),           // #FFB000
        textSecondary:        Brush(204, 140, 0),           // #CC8C00
        textMuted:            Brush(128, 88, 0),            // #805800
        background:           Brush(26, 10, 0),             // #1A0A00
        selectionHighlight:   BrushA(80, 255, 176, 0),      // translucent amber
        cursorHighlight:      BrushA(120, 255, 220, 80),    // translucent gold
        gridLine:             BrushA(40, 255, 176, 0),      // faint amber
        headerBackground:     BrushA(30, 255, 176, 0),      // faint amber
        headerText:           Brush(255, 210, 100),         // bright amber
        gutterBackground:     BrushA(20, 255, 176, 0),      // near-transparent amber
        cursorBar:            BrushA(200, 255, 176, 0),     // bright amber caret
        matchHighlight:       BrushA(90, 255, 255, 0),      // translucent yellow
        activeMatchHighlight: BrushA(140, 255, 200, 0)      // translucent bright amber
    );

    /// <summary>All built-in themes.</summary>
    public static IReadOnlyList<ColorTheme> BuiltInThemes { get; } = [Dark, Light, GreenPhosphor, AmberPhosphor];

    // ── Lookup ───────────────────────────────────────────────────────

    /// <summary>
    /// Finds a theme by ID. Searches built-in themes first, then user themes.
    /// Falls back to <see cref="Dark"/> if not found.
    /// </summary>
    public static ColorTheme FindById(string id, IReadOnlyList<ColorTheme>? userThemes = null)
    {
        foreach (ColorTheme theme in BuiltInThemes)
        {
            if (string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
                return theme;
        }

        if (userThemes is not null)
        {
            foreach (ColorTheme theme in userThemes)
            {
                if (string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
                    return theme;
            }
        }

        return Dark;
    }

    // ── User theme loading ───────────────────────────────────────────

    /// <summary>
    /// Loads all <c>*.json</c> theme files from the specified directory.
    /// Invalid files are silently skipped.
    /// </summary>
    public static List<ColorTheme> LoadUserThemes(string themesDirectory)
    {
        List<ColorTheme> themes = [];
        if (!Directory.Exists(themesDirectory))
            return themes;

        foreach (string file in Directory.EnumerateFiles(themesDirectory, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                ColorTheme? theme = LoadFromJson(json);
                if (theme is not null)
                    themes.Add(theme);
            }
            catch
            {
                // Skip invalid theme files
            }
        }

        return themes;
    }

    /// <summary>
    /// Loads a single theme from a JSON string. Returns null if the JSON is invalid
    /// or missing required fields.
    /// </summary>
    public static ColorTheme? LoadFromJson(string json)
    {
        ThemeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, ColorThemeJsonContext.Default.ThemeDto);
        }
        catch
        {
            return null;
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            return null;

        ThemeVariant baseVariant = string.Equals(dto.Base, "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        ColorTheme fallback = baseVariant == ThemeVariant.Light ? Light : Dark;

        string id = !string.IsNullOrWhiteSpace(dto.Id)
            ? dto.Id!
            : dto.Name!.ToLowerInvariant().Replace(' ', '-');

        Dictionary<string, string>? c = dto.Colors;

        return new ColorTheme(
            id: id,
            name: dto.Name!,
            baseVariant: baseVariant,
            textPrimary:          ParseBrush(c, "textPrimary", fallback.TextPrimary),
            textSecondary:        ParseBrush(c, "textSecondary", fallback.TextSecondary),
            textMuted:            ParseBrush(c, "textMuted", fallback.TextMuted),
            background:           ParseBrush(c, "background", fallback.Background),
            selectionHighlight:   ParseBrush(c, "selectionHighlight", fallback.SelectionHighlight),
            cursorHighlight:      ParseBrush(c, "cursorHighlight", fallback.CursorHighlight),
            gridLine:             ParseBrush(c, "gridLine", fallback.GridLine),
            headerBackground:     ParseBrush(c, "headerBackground", fallback.HeaderBackground),
            headerText:           ParseBrush(c, "headerText", fallback.HeaderText),
            gutterBackground:     ParseBrush(c, "gutterBackground", fallback.GutterBackground),
            cursorBar:            ParseBrush(c, "cursorBar", fallback.CursorBar),
            matchHighlight:       ParseBrush(c, "matchHighlight", fallback.MatchHighlight),
            activeMatchHighlight: ParseBrush(c, "activeMatchHighlight", fallback.ActiveMatchHighlight)
        );
    }

    // ── Color parsing helpers ────────────────────────────────────────

    private static IBrush ParseBrush(Dictionary<string, string>? colors, string key, IBrush fallback)
    {
        if (colors is null || !colors.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            return fallback;

        return TryParseColor(value, out Color color) ? new SolidColorBrush(color) : fallback;
    }

    /// <summary>
    /// Parses a CSS-style color string. Supports:
    /// <c>#RRGGBB</c>, <c>#AARRGGBB</c>, <c>rgba(r,g,b,a)</c>.
    /// </summary>
    internal static bool TryParseColor(string value, out Color color)
    {
        color = default;
        ReadOnlySpan<char> s = value.AsSpan().Trim();

        // #RRGGBB or #AARRGGBB
        if (s.Length > 0 && s[0] == '#')
        {
            s = s[1..];
            if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb6))
            {
                color = Color.FromRgb(
                    (byte)((rgb6 >> 16) & 0xFF),
                    (byte)((rgb6 >> 8) & 0xFF),
                    (byte)(rgb6 & 0xFF));
                return true;
            }

            if (s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb8))
            {
                color = Color.FromArgb(
                    (byte)((argb8 >> 24) & 0xFF),
                    (byte)((argb8 >> 16) & 0xFF),
                    (byte)((argb8 >> 8) & 0xFF),
                    (byte)(argb8 & 0xFF));
                return true;
            }

            return false;
        }

        // rgba(r,g,b,a) where a is 0–255
        if (s.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && s.EndsWith(")"))
        {
            ReadOnlySpan<char> inner = s[5..^1];
            Span<Range> parts = stackalloc Range[5];
            int count = inner.Split(parts, ',', StringSplitOptions.TrimEntries);
            if (count == 4
                && byte.TryParse(inner[parts[0]], out byte r)
                && byte.TryParse(inner[parts[1]], out byte g)
                && byte.TryParse(inner[parts[2]], out byte b)
                && byte.TryParse(inner[parts[3]], out byte a))
            {
                color = Color.FromArgb(a, r, g, b);
                return true;
            }

            return false;
        }

        return false;
    }

    // ── Brush factory helpers ────────────────────────────────────────

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));

    private static SolidColorBrush BrushA(byte a, byte r, byte g, byte b) =>
        new(Color.FromArgb(a, r, g, b));
}

// ── JSON DTO for user theme files ────────────────────────────────────

/// <summary>
/// Data transfer object for deserializing theme JSON files.
/// </summary>
internal sealed class ThemeDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("base")]
    public string? Base { get; set; }

    [JsonPropertyName("colors")]
    public Dictionary<string, string>? Colors { get; set; }
}

/// <summary>
/// AOT-safe JSON serializer context for theme files.
/// </summary>
[JsonSerializable(typeof(ThemeDto))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ColorThemeJsonContext : JsonSerializerContext;
