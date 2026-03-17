using Leviathan.Core.DataModel;

namespace Leviathan.Core.Tests;

public class BookmarkSerializerTests
{
    [Fact]
    public void Save_Load_RoundTrip()
    {
        string tempFile = CreateTempFile();
        try {
            BookmarkCollection original = new();
            original.Add(100, "First");
            original.Add(500, "Second");
            original.Add(1000, "Third");

            BookmarkSerializer.Save(tempFile, original);

            BookmarkCollection loaded = new();
            BookmarkSerializer.Load(tempFile, loaded);

            Assert.Equal(3, loaded.Count);
            IReadOnlyList<Bookmark> all = loaded.GetAll();
            Assert.Equal(100, all[0].Offset);
            Assert.Equal("First", all[0].Label);
            Assert.Equal(500, all[1].Offset);
            Assert.Equal("Second", all[1].Label);
            Assert.Equal(1000, all[2].Offset);
            Assert.Equal("Third", all[2].Label);
        } finally {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void Save_EmptyCollection_DeletesSidecar()
    {
        string tempFile = CreateTempFile();
        try {
            // First save some bookmarks to create the sidecar
            BookmarkCollection col = new();
            col.Add(100, "test");
            BookmarkSerializer.Save(tempFile, col);

            string sidecarPath = BookmarkSerializer.GetSidecarPath(tempFile);
            Assert.True(File.Exists(sidecarPath));

            // Now save empty collection — should delete sidecar
            col.Clear();
            BookmarkSerializer.Save(tempFile, col);

            Assert.False(File.Exists(sidecarPath));
        } finally {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void Load_NoSidecar_LeavesCollectionEmpty()
    {
        string tempFile = CreateTempFile();
        try {
            BookmarkCollection col = new();
            col.Add(100, "existing");

            BookmarkSerializer.Load(tempFile, col);

            Assert.Equal(0, col.Count);
        } finally {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void Load_CorruptSidecar_LeavesCollectionEmpty()
    {
        string tempFile = CreateTempFile();
        try {
            string sidecarPath = BookmarkSerializer.GetSidecarPath(tempFile);
            File.WriteAllText(sidecarPath, "not valid json {{{");

            BookmarkCollection col = new();
            BookmarkSerializer.Load(tempFile, col);

            Assert.Equal(0, col.Count);
        } finally {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void GetSidecarPath_AppendsExtension()
    {
        string path = BookmarkSerializer.GetSidecarPath(@"C:\files\test.bin");
        Assert.Equal(@"C:\files\test.bin.leviathan-bookmarks", path);
    }

    [Fact]
    public void Save_Load_PreservesCreatedUtc()
    {
        string tempFile = CreateTempFile();
        try {
            BookmarkCollection original = new();
            original.Add(42, "timed");

            DateTime beforeSave = original.GetAll()[0].CreatedUtc;

            BookmarkSerializer.Save(tempFile, original);
            BookmarkCollection loaded = new();
            BookmarkSerializer.Load(tempFile, loaded);

            Assert.Equal(1, loaded.Count);
            // DateTime round-trip via JSON may lose sub-tick precision, so compare within 1s
            Assert.True(Math.Abs((loaded.GetAll()[0].CreatedUtc - beforeSave).TotalSeconds) < 1);
        } finally {
            CleanupTempFile(tempFile);
        }
    }

    private static string CreateTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bookmark_test_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[1024]);
        return path;
    }

    private static void CleanupTempFile(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(BookmarkSerializer.GetSidecarPath(path)); } catch { }
    }
}
