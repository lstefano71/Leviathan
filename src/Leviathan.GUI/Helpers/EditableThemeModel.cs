using Avalonia.Styling;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Theme color slot keys used by persisted JSON and the editor model.
/// </summary>
internal static class ThemeColorKeys
{
    public const string TextPrimary = "textPrimary";
    public const string TextSecondary = "textSecondary";
    public const string TextMuted = "textMuted";
    public const string Background = "background";
    public const string SelectionHighlight = "selectionHighlight";
    public const string CursorHighlight = "cursorHighlight";
    public const string GridLine = "gridLine";
    public const string HeaderBackground = "headerBackground";
    public const string HeaderText = "headerText";
    public const string GutterBackground = "gutterBackground";
    public const string CursorBar = "cursorBar";
    public const string MatchHighlight = "matchHighlight";
    public const string ActiveMatchHighlight = "activeMatchHighlight";
    public const string RowStripe = "rowStripe";
    public const string ColumnStripe = "columnStripe";

    public static IReadOnlyList<string> All { get; } = [
        TextPrimary,
        TextSecondary,
        TextMuted,
        Background,
        SelectionHighlight,
        CursorHighlight,
        GridLine,
        HeaderBackground,
        HeaderText,
        GutterBackground,
        CursorBar,
        MatchHighlight,
        ActiveMatchHighlight,
        RowStripe,
        ColumnStripe
    ];
}

/// <summary>
/// Validation issue for a single editable theme field.
/// </summary>
/// <param name="Field">Field key (for example: <c>id</c>, <c>name</c>, <c>colors.textPrimary</c>).</param>
/// <param name="Message">Human-readable validation error.</param>
internal readonly record struct ThemeValidationIssue(string Field, string Message);

