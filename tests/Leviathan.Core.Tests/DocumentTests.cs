namespace Leviathan.Core.Tests;

public class DocumentTests
{
    static private string CreateTempFile(byte[] content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Open_ExistingFile_ReportsCorrectLength()
    {
        var data = new byte[1024];
        Random.Shared.NextBytes(data);
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            Assert.Equal(1024, doc.Length);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ReturnsOriginalContent()
    {
        var data = "Hello"u8.ToArray(); // "Hello"
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            Span<byte> buf = stackalloc byte[5];
            int read = doc.Read(0, buf);

            Assert.Equal(5, read);
            Assert.True(buf.SequenceEqual(data));
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Insert_IncreasesLength()
    {
        var data = new byte[100];
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Insert(50, [0xFF, 0xFE]);

            Assert.Equal(102, doc.Length);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Insert_DataIsReadable()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Insert(2, [0xAA, 0xBB]);

            Span<byte> buf = stackalloc byte[7];
            doc.Read(0, buf);

            Assert.Equal(1, buf[0]);
            Assert.Equal(2, buf[1]);
            Assert.Equal(0xAA, buf[2]);
            Assert.Equal(0xBB, buf[3]);
            Assert.Equal(3, buf[4]);
            Assert.Equal(4, buf[5]);
            Assert.Equal(5, buf[6]);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Delete_DecreasesLength()
    {
        var data = new byte[100];
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Delete(10, 20);

            Assert.Equal(80, doc.Length);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveTo_ProducesCorrectFile()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var path = CreateTempFile(data);
        var savePath = Path.GetTempFileName();

        try {
            using var doc = new Document(path);
            doc.Insert(2, [0xAA]);
            doc.SaveTo(savePath);

            var saved = File.ReadAllBytes(savePath);
            Assert.Equal(new byte[] { 1, 2, 0xAA, 3, 4, 5 }, saved);
        } finally {
            File.Delete(path);
            File.Delete(savePath);
        }
    }

    [Fact]
    public void EmptyDocument_HasZeroLength()
    {
        using var doc = new Document();
        Assert.Equal(0, doc.Length);
    }

    [Fact]
    public void EmptyDocument_InsertAndRead()
    {
        using var doc = new Document();
        doc.Insert(0, [0x41, 0x42, 0x43]); // "ABC"

        Assert.Equal(3, doc.Length);

        Span<byte> buf = stackalloc byte[3];
        doc.Read(0, buf);
        Assert.Equal(0x41, buf[0]);
        Assert.Equal(0x42, buf[1]);
        Assert.Equal(0x43, buf[2]);
    }

    [Fact]
    public void SaveTo_OverwritesExistingDestination()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var srcPath = Path.GetTempFileName();
        var dstPath = Path.GetTempFileName(); // already exists

        try {
            File.WriteAllBytes(srcPath, data);
            File.WriteAllBytes(dstPath, [0xFF, 0xFF, 0xFF, 0xFF]);

            using var doc = new Document(srcPath);
            doc.SaveTo(dstPath);

            var saved = File.ReadAllBytes(dstPath);
            Assert.Equal(data, saved);
        } finally {
            File.Delete(srcPath);
            File.Delete(dstPath);
        }
    }

    [Fact]
    public void SaveTo_EmptyDocument_WritesEmptyFile()
    {
        var dstPath = Path.GetTempFileName();
        try {
            using var doc = new Document(); // empty, no backing file
            doc.SaveTo(dstPath);

            long length = new FileInfo(dstPath).Length;
            Assert.Equal(0, length);
        } finally {
            File.Delete(dstPath);
        }
    }

    [Fact]
    public void IsModified_FalseByDefault()
    {
        var data = new byte[] { 1, 2, 3 };
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            Assert.False(doc.IsModified);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsModified_TrueAfterInsert()
    {
        var data = new byte[] { 1, 2, 3 };
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Insert(0, [0xFF]);
            Assert.True(doc.IsModified);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsModified_TrueAfterDelete()
    {
        var data = new byte[] { 1, 2, 3 };
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Delete(0, 1);
            Assert.True(doc.IsModified);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsModified_FalseAfterSave()
    {
        var data = new byte[] { 1, 2, 3 };
        var path = CreateTempFile(data);
        var savePath = Path.GetTempFileName();

        try {
            using var doc = new Document(path);
            doc.Insert(0, [0xFF]);
            Assert.True(doc.IsModified);

            doc.SaveTo(savePath);
            Assert.False(doc.IsModified);
        } finally {
            File.Delete(path);
            File.Delete(savePath);
        }
    }

    [Fact]
    public void SaveTo_SameFile_Succeeds()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Insert(2, [0xAA]);
            doc.SaveTo(path);

            var saved = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 1, 2, 0xAA, 3, 4, 5 }, saved);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveTo_SameFile_ResetsModifiedFlag()
    {
        var data = new byte[] { 1, 2, 3 };
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Insert(0, [0xFF]);
            Assert.True(doc.IsModified);

            doc.SaveTo(path);
            Assert.False(doc.IsModified);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveTo_SameFile_DocumentStillReadable()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var path = CreateTempFile(data);

        try {
            using var doc = new Document(path);
            doc.Insert(2, [0xAA]);
            doc.SaveTo(path);

            // Document should reflect the saved content
            Assert.Equal(6, doc.Length);
            Span<byte> buf = stackalloc byte[6];
            int read = doc.Read(0, buf);
            Assert.Equal(6, read);
            Assert.True(buf.SequenceEqual(new byte[] { 1, 2, 0xAA, 3, 4, 5 }));

            // Further edits should still work
            doc.Insert(0, [0xBB]);
            Assert.Equal(7, doc.Length);
            Assert.True(doc.IsModified);

            Span<byte> buf2 = stackalloc byte[7];
            doc.Read(0, buf2);
            Assert.Equal(0xBB, buf2[0]);
            Assert.Equal(1, buf2[1]);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenFile_AllowsExternalAppendWhileDocumentIsOpen()
    {
        if (!OperatingSystem.IsWindows())
            return;

        byte[] data = [1, 2, 3, 4];
        string path = CreateTempFile(data);

        try {
            using var doc = new Document(path);

            Exception? appendException = Record.Exception(() => {
                using var appendStream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                appendStream.WriteByte(0x7E);
                appendStream.Flush(flushToDisk: true);
            });

            Assert.Null(appendException);
            Assert.Equal(data.Length + 1, new FileInfo(path).Length);
        } finally {
            File.Delete(path);
        }
    }
}
