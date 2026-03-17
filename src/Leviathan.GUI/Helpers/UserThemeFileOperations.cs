using System.Globalization;

namespace Leviathan.GUI.Helpers;

/// <summary>
/// Result for a user-theme file operation.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Message">Human-readable success/error message.</param>
/// <param name="Theme">Theme produced by the operation, when applicable.</param>
/// <param name="FilePath">Target file path written/deleted by the operation.</param>
/// <param name="PreviousFilePath">Original file path, when an operation moved/renamed a file.</param>
internal readonly record struct ThemeFileOperationResult(
    bool Success,
    string Message,
    ColorTheme? Theme = null,
    string? FilePath = null,
    string? PreviousFilePath = null)
{
    public static ThemeFileOperationResult Ok(
        string message,
        ColorTheme? theme = null,
        string? filePath = null,
        string? previousFilePath = null) =>
        new(true, message, theme, filePath, previousFilePath);

    public static ThemeFileOperationResult Fail(string message) =>
        new(false, message);
}

/// <summary>
/// File-level operations for user themes in the <c>themes/*.json</c> directory.
/// </summary>
internal static class UserThemeFileOperations
{
    /// <summary>
    /// Imports a theme JSON file into the app themes directory.
    /// </summary>
    public static ThemeFileOperationResult ImportTheme(string sourceJsonFilePath, string themesDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceJsonFilePath))
            return ThemeFileOperationResult.Fail("Source theme file path is required.");

        if (!File.Exists(sourceJsonFilePath))
            return ThemeFileOperationResult.Fail($"Source theme file was not found: '{sourceJsonFilePath}'.");

        string json;
        try {
            json = File.ReadAllText(sourceJsonFilePath);
        } catch (Exception ex) {
            return ThemeFileOperationResult.Fail($"Failed to read source theme file '{sourceJsonFilePath}': {ex.Message}");
        }

        ColorTheme? importedTheme = ColorTheme.LoadFromJson(json);
        if (importedTheme is null)
            return ThemeFileOperationResult.Fail($"Source file '{sourceJsonFilePath}' is not a valid theme JSON file.");

        List<UserThemeEntry> userEntries = LoadUserThemeEntries(themesDirectory);
        HashSet<string> reservedIds = BuildReservedIds(userEntries, ignoreId: null);
        HashSet<string> reservedNames = BuildReservedNames(userEntries, ignoreName: null);

        string normalizedBaseId = NormalizeThemeId(importedTheme.Id, importedTheme.Name);
        string uniqueId = GetUniqueId(normalizedBaseId, reservedIds);
        string uniqueName = GetUniqueName(importedTheme.Name.Trim(), reservedNames);

        ThemeFileOperationResult buildResult = BuildTheme(importedTheme, uniqueId, uniqueName);
        if (!buildResult.Success || buildResult.Theme is null)
            return buildResult;

        string destinationPath = GetUniqueThemeFilePath(themesDirectory, uniqueId, currentFilePath: null);
        try {
            buildResult.Theme.SaveToJsonFile(destinationPath);
        } catch (Exception ex) {
            return ThemeFileOperationResult.Fail($"Failed to import theme to '{destinationPath}': {ex.Message}");
        }

        return ThemeFileOperationResult.Ok(
            $"Imported theme '{buildResult.Theme.Name}' as id '{buildResult.Theme.Id}'.",
            buildResult.Theme,
            destinationPath);
    }

    /// <summary>
    /// Exports a theme to an arbitrary JSON file path.
    /// </summary>
    public static ThemeFileOperationResult ExportTheme(ColorTheme theme, string destinationJsonFilePath)
    {
        ArgumentNullException.ThrowIfNull(theme);

        if (string.IsNullOrWhiteSpace(destinationJsonFilePath))
            return ThemeFileOperationResult.Fail("Destination theme file path is required.");

        try {
            theme.SaveToJsonFile(destinationJsonFilePath);
        } catch (Exception ex) {
            return ThemeFileOperationResult.Fail($"Failed to export theme to '{destinationJsonFilePath}': {ex.Message}");
        }

        return ThemeFileOperationResult.Ok(
            $"Exported theme '{theme.Name}' to '{destinationJsonFilePath}'.",
            theme,
            destinationJsonFilePath);
    }

    /// <summary>
    /// Duplicates an existing user theme with unique id, name, and filename.
    /// </summary>
    public static ThemeFileOperationResult DuplicateUserTheme(string themesDirectory, string userThemeId)
    {
        if (string.IsNullOrWhiteSpace(userThemeId))
            return ThemeFileOperationResult.Fail("User theme id is required.");

        if (IsBuiltInThemeId(userThemeId))
            return ThemeFileOperationResult.Fail($"Built-in theme '{userThemeId}' is immutable and cannot be duplicated as a user theme.");

        List<UserThemeEntry> userEntries = LoadUserThemeEntries(themesDirectory);
        ThemeFileOperationResult entryResult = TryFindUniqueThemeEntry(userEntries, userThemeId);
        if (!entryResult.Success || entryResult.Theme is null || string.IsNullOrWhiteSpace(entryResult.FilePath))
            return entryResult;

        string baseId = NormalizeThemeId($"{entryResult.Theme.Id}-copy", entryResult.Theme.Name);
        string baseName = $"{entryResult.Theme.Name} Copy";
        HashSet<string> reservedIds = BuildReservedIds(userEntries, ignoreId: null);
        HashSet<string> reservedNames = BuildReservedNames(userEntries, ignoreName: null);

        string uniqueId = GetUniqueId(baseId, reservedIds);
        string uniqueName = GetUniqueName(baseName, reservedNames);

        ThemeFileOperationResult buildResult = BuildTheme(entryResult.Theme, uniqueId, uniqueName);
        if (!buildResult.Success || buildResult.Theme is null)
            return buildResult;

        string duplicatePath = GetUniqueThemeFilePath(themesDirectory, uniqueId, currentFilePath: null);
        try {
            buildResult.Theme.SaveToJsonFile(duplicatePath);
        } catch (Exception ex) {
            return ThemeFileOperationResult.Fail($"Failed to write duplicated theme file '{duplicatePath}': {ex.Message}");
        }

        return ThemeFileOperationResult.Ok(
            $"Duplicated theme '{entryResult.Theme.Name}' as '{buildResult.Theme.Name}' ({buildResult.Theme.Id}).",
            buildResult.Theme,
            duplicatePath,
            entryResult.FilePath);
    }

    /// <summary>
    /// Renames an existing user theme and moves it to a filename based on the final id.
    /// </summary>
    public static ThemeFileOperationResult RenameUserTheme(
        string themesDirectory,
        string currentUserThemeId,
        string requestedThemeId,
        string requestedThemeName)
    {
        if (string.IsNullOrWhiteSpace(currentUserThemeId))
            return ThemeFileOperationResult.Fail("Current user theme id is required.");

        if (IsBuiltInThemeId(currentUserThemeId))
            return ThemeFileOperationResult.Fail($"Built-in theme '{currentUserThemeId}' is immutable and cannot be renamed.");

        if (string.IsNullOrWhiteSpace(requestedThemeName))
            return ThemeFileOperationResult.Fail("Requested theme name is required.");

        List<UserThemeEntry> userEntries = LoadUserThemeEntries(themesDirectory);
        ThemeFileOperationResult entryResult = TryFindUniqueThemeEntry(userEntries, currentUserThemeId);
        if (!entryResult.Success || entryResult.Theme is null || string.IsNullOrWhiteSpace(entryResult.FilePath))
            return entryResult;

        string normalizedRequestedId = string.IsNullOrWhiteSpace(requestedThemeId)
            ? NormalizeThemeId(requestedThemeName, requestedThemeName)
            : requestedThemeId.Trim();

        if (!IsValidThemeId(normalizedRequestedId))
            return ThemeFileOperationResult.Fail("Requested theme id is invalid. Use letters, digits, and '-' only.");

        HashSet<string> reservedIds = BuildReservedIds(userEntries, ignoreId: entryResult.Theme.Id);
        HashSet<string> reservedNames = BuildReservedNames(userEntries, ignoreName: entryResult.Theme.Name);
        string uniqueId = GetUniqueId(normalizedRequestedId, reservedIds);
        string uniqueName = GetUniqueName(requestedThemeName.Trim(), reservedNames);

        ThemeFileOperationResult buildResult = BuildTheme(entryResult.Theme, uniqueId, uniqueName);
        if (!buildResult.Success || buildResult.Theme is null)
            return buildResult;

        string destinationPath = GetUniqueThemeFilePath(themesDirectory, uniqueId, entryResult.FilePath);
        try {
            buildResult.Theme.SaveToJsonFile(destinationPath);
        } catch (Exception ex) {
            return ThemeFileOperationResult.Fail($"Failed to save renamed theme file '{destinationPath}': {ex.Message}");
        }

        if (!PathEquals(entryResult.FilePath, destinationPath)) {
            try {
                File.Delete(entryResult.FilePath);
            } catch (Exception ex) {
                return ThemeFileOperationResult.Fail(
                    $"Theme data was written to '{destinationPath}', but original file '{entryResult.FilePath}' could not be deleted: {ex.Message}");
            }
        }

        return ThemeFileOperationResult.Ok(
            $"Renamed theme '{entryResult.Theme.Name}' to '{buildResult.Theme.Name}' ({buildResult.Theme.Id}).",
            buildResult.Theme,
            destinationPath,
            entryResult.FilePath);
    }

    /// <summary>
    /// Deletes a user theme file by theme id.
    /// </summary>
    public static ThemeFileOperationResult DeleteUserTheme(string themesDirectory, string userThemeId)
    {
        if (string.IsNullOrWhiteSpace(userThemeId))
            return ThemeFileOperationResult.Fail("User theme id is required.");

        if (IsBuiltInThemeId(userThemeId))
            return ThemeFileOperationResult.Fail($"Built-in theme '{userThemeId}' is immutable and cannot be deleted.");

        List<UserThemeEntry> userEntries = LoadUserThemeEntries(themesDirectory);
        ThemeFileOperationResult entryResult = TryFindUniqueThemeEntry(userEntries, userThemeId);
        if (!entryResult.Success || string.IsNullOrWhiteSpace(entryResult.FilePath))
            return entryResult;

        try {
            File.Delete(entryResult.FilePath);
        } catch (Exception ex) {
            return ThemeFileOperationResult.Fail($"Failed to delete theme file '{entryResult.FilePath}': {ex.Message}");
        }

        return ThemeFileOperationResult.Ok(
            $"Deleted user theme file '{entryResult.FilePath}'.",
            filePath: entryResult.FilePath);
    }

    /// <summary>
    /// Saves an editable theme model as a user theme file.
    /// </summary>
    /// <param name="themesDirectory">Themes directory where user files are stored.</param>
    /// <param name="model">Editable theme model to validate and persist.</param>
    /// <param name="currentUserThemeId">
    /// Current user theme id when editing an existing file; null when creating from built-in/new theme.
    /// </param>
    public static ThemeFileOperationResult SaveUserTheme(
        string themesDirectory,
        EditableThemeModel model,
        string? currentUserThemeId = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        string requestedId = model.Id.Trim();
        string requestedName = model.Name.Trim();
        model.Id = requestedId;
        model.Name = requestedName;

        List<ThemeValidationIssue> issues = model.Validate();
        if (issues.Count > 0) {
            ThemeValidationIssue issue = issues[0];
            return ThemeFileOperationResult.Fail($"Validation failed for '{issue.Field}': {issue.Message}");
        }

        if (IsBuiltInThemeId(requestedId))
            return ThemeFileOperationResult.Fail($"Built-in theme id '{requestedId}' is reserved and cannot be overwritten.");

        List<UserThemeEntry> userEntries = LoadUserThemeEntries(themesDirectory);

        UserThemeEntry? currentEntry = null;
        if (!string.IsNullOrWhiteSpace(currentUserThemeId)) {
            if (IsBuiltInThemeId(currentUserThemeId))
                return ThemeFileOperationResult.Fail($"Built-in theme '{currentUserThemeId}' is immutable and cannot be saved as a user theme.");

            ThemeFileOperationResult currentEntryResult = TryFindUniqueThemeEntry(userEntries, currentUserThemeId);
            if (!currentEntryResult.Success || currentEntryResult.Theme is null || string.IsNullOrWhiteSpace(currentEntryResult.FilePath))
                return currentEntryResult;

            currentEntry = new UserThemeEntry(currentEntryResult.FilePath, currentEntryResult.Theme);
        }

        foreach (UserThemeEntry entry in userEntries) {
            if (currentEntry is UserThemeEntry current && PathEquals(entry.FilePath, current.FilePath))
                continue;

            if (string.Equals(entry.Theme.Id, requestedId, StringComparison.OrdinalIgnoreCase))
                return ThemeFileOperationResult.Fail($"A user theme with id '{requestedId}' already exists.");
        }

        ColorTheme? savedTheme = model.ToColorTheme();
        if (savedTheme is null)
            return ThemeFileOperationResult.Fail("Failed to build a valid theme from the provided values.");

        string destinationPath;
        if (currentEntry is UserThemeEntry currentEntryValue &&
            string.Equals(currentEntryValue.Theme.Id, requestedId, StringComparison.OrdinalIgnoreCase)) {
            destinationPath = currentEntryValue.FilePath;
        } else {
            string fileStem = NormalizeThemeId(requestedId, requestedName);
            destinationPath = Path.Combine(themesDirectory, fileStem + ".json");
            if (File.Exists(destinationPath))
                return ThemeFileOperationResult.Fail($"Theme file '{destinationPath}' already exists.");
        }

        try {
            savedTheme.SaveToJsonFile(destinationPath);
        } catch (IOException ex) {
            return ThemeFileOperationResult.Fail($"Failed to save theme file '{destinationPath}': {ex.Message}");
        } catch (UnauthorizedAccessException ex) {
            return ThemeFileOperationResult.Fail($"Failed to save theme file '{destinationPath}': {ex.Message}");
        }

        if (currentEntry is UserThemeEntry previousEntry && !PathEquals(previousEntry.FilePath, destinationPath)) {
            try {
                File.Delete(previousEntry.FilePath);
            } catch (IOException ex) {
                return ThemeFileOperationResult.Fail(
                    $"Theme data was written to '{destinationPath}', but original file '{previousEntry.FilePath}' could not be deleted: {ex.Message}");
            } catch (UnauthorizedAccessException ex) {
                return ThemeFileOperationResult.Fail(
                    $"Theme data was written to '{destinationPath}', but original file '{previousEntry.FilePath}' could not be deleted: {ex.Message}");
            }
        }

        return ThemeFileOperationResult.Ok(
            $"Saved theme '{savedTheme.Name}' ({savedTheme.Id}).",
            savedTheme,
            destinationPath,
            currentEntry?.FilePath);
    }

    private static ThemeFileOperationResult BuildTheme(ColorTheme baseTheme, string id, string name)
    {
        EditableThemeModel model = EditableThemeModel.FromColorTheme(baseTheme);
        model.Id = id;
        model.Name = name;

        List<ThemeValidationIssue> issues = model.Validate();
        foreach (ThemeValidationIssue issue in issues) {
            if (issue.Field == "id")
                return ThemeFileOperationResult.Fail(issue.Message);

            if (issue.Field == "name")
                return ThemeFileOperationResult.Fail(issue.Message);
        }

        ColorTheme? theme = model.ToColorTheme();
        if (theme is null)
            return ThemeFileOperationResult.Fail("Failed to build a valid theme from the provided identity.");

        return ThemeFileOperationResult.Ok("Theme built.", theme);
    }

    private static bool IsBuiltInThemeId(string id)
    {
        foreach (ColorTheme builtInTheme in ColorTheme.BuiltInThemes) {
            if (string.Equals(builtInTheme.Id, id, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ThemeFileOperationResult TryFindUniqueThemeEntry(List<UserThemeEntry> entries, string themeId)
    {
        List<UserThemeEntry> matches = [];
        foreach (UserThemeEntry entry in entries) {
            if (string.Equals(entry.Theme.Id, themeId, StringComparison.OrdinalIgnoreCase))
                matches.Add(entry);
        }

        if (matches.Count == 0)
            return ThemeFileOperationResult.Fail($"User theme '{themeId}' was not found in the themes directory.");

        if (matches.Count > 1)
            return ThemeFileOperationResult.Fail($"Multiple user theme files share id '{themeId}'. Resolve duplicate ids before this operation.");

        UserThemeEntry match = matches[0];
        return ThemeFileOperationResult.Ok("Theme located.", match.Theme, match.FilePath);
    }

    private static List<UserThemeEntry> LoadUserThemeEntries(string themesDirectory)
    {
        List<UserThemeEntry> entries = [];
        if (!Directory.Exists(themesDirectory))
            return entries;

        foreach (string filePath in Directory.EnumerateFiles(themesDirectory, "*.json")) {
            try {
                string json = File.ReadAllText(filePath);
                ColorTheme? theme = ColorTheme.LoadFromJson(json);
                if (theme is not null)
                    entries.Add(new UserThemeEntry(filePath, theme));
            } catch {
                // Ignore unreadable/invalid files while searching for valid user themes.
            }
        }

        return entries;
    }

    private static HashSet<string> BuildReservedIds(List<UserThemeEntry> entries, string? ignoreId)
    {
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (ColorTheme theme in ColorTheme.BuiltInThemes) {
            ids.Add(theme.Id);
        }

        foreach (UserThemeEntry entry in entries) {
            if (ignoreId is not null &&
                string.Equals(entry.Theme.Id, ignoreId, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            ids.Add(entry.Theme.Id);
        }

        return ids;
    }

    private static HashSet<string> BuildReservedNames(List<UserThemeEntry> entries, string? ignoreName)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ColorTheme theme in ColorTheme.BuiltInThemes) {
            names.Add(theme.Name);
        }

        foreach (UserThemeEntry entry in entries) {
            if (ignoreName is not null &&
                string.Equals(entry.Theme.Name, ignoreName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            names.Add(entry.Theme.Name);
        }

        return names;
    }

    private static string GetUniqueId(string baseId, HashSet<string> reservedIds)
    {
        string normalizedBaseId = NormalizeThemeId(baseId, fallbackName: "theme");
        if (reservedIds.Add(normalizedBaseId))
            return normalizedBaseId;

        for (int i = 2; i < int.MaxValue; i++) {
            string candidate = string.Create(
                CultureInfo.InvariantCulture,
                $"{normalizedBaseId}-{i}");
            if (reservedIds.Add(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Failed to generate a unique theme id.");
    }

    private static string GetUniqueName(string baseName, HashSet<string> reservedNames)
    {
        string trimmedBaseName = string.IsNullOrWhiteSpace(baseName) ? "Theme" : baseName.Trim();
        if (reservedNames.Add(trimmedBaseName))
            return trimmedBaseName;

        for (int i = 2; i < int.MaxValue; i++) {
            string candidate = string.Create(
                CultureInfo.InvariantCulture,
                $"{trimmedBaseName} ({i})");
            if (reservedNames.Add(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Failed to generate a unique theme name.");
    }

    private static string GetUniqueThemeFilePath(string themesDirectory, string id, string? currentFilePath)
    {
        string fileStem = NormalizeThemeId(id, fallbackName: "theme");
        string basePath = Path.Combine(themesDirectory, fileStem + ".json");
        if (CanUseFilePath(basePath, currentFilePath))
            return basePath;

        for (int i = 2; i < int.MaxValue; i++) {
            string candidatePath = Path.Combine(themesDirectory, $"{fileStem}-{i}.json");
            if (CanUseFilePath(candidatePath, currentFilePath))
                return candidatePath;
        }

        throw new InvalidOperationException("Failed to generate a unique theme file path.");
    }

    private static bool CanUseFilePath(string path, string? currentFilePath)
    {
        if (!string.IsNullOrWhiteSpace(currentFilePath) && PathEquals(path, currentFilePath))
            return true;

        return !File.Exists(path);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsValidThemeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        ReadOnlySpan<char> span = id.AsSpan().Trim();
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

    private static string NormalizeThemeId(string id, string fallbackName)
    {
        string candidate = string.IsNullOrWhiteSpace(id) ? fallbackName : id;
        Span<char> buffer = stackalloc char[candidate.Length];
        int length = 0;
        bool previousWasDash = false;

        for (int i = 0; i < candidate.Length; i++) {
            char ch = candidate[i];
            if (char.IsLetterOrDigit(ch)) {
                buffer[length++] = char.ToLowerInvariant(ch);
                previousWasDash = false;
                continue;
            }

            if (ch is '-' or '_' or ' ') {
                if (length > 0 && !previousWasDash) {
                    buffer[length++] = '-';
                    previousWasDash = true;
                }
            }
        }

        while (length > 0 && buffer[length - 1] == '-') {
            length--;
        }

        if (length == 0)
            return "theme";

        return new string(buffer[..length]);
    }

    private readonly record struct UserThemeEntry(string FilePath, ColorTheme Theme);
}