/// <summary>
/// Editable, serializable theme model used by advanced theme editor UI.
/// </summary>
internal sealed class EditableThemeModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ThemeVariant BaseVariant { get; set; } = ThemeVariant.Dark;

    public string TextPrimary { get; set; } = string.Empty;
    public string TextSecondary { get; set; } = string.Empty;
    public string TextMuted { get; set; } = string.Empty;
    public string Background { get; set; } = string.Empty;
    public string SelectionHighlight { get; set; } = string.Empty;
    public string CursorHighlight { get; set; } = string.Empty;
    public string GridLine { get; set; } = string.Empty;
    public string HeaderBackground { get; set; } = string.Empty;
    public string HeaderText { get; set; } = string.Empty;
    public string GutterBackground { get; set; } = string.Empty;
    public string CursorBar { get; set; } = string.Empty;
    public string MatchHighlight { get; set; } = string.Empty;
    public string ActiveMatchHighlight { get; set; } = string.Empty;
    public string RowStripe { get; set; } = string.Empty;
    public string ColumnStripe { get; set; } = string.Empty;

    /// <summary>
    /// Creates an editable model from an existing runtime theme.
    /// </summary>
    public static EditableThemeModel FromColorTheme(ColorTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        return new EditableThemeModel {
            Id = theme.Id,
            Name = theme.Name,
            BaseVariant = theme.BaseVariant,
            TextPrimary = ColorTheme.FormatBrushColor(theme.TextPrimary),
            TextSecondary = ColorTheme.FormatBrushColor(theme.TextSecondary),
            TextMuted = ColorTheme.FormatBrushColor(theme.TextMuted),
            Background = ColorTheme.FormatBrushColor(theme.Background),
            SelectionHighlight = ColorTheme.FormatBrushColor(theme.SelectionHighlight),
            CursorHighlight = ColorTheme.FormatBrushColor(theme.CursorHighlight),
            GridLine = ColorTheme.FormatBrushColor(theme.GridLine),
            HeaderBackground = ColorTheme.FormatBrushColor(theme.HeaderBackground),
            HeaderText = ColorTheme.FormatBrushColor(theme.HeaderText),
            GutterBackground = ColorTheme.FormatBrushColor(theme.GutterBackground),
            CursorBar = ColorTheme.FormatBrushColor(theme.CursorBar),
            MatchHighlight = ColorTheme.FormatBrushColor(theme.MatchHighlight),
            ActiveMatchHighlight = ColorTheme.FormatBrushColor(theme.ActiveMatchHighlight),
            RowStripe = ColorTheme.FormatBrushColor(theme.RowStripe),
            ColumnStripe = ColorTheme.FormatBrushColor(theme.ColumnStripe)
        };
    }

    /// <summary>
    /// Converts the editable model to a serializable theme DTO.
    /// </summary>
    public ThemeDto ToThemeDto()
    {
        return new ThemeDto {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Base = ColorTheme.BaseVariantToString(BaseVariant),
            Colors = ToColorValues()
        };
    }

    /// <summary>
    /// Converts the editable model to a runtime <see cref="ColorTheme"/> if valid.
    /// </summary>
    public ColorTheme? ToColorTheme()
    {
        return ColorTheme.FromThemeDto(ToThemeDto());
    }

    /// <summary>
    /// Converts all color slots to a serializable dictionary.
    /// </summary>
    public Dictionary<string, string> ToColorValues()
    {
        return new Dictionary<string, string>(ThemeColorKeys.All.Count, StringComparer.Ordinal) {
            [ThemeColorKeys.TextPrimary] = TextPrimary,
            [ThemeColorKeys.TextSecondary] = TextSecondary,
            [ThemeColorKeys.TextMuted] = TextMuted,
            [ThemeColorKeys.Background] = Background,
            [ThemeColorKeys.SelectionHighlight] = SelectionHighlight,
            [ThemeColorKeys.CursorHighlight] = CursorHighlight,
            [ThemeColorKeys.GridLine] = GridLine,
            [ThemeColorKeys.HeaderBackground] = HeaderBackground,
            [ThemeColorKeys.HeaderText] = HeaderText,
            [ThemeColorKeys.GutterBackground] = GutterBackground,
            [ThemeColorKeys.CursorBar] = CursorBar,
            [ThemeColorKeys.MatchHighlight] = MatchHighlight,
            [ThemeColorKeys.ActiveMatchHighlight] = ActiveMatchHighlight,
            [ThemeColorKeys.RowStripe] = RowStripe,
            [ThemeColorKeys.ColumnStripe] = ColumnStripe
        };
    }

    /// <summary>
    /// Validates identity and all color fields.
    /// </summary>
    public List<ThemeValidationIssue> Validate()
    {
        List<ThemeValidationIssue> issues = [];

        if (!IsValidId(Id))
            issues.Add(new ThemeValidationIssue("id", "Theme ID must contain only letters, digits, and '-'."));

        if (string.IsNullOrWhiteSpace(Name))
            issues.Add(new ThemeValidationIssue("name", "Theme name is required."));

        ValidateColor(ThemeColorKeys.TextPrimary, TextPrimary, issues);
        ValidateColor(ThemeColorKeys.TextSecondary, TextSecondary, issues);
        ValidateColor(ThemeColorKeys.TextMuted, TextMuted, issues);
        ValidateColor(ThemeColorKeys.Background, Background, issues);
        ValidateColor(ThemeColorKeys.SelectionHighlight, SelectionHighlight, issues);
        ValidateColor(ThemeColorKeys.CursorHighlight, CursorHighlight, issues);
        ValidateColor(ThemeColorKeys.GridLine, GridLine, issues);
        ValidateColor(ThemeColorKeys.HeaderBackground, HeaderBackground, issues);
        ValidateColor(ThemeColorKeys.HeaderText, HeaderText, issues);
        ValidateColor(ThemeColorKeys.GutterBackground, GutterBackground, issues);
        ValidateColor(ThemeColorKeys.CursorBar, CursorBar, issues);
        ValidateColor(ThemeColorKeys.MatchHighlight, MatchHighlight, issues);
        ValidateColor(ThemeColorKeys.ActiveMatchHighlight, ActiveMatchHighlight, issues);
        ValidateColor(ThemeColorKeys.RowStripe, RowStripe, issues);
        ValidateColor(ThemeColorKeys.ColumnStripe, ColumnStripe, issues);

        return issues;
    }

    /// <summary>
    /// Resets a single color slot to the current light/dark base fallback.
    /// </summary>
    public void ResetColor(string colorKey)
    {
        string fallbackValue = ColorTheme.GetFallbackColorValue(colorKey, BaseVariant);
        bool success = TrySetColorValue(colorKey, fallbackValue);
        if (!success)
            throw new ArgumentOutOfRangeException(nameof(colorKey), colorKey, "Unknown theme color key.");
    }

    /// <summary>
    /// Resets all color slots to the current light/dark base fallback.
    /// </summary>
    public void ResetAllColors()
    {
        foreach (string key in ThemeColorKeys.All) {
            ResetColor(key);
        }
    }

    /// <summary>
    /// Assigns one color slot by key.
    /// </summary>
    public bool TrySetColorValue(string colorKey, string value)
    {
        switch (colorKey) {
            case ThemeColorKeys.TextPrimary: TextPrimary = value; return true;
            case ThemeColorKeys.TextSecondary: TextSecondary = value; return true;
            case ThemeColorKeys.TextMuted: TextMuted = value; return true;
            case ThemeColorKeys.Background: Background = value; return true;
            case ThemeColorKeys.SelectionHighlight: SelectionHighlight = value; return true;
            case ThemeColorKeys.CursorHighlight: CursorHighlight = value; return true;
            case ThemeColorKeys.GridLine: GridLine = value; return true;
            case ThemeColorKeys.HeaderBackground: HeaderBackground = value; return true;
            case ThemeColorKeys.HeaderText: HeaderText = value; return true;
            case ThemeColorKeys.GutterBackground: GutterBackground = value; return true;
            case ThemeColorKeys.CursorBar: CursorBar = value; return true;
            case ThemeColorKeys.MatchHighlight: MatchHighlight = value; return true;
            case ThemeColorKeys.ActiveMatchHighlight: ActiveMatchHighlight = value; return true;
            case ThemeColorKeys.RowStripe: RowStripe = value; return true;
            case ThemeColorKeys.ColumnStripe: ColumnStripe = value; return true;
            default: return false;
        }
    }

    private static bool IsValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.Length == 0 || span[0] == '-' || span[^1] == '-')
            return false;

        for (int i = 0; i < span.Length; i++) {
            char ch = span[i];
            if (char.IsLetterOrDigit(ch) || ch == '-')
                continue;
            return false;
        }

        return true;
    }

    private static void ValidateColor(string key, string value, List<ThemeValidationIssue> issues)
    {
        if (!ColorTheme.TryParseColor(value ?? string.Empty, out _)) {
            issues.Add(new ThemeValidationIssue($"colors.{key}", $"Color value for '{key}' is invalid."));
        }
    }
}
