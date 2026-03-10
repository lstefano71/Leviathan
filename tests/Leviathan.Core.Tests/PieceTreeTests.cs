using Leviathan.Core.DataModel;

namespace Leviathan.Core.Tests;

public class PieceTreeTests
{
  [Fact]
  public void Init_SinglePiece_HasCorrectLength()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 1000));

    Assert.Equal(1000, tree.TotalLength);
    Assert.Equal(1, tree.PieceCount);
  }

  [Fact]
  public void Insert_AtBeginning_SplitsCorrectly()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Insert(0, new Piece(PieceSource.Append, 0, 5));

    Assert.Equal(105, tree.TotalLength);
  }

  [Fact]
  public void Insert_AtEnd_AppendsCorrectly()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Insert(100, new Piece(PieceSource.Append, 0, 10));

    Assert.Equal(110, tree.TotalLength);
  }

  [Fact]
  public void Insert_InMiddle_SplitsIntoThreePieces()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Insert(50, new Piece(PieceSource.Append, 0, 5));

    Assert.Equal(105, tree.TotalLength);
    Assert.Equal(3, tree.PieceCount); // left, inserted, right

    var pieces = tree.InOrder().ToList();
    Assert.Equal(3, pieces.Count);
    Assert.Equal(50, pieces[0].Length);  // original left half
    Assert.Equal(5, pieces[1].Length);   // inserted
    Assert.Equal(50, pieces[2].Length);  // original right half
  }

  [Fact]
  public void Delete_EntirePiece_RemovesIt()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Delete(0, 100);

    Assert.Equal(0, tree.TotalLength);
  }

  [Fact]
  public void Delete_FromMiddle_SplitsIntoPieces()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Delete(40, 20); // delete bytes 40..59

    Assert.Equal(80, tree.TotalLength);

    var pieces = tree.InOrder().ToList();
    Assert.Equal(2, pieces.Count);
    Assert.Equal(40, pieces[0].Length);  // [0..39]
    Assert.Equal(40, pieces[1].Length);  // [60..99]
    Assert.Equal(60, pieces[1].Offset);
  }

  [Fact]
  public void Delete_FromStart_TrimsFirstPiece()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Delete(0, 10);

    Assert.Equal(90, tree.TotalLength);
  }

  [Fact]
  public void Delete_FromEnd_TrimsLastPiece()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Delete(90, 10);

    Assert.Equal(90, tree.TotalLength);
  }

  [Fact]
  public void ManyInsertions_MaintainCorrectLength()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 1000));

    for (int i = 0; i < 1000; i++) {
      tree.Insert(i * 2, new Piece(PieceSource.Append, i, 1));
    }

    Assert.Equal(2000, tree.TotalLength);
  }

  [Fact]
  public void Read_ReturnsCorrectData()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 10));

    // Insert 3 bytes at position 5
    tree.Insert(5, new Piece(PieceSource.Append, 0, 3));

    Span<byte> buffer = stackalloc byte[13];
    int read = tree.Read(0, buffer, (source, offset, length) => {
      // Simulate: Original = bytes 0-9, Append = bytes 100-102
      Span<byte> result = new byte[length];
      for (int i = 0; i < length; i++) {
        result[i] = source == PieceSource.Original
            ? (byte)(offset + i)
            : (byte)(100 + offset + i);
      }
      return result;
    });

    Assert.Equal(13, read);
    // First 5 from original
    for (int i = 0; i < 5; i++)
      Assert.Equal((byte)i, buffer[i]);
    // 3 from append
    Assert.Equal(100, buffer[5]);
    Assert.Equal(101, buffer[6]);
    Assert.Equal(102, buffer[7]);
    // Last 5 from original (offset 5-9)
    for (int i = 0; i < 5; i++)
      Assert.Equal((byte)(5 + i), buffer[8 + i]);
  }

  [Fact]
  public void FindByOffset_ReturnsCorrectNode()
  {
    var tree = new PieceTree();
    tree.Init(new Piece(PieceSource.Original, 0, 100));

    tree.Insert(50, new Piece(PieceSource.Append, 0, 10));

    // Offset 0 should be in original piece
    var (node0, local0) = tree.FindByOffset(0);
    Assert.Equal(PieceSource.Original, node0.Piece.Source);
    Assert.Equal(0, local0);

    // Offset 50 should be in append piece
    var (node50, local50) = tree.FindByOffset(50);
    Assert.Equal(PieceSource.Append, node50.Piece.Source);
    Assert.Equal(0, local50);

    // Offset 60 should be in the second original piece
    var (node60, local60) = tree.FindByOffset(60);
    Assert.Equal(PieceSource.Original, node60.Piece.Source);
    Assert.Equal(0, local60);
  }
}
