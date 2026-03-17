using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.GUI;

/// <summary>
/// Persisted settings for the GUI frontend. AOT-safe via source-generated JSON context.
/// </summary>
public sealed class GuiSettings
{
    private const int MaxRecentFiles = 20;
    private const int MaxFindHistory = 20;
    private const int MaxCsvFileSettings = 200;

    public List<string> RecentFiles { get; set; } = [];

    /// <summary>
    /// User-pinned favourite files. Pinned files are never evicted by MRU rotation
    /// and always appear above the recent files list on the welcome screen.
    /// </summary>
    public List<string> PinnedFiles { get; set; } = [];

    public int BytesPerRow { get; set; } // 0 = auto
    public bool WordWrap { get; set; } = true;
    public bool StartReadOnly { get; set; }
    public List<string> FindHistory { get; set; } = [];

    /// <summary>
    /// Active color theme ID (e.g. "dark", "light", "green-phosphor", or a user theme filename).
    /// </summary>
    public string ThemeName { get; set; } = "dark";

    /// <summary>
    /// Font family used for content views. Must be a monospace font.
    /// </summary>
    public string FontFamily { get; set; } = "Consolas";

    /// <summary>
    /// Font size in device-independent pixels for content views.
    /// </summary>
    public int FontSize { get; set; } = 14;

    /// <summary>Per-file CSV dialect settings, keyed by full file path.</summary>
    public Dictionary<string, CsvFileSettings> CsvFileSettings { get; set; } = new();

    /// <summary>
    /// Adds a file to the recent list. Skips files already pinned.
    /// </summary>
    public void AddRecent(string filePath)
    {
        if (PinnedFiles.Contains(filePath))
            return;

        RecentFiles.Remove(filePath);
        RecentFiles.Insert(0, filePath);
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        Save();
    }

    /// <summary>
    /// Pins a file: removes it from recent files and adds it to the pinned list.
    /// </summary>
    public void PinFile(string filePath)
    {
        if (PinnedFiles.Contains(filePath))
            return;

        RecentFiles.Remove(filePath);
        PinnedFiles.Add(filePath);
        Save();
    }

    /// <summary>
    /// Unpins a file: removes it from pinned files and inserts it at the top of recent files.
    /// </summary>
    public void UnpinFile(string filePath)
    {
        if (!PinnedFiles.Remove(filePath))
            return;

        RecentFiles.Remove(filePath);
        RecentFiles.Insert(0, filePath);
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        Save();
    }

    /// <summary>
    /// Removes a file from both pinned and recent lists.
    /// </summary>
    public void RemoveFile(string filePath)
    {
        PinnedFiles.Remove(filePath);
        RecentFiles.Remove(filePath);
        Save();
    }

    public void AddFindHistory(string query)
    {
        FindHistory.Remove(query);
        FindHistory.Insert(0, query);
        if (FindHistory.Count > MaxFindHistory)
            FindHistory.RemoveRange(MaxFindHistory, FindHistory.Count - MaxFindHistory);
        Save();
    }

    /// <summary>
    /// Stores CSV dialect settings for a specific file.
    /// </summary>
    public void SetCsvFileSettings(string filePath, CsvFileSettings settings)
    {
        CsvFileSettings[filePath] = settings;

        if (CsvFileSettings.Count > MaxCsvFileSettings) {
            string? oldest = null;
            foreach (string key in CsvFileSettings.Keys) {
                oldest = key;
                break;
            }
            if (oldest is not null)
                CsvFileSettings.Remove(oldest);
        }

        Save();
    }

    /// <summary>
    /// Retrieves per-file CSV settings, or null if none stored.
    /// </summary>
    public CsvFileSettings? GetCsvFileSettings(string filePath)
    {
        return CsvFileSettings.TryGetValue(filePath, out CsvFileSettings? settings) ? settings : null;
    }

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "gui-settings.json");

    public static GuiSettings Load()
    {
        try {
            string path = SettingsPath;
            if (!File.Exists(path))
                return new GuiSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, GuiSettingsContext.Default.GuiSettings)
                   ?? new GuiSettings();
        } catch {
            return new GuiSettings();
        }
    }

    /// <summary>
    /// Saves settings with file locking and read-merge-write to support
    /// concurrent Leviathan instances.
    /// </summary>
    public void Save()
    {
        try {
            string path = SettingsPath;

            using FileStream fs = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            GuiSettings? diskSettings = null;
            if (fs.Length > 0) {
                try {
                    byte[] existing = new byte[fs.Length];
                    fs.ReadExactly(existing);
                    diskSettings = JsonSerializer.Deserialize(existing, GuiSettingsContext.Default.GuiSettings);
                } catch {
                    // Corrupt file — overwrite entirely
                }
            }

            if (diskSettings is not null) {
                foreach (KeyValuePair<string, CsvFileSettings> kvp in diskSettings.CsvFileSettings) {
                    CsvFileSettings.TryAdd(kvp.Key, kvp.Value);
                }
            }

            fs.SetLength(0);
            fs.Seek(0, SeekOrigin.Begin);
            JsonSerializer.Serialize(fs, this, GuiSettingsContext.Default.GuiSettings);
            fs.Flush();
        } catch {
            // Best effort — settings are not critical
        }
    }
}

/// <summary>
/// Per-file CSV dialect settings.
/// </summary>
public sealed class CsvFileSettings
{
    public byte Separator { get; set; } = (byte)',';
    public byte Quote { get; set; } = (byte)'"';
    public byte Escape { get; set; } = (byte)'"';
    public bool HasHeader { get; set; } = true;
}

[JsonSerializable(typeof(GuiSettings))]
[JsonSerializable(typeof(CsvFileSettings))]
[JsonSerializable(typeof(Dictionary<string, CsvFileSettings>))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class GuiSettingsContext : JsonSerializerContext;
