using System.Text.Json;

namespace Leviathan.Core.DataModel;

/// <summary>
/// Persists bookmarks to a JSON sidecar file next to the document.
/// Uses source-generated JSON for AOT compatibility.
/// </summary>
public static class BookmarkSerializer
{
    private const string SidecarExtension = ".leviathan-bookmarks";

    /// <summary>
    /// Returns the sidecar file path for the given document path.
    /// </summary>
    public static string GetSidecarPath(string documentPath)
    {
        return documentPath + SidecarExtension;
    }

    /// <summary>
    /// Saves all bookmarks from the collection to a JSON sidecar file.
    /// If the collection is empty, deletes the sidecar file.
    /// </summary>
    public static void Save(string documentPath, BookmarkCollection bookmarks)
    {
        string sidecarPath = GetSidecarPath(documentPath);

        if (bookmarks.Count == 0) {
            try { File.Delete(sidecarPath); } catch { /* best effort */ }
            return;
        }

        BookmarkFileData data = new();
        foreach (Bookmark bm in bookmarks.GetAll()) {
            data.Bookmarks.Add(new BookmarkEntry {
                Offset = bm.Offset,
                Label = bm.Label,
                CreatedUtc = bm.CreatedUtc
            });
        }

        try {
            string json = JsonSerializer.Serialize(data, BookmarkJsonContext.Default.BookmarkFileData);
            File.WriteAllText(sidecarPath, json);
        } catch {
            // Best effort — bookmark persistence is not critical
        }
    }

    /// <summary>
    /// Loads bookmarks from the sidecar file into the collection.
    /// Clears the collection first. If no sidecar exists, the collection is left empty.
    /// </summary>
    public static void Load(string documentPath, BookmarkCollection bookmarks)
    {
        bookmarks.Clear();
        string sidecarPath = GetSidecarPath(documentPath);

        if (!File.Exists(sidecarPath))
            return;

        try {
            string json = File.ReadAllText(sidecarPath);
            BookmarkFileData? data = JsonSerializer.Deserialize(json, BookmarkJsonContext.Default.BookmarkFileData);
            if (data?.Bookmarks is null)
                return;

            IEnumerable<Bookmark> loaded = data.Bookmarks.Select(static e =>
                new Bookmark(e.Offset, e.Label, e.CreatedUtc));
            bookmarks.Load(loaded);
        } catch {
            // Corrupt sidecar — start with empty bookmarks
        }
    }
}
