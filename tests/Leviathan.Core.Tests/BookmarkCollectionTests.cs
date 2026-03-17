using Leviathan.Core.DataModel;

namespace Leviathan.Core.Tests;

public class BookmarkCollectionTests
{
    [Fact]
    public void Add_SingleBookmark_CountIsOne()
    {
        BookmarkCollection col = new();
        col.Add(100, "test");

        Assert.Equal(1, col.Count);
        Assert.Equal(100, col.GetAll()[0].Offset);
        Assert.Equal("test", col.GetAll()[0].Label);
    }

    [Fact]
    public void Add_MultipleBookmarks_SortedByOffset()
    {
        BookmarkCollection col = new();
        col.Add(300, "c");
        col.Add(100, "a");
        col.Add(200, "b");

        IReadOnlyList<Bookmark> all = col.GetAll();
        Assert.Equal(3, all.Count);
        Assert.Equal(100, all[0].Offset);
        Assert.Equal(200, all[1].Offset);
        Assert.Equal(300, all[2].Offset);
    }

    [Fact]
    public void Add_DuplicateOffset_ReplacesExisting()
    {
        BookmarkCollection col = new();
        col.Add(100, "first");
        col.Add(100, "second");

        Assert.Equal(1, col.Count);
        Assert.Equal("second", col.GetAll()[0].Label);
    }

    [Fact]
    public void Remove_ExistingBookmark_ReturnsTrue()
    {
        BookmarkCollection col = new();
        col.Add(100, "test");

        Assert.True(col.Remove(100));
        Assert.Equal(0, col.Count);
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        BookmarkCollection col = new();
        col.Add(100, "test");

        Assert.False(col.Remove(200));
        Assert.Equal(1, col.Count);
    }

    [Fact]
    public void Toggle_AddsThenRemoves()
    {
        BookmarkCollection col = new();

        bool added = col.Toggle(100, "test");
        Assert.True(added);
        Assert.Equal(1, col.Count);

        bool removed = col.Toggle(100);
        Assert.False(removed);
        Assert.Equal(0, col.Count);
    }

    [Fact]
    public void Contains_ExistingOffset_ReturnsTrue()
    {
        BookmarkCollection col = new();
        col.Add(100);
        col.Add(200);

        Assert.True(col.Contains(100));
        Assert.True(col.Contains(200));
        Assert.False(col.Contains(150));
    }

    [Fact]
    public void TryGetNearest_EmptyCollection_ReturnsFalse()
    {
        BookmarkCollection col = new();

        Assert.False(col.TryGetNearest(100, out _));
    }

    [Fact]
    public void TryGetNearest_ExactMatch_ReturnsIt()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(200, "b");
        col.Add(300, "c");

        Assert.True(col.TryGetNearest(200, out Bookmark nearest));
        Assert.Equal(200, nearest.Offset);
    }

    [Fact]
    public void TryGetNearest_BetweenTwo_ReturnsCloser()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(200, "b");

        Assert.True(col.TryGetNearest(140, out Bookmark nearest));
        Assert.Equal(100, nearest.Offset);

        Assert.True(col.TryGetNearest(160, out Bookmark nearest2));
        Assert.Equal(200, nearest2.Offset);
    }

    [Fact]
    public void TryGetNearest_BeyondLast_ReturnsLast()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(200, "b");

        Assert.True(col.TryGetNearest(999, out Bookmark nearest));
        Assert.Equal(200, nearest.Offset);
    }

    [Fact]
    public void TryGetNearest_BeforeFirst_ReturnsFirst()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(200, "b");

        Assert.True(col.TryGetNearest(0, out Bookmark nearest));
        Assert.Equal(100, nearest.Offset);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        BookmarkCollection col = new();
        col.Add(100);
        col.Add(200);
        col.Clear();

        Assert.Equal(0, col.Count);
    }

    [Fact]
    public void AdjustForInsert_ShiftsBookmarksAtAndAfterOffset()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(200, "b");
        col.Add(300, "c");

        col.AdjustForInsert(200, 50);

        IReadOnlyList<Bookmark> all = col.GetAll();
        Assert.Equal(100, all[0].Offset); // before insert — unchanged
        Assert.Equal(250, all[1].Offset); // at insert — shifted
        Assert.Equal(350, all[2].Offset); // after insert — shifted
    }

    [Fact]
    public void AdjustForInsert_BeforeAll_ShiftsEverything()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(200, "b");

        col.AdjustForInsert(0, 10);

        Assert.Equal(110, col.GetAll()[0].Offset);
        Assert.Equal(210, col.GetAll()[1].Offset);
    }

    [Fact]
    public void AdjustForDelete_RemovesBookmarksInRange()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(150, "b");
        col.Add(200, "c");
        col.Add(300, "d");

        col.AdjustForDelete(120, 100); // deletes [120, 220)

        IReadOnlyList<Bookmark> all = col.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(100, all[0].Offset); // before range — unchanged
        Assert.Equal(200, all[1].Offset); // 300 - 100 = 200
    }

    [Fact]
    public void AdjustForDelete_ShiftsBookmarksAfterRange()
    {
        BookmarkCollection col = new();
        col.Add(100, "a");
        col.Add(500, "b");

        col.AdjustForDelete(200, 100); // deletes [200, 300)

        Assert.Equal(100, col.GetAll()[0].Offset);
        Assert.Equal(400, col.GetAll()[1].Offset);
    }

    [Fact]
    public void Load_ReplacesExistingBookmarks()
    {
        BookmarkCollection col = new();
        col.Add(100, "old");

        Bookmark[] newBookmarks = [
            new Bookmark(500, "x", DateTime.UtcNow),
            new Bookmark(200, "y", DateTime.UtcNow)
        ];
        col.Load(newBookmarks);

        Assert.Equal(2, col.Count);
        Assert.Equal(200, col.GetAll()[0].Offset); // sorted
        Assert.Equal(500, col.GetAll()[1].Offset);
    }
}
