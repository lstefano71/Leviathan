using System.Text.Json;
using System.Text.Json.Serialization;

namespace Leviathan.UI;

/// <summary>
/// Application settings including MRU list. Persisted as settings.json next to the executable.
/// </summary>
public sealed class Settings
{
  private const int MaxRecentFiles = 10;

  public List<string> RecentFiles { get; set; } = [];
  public int BytesPerRow { get; set; } // 0 = auto

  /// <summary>
  /// Adds a file to the top of the MRU list (deduplicates, trims to max).
  /// </summary>
  public void AddRecent(string filePath)
  {
    filePath = Path.GetFullPath(filePath);
    RecentFiles.RemoveAll(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
    RecentFiles.Insert(0, filePath);
    if (RecentFiles.Count > MaxRecentFiles)
      RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    Save();
  }

  private static string SettingsPath =>
      Path.Combine(AppContext.BaseDirectory, "settings.json");

  public static Settings Load()
  {
    try {
      string path = SettingsPath;
      if (File.Exists(path)) {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings) ?? new Settings();
      }
    } catch {
      // Corrupted settings — start fresh
    }
    return new Settings();
  }

  public void Save()
  {
    try {
      string json = JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings);
      File.WriteAllText(SettingsPath, json);
    } catch {
      // Best effort — don't crash on save failure
    }
  }
}

/// <summary>
/// Source-generated JSON serializer context for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(Settings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;
