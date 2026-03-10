namespace Leviathan.Core.DataModel;

/// <summary>
/// A node in a Red-Black tree that tracks pieces.
/// Each node caches the subtree size for O(log N) positional lookups.
/// </summary>
internal sealed class PieceNode
{
  public Piece Piece;
  public long SubtreeLength; // sum of lengths in this subtree
  public PieceNode? Left;
  public PieceNode? Right;
  public PieceNode? Parent;
  public bool IsRed;

  public PieceNode(Piece piece)
  {
    Piece = piece;
    SubtreeLength = piece.Length;
    IsRed = true; // new nodes are red
  }

  /// <summary>
  /// Recalculates the subtree length from children.
  /// Must be called after structural changes.
  /// </summary>
  public void UpdateSubtreeLength()
  {
    SubtreeLength = Piece.Length
        + (Left?.SubtreeLength ?? 0)
        + (Right?.SubtreeLength ?? 0);
  }
}
