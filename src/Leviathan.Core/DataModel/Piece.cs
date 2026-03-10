namespace Leviathan.Core.DataModel;

/// <summary>
/// Identifies which buffer a piece refers to.
/// </summary>
public enum PieceSource : byte
{
  Original = 0,
  Append = 1
}

/// <summary>
/// A single piece in the piece table. Immutable value type — no GC pressure.
/// </summary>
public readonly record struct Piece(PieceSource Source, long Offset, long Length);
