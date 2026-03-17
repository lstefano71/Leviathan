using System.Text.Json.Serialization;

namespace Leviathan.Core.DataModel;

/// <summary>
/// A single bookmark marking a byte offset in a document.
/// Immutable value type — no GC pressure.
/// </summary>
public readonly record struct Bookmark(long Offset, string Label, DateTime CreatedUtc)
{
    /// <summary>
    /// Creates a bookmark at the given offset with an auto-generated label.
    /// </summary>
    public static Bookmark At(long offset, string? label = null)
    {
        return new Bookmark(offset, label ?? $"Bookmark @ 0x{offset:X}", DateTime.UtcNow);
    }
}

/// <summary>
/// Collection of bookmarks sorted by offset. Provides add/remove/toggle semantics
/// and offset adjustment when the document is edited.
/// </summary>
public sealed class BookmarkCollection
{
    private readonly List<Bookmark> _bookmarks = [];

    /// <summary>Number of bookmarks in the collection.</summary>
    public int Count => _bookmarks.Count;

    /// <summary>Returns all bookmarks in offset order.</summary>
    public IReadOnlyList<Bookmark> GetAll() => _bookmarks;

    /// <summary>
    /// Adds a bookmark at the given offset. If a bookmark already exists at that offset,
    /// it is replaced with the new label.
    /// </summary>
    public void Add(long offset, string? label = null)
    {
        Remove(offset);
        Bookmark bm = Bookmark.At(offset, label);
        int idx = FindInsertionIndex(offset);
        _bookmarks.Insert(idx, bm);
    }

    /// <summary>
    /// Removes the bookmark at the exact offset. Returns true if one was removed.
    /// </summary>
    public bool Remove(long offset)
    {
        for (int i = 0; i < _bookmarks.Count; i++) {
            if (_bookmarks[i].Offset == offset) {
                _bookmarks.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Toggles a bookmark: removes it if one exists at the offset, adds it otherwise.
    /// Returns true if a bookmark was added, false if one was removed.
    /// </summary>
    public bool Toggle(long offset, string? label = null)
    {
        if (Remove(offset))
            return false;
        Add(offset, label);
        return true;
    }

    /// <summary>
    /// Finds the bookmark nearest to the given offset, or returns false if empty.
    /// </summary>
    public bool TryGetNearest(long offset, out Bookmark nearest)
    {
        nearest = default;
        if (_bookmarks.Count == 0)
            return false;

        int idx = FindInsertionIndex(offset);
        if (idx >= _bookmarks.Count)
            idx = _bookmarks.Count - 1;

        nearest = _bookmarks[idx];
        long bestDist = Math.Abs(nearest.Offset - offset);

        if (idx > 0) {
            long dist = Math.Abs(_bookmarks[idx - 1].Offset - offset);
            if (dist < bestDist) {
                nearest = _bookmarks[idx - 1];
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if a bookmark exists at the exact offset.
    /// </summary>
    public bool Contains(long offset)
    {
        for (int i = 0; i < _bookmarks.Count; i++) {
            if (_bookmarks[i].Offset == offset)
                return true;
            if (_bookmarks[i].Offset > offset)
                break;
        }
        return false;
    }

    /// <summary>Removes all bookmarks.</summary>
    public void Clear() => _bookmarks.Clear();

    /// <summary>
    /// Adjusts bookmark offsets after an insertion. Bookmarks at or after the insertion
    /// point are shifted forward by the inserted length.
    /// </summary>
    public void AdjustForInsert(long offset, long length)
    {
        for (int i = 0; i < _bookmarks.Count; i++) {
            if (_bookmarks[i].Offset >= offset) {
                Bookmark bm = _bookmarks[i];
                _bookmarks[i] = bm with { Offset = bm.Offset + length };
            }
        }
    }

    /// <summary>
    /// Adjusts bookmark offsets after a deletion. Bookmarks inside the deleted range
    /// are removed; bookmarks after the range are shifted back.
    /// </summary>
    public void AdjustForDelete(long offset, long length)
    {
        long deleteEnd = offset + length;
        for (int i = _bookmarks.Count - 1; i >= 0; i--) {
            long bmOffset = _bookmarks[i].Offset;
            if (bmOffset >= offset && bmOffset < deleteEnd) {
                _bookmarks.RemoveAt(i);
            } else if (bmOffset >= deleteEnd) {
                Bookmark bm = _bookmarks[i];
                _bookmarks[i] = bm with { Offset = bm.Offset - length };
            }
        }
    }

    /// <summary>
    /// Replaces all bookmarks with the given list (used for deserialization).
    /// </summary>
    internal void Load(IEnumerable<Bookmark> bookmarks)
    {
        _bookmarks.Clear();
        _bookmarks.AddRange(bookmarks.OrderBy(static b => b.Offset));
    }

    private int FindInsertionIndex(long offset)
    {
        int lo = 0, hi = _bookmarks.Count;
        while (lo < hi) {
            int mid = lo + (hi - lo) / 2;
            if (_bookmarks[mid].Offset < offset)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}

/// <summary>
/// Serializable bookmark data for JSON persistence. AOT-safe via source-generated context.
/// </summary>
public sealed class BookmarkFileData
{
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
}

/// <summary>
/// Single bookmark entry for JSON serialization.
/// </summary>
public sealed class BookmarkEntry
{
    public long Offset { get; set; }
    public string Label { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// AOT-safe JSON serializer context for bookmark persistence.
/// </summary>
[JsonSerializable(typeof(BookmarkFileData))]
[JsonSerializable(typeof(List<BookmarkEntry>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public sealed partial class BookmarkJsonContext : JsonSerializerContext;
