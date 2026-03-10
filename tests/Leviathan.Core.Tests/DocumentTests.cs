namespace Leviathan.Core.Tests;

public class DocumentTests
{
  private string CreateTempFile(byte[] content)
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
    var data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }; // "Hello"
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
      doc.Insert(50, new byte[] { 0xFF, 0xFE });

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
      doc.Insert(2, new byte[] { 0xAA, 0xBB });

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
      doc.Insert(2, new byte[] { 0xAA });
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
    doc.Insert(0, new byte[] { 0x41, 0x42, 0x43 }); // "ABC"

    Assert.Equal(3, doc.Length);

    Span<byte> buf = stackalloc byte[3];
    doc.Read(0, buf);
    Assert.Equal(0x41, buf[0]);
    Assert.Equal(0x42, buf[1]);
    Assert.Equal(0x43, buf[2]);
  }
}
