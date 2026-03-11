using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.TUI;

/// <summary>
/// Persisted settings for the TUI frontend. AOT-safe via source-generated JSON context.
/// </summary>
internal sealed class TuiSettings
{
    private const int MaxRecentFiles = 10;
    private const int MaxFindHistory = 20;

    public List<string> RecentFiles { get; set; } = [];
    public int BytesPerRow { get; set; } // 0 = auto
    public bool WordWrap { get; set; } = true;
    public List<string> FindHistory { get; set; } = [];

    public void AddRecent(string filePath)
    {
        RecentFiles.Remove(filePath);
        RecentFiles.Insert(0, filePath);
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
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

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "tui-settings.json");

    public static TuiSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path))
                return new TuiSettings();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, TuiSettingsContext.Default.TuiSettings)
                   ?? new TuiSettings();
        }
        catch
        {
            return new TuiSettings();
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, TuiSettingsContext.Default.TuiSettings);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best effort — settings are not critical
        }
    }
}

[JsonSerializable(typeof(TuiSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class TuiSettingsContext : JsonSerializerContext;
