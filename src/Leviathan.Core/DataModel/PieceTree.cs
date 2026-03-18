using System.Runtime.CompilerServices;

namespace Leviathan.Core.DataModel;

/// <summary>
/// A Red-Black tree of <see cref="Piece"/> nodes that represents the logical document.
/// Supports O(log N) insert, delete, and positional lookup.
/// </summary>
public sealed class PieceTree
{
    internal PieceNode? Root;
    public long TotalLength => Root?.SubtreeLength ?? 0;
    public int PieceCount { get; private set; }

    /// <summary>
    /// Initialises the tree with a single piece covering the original file.
    /// </summary>
    public void Init(Piece original)
    {
        Root = new PieceNode(original) { IsRed = false };
        PieceCount = 1;
    }

    /// <summary>
    /// Removes all pieces from the tree, leaving it empty.
    /// </summary>
    public void Clear()
    {
        Root = null;
        PieceCount = 0;
    }

    /// <summary>
    /// Finds the piece containing the given logical offset.
    /// Returns the node and the local offset within that piece.
    /// </summary>
    internal (PieceNode Node, long LocalOffset) FindByOffset(long logicalOffset)
    {
        if (Root is null)
            throw new InvalidOperationException("Tree is empty.");

        if ((ulong)logicalOffset >= (ulong)TotalLength)
            throw new ArgumentOutOfRangeException(nameof(logicalOffset));

        return FindByOffset(Root, logicalOffset);
    }

    private static (PieceNode, long) FindByOffset(PieceNode node, long offset)
    {
        while (true) {
            long leftLen = node.Left?.SubtreeLength ?? 0;

            if (offset < leftLen) {
                node = node.Left!;
                continue;
            }

            offset -= leftLen;

            if (offset < node.Piece.Length)
                return (node, offset);

            offset -= node.Piece.Length;
            node = node.Right!;
        }
    }

    /// <summary>
    /// Inserts new data at the given logical position by splitting an existing piece.
    /// </summary>
    public void Insert(long logicalOffset, Piece newPiece)
    {
        if (Root is null) {
            Init(newPiece);
            return;
        }

        // Clamp to allow append at the very end
        if (logicalOffset == TotalLength) {
            var rightmost = GetRightmost(Root);
            InsertAfter(rightmost, new PieceNode(newPiece));
            PieceCount++;
            return;
        }

        var (target, localOffset) = FindByOffset(logicalOffset);

        if (localOffset == 0) {
            // Insert before this node
            InsertBefore(target, new PieceNode(newPiece));
            PieceCount++;
        } else {
            // Split the target piece
            var origPiece = target.Piece;

            // Left portion stays in the existing node
            target.Piece = new Piece(origPiece.Source, origPiece.Offset, localOffset);

            // Right portion becomes a new trailing node
            var trailingPiece = new Piece(
                origPiece.Source,
                origPiece.Offset + localOffset,
                origPiece.Length - localOffset);

            var trailingNode = new PieceNode(trailingPiece);
            InsertAfter(target, trailingNode);

            // Then insert the new piece between them
            var insertNode = new PieceNode(newPiece);
            InsertAfter(target, insertNode);

            // Fix up subtree lengths
            FixSubtreeLengthsUp(trailingNode);
            FixSubtreeLengthsUp(insertNode);
            FixSubtreeLengthsUp(target);

            PieceCount += 2; // trailing + inserted
        }
    }

    /// <summary>
    /// Deletes a range from the logical document.
    /// </summary>
    public void Delete(long logicalOffset, long length)
    {
        if (length == 0) return;
        if ((ulong)(logicalOffset + length) > (ulong)TotalLength)
            throw new ArgumentOutOfRangeException(nameof(length));

        long remaining = length;
        while (remaining > 0) {
            var (node, localOff) = FindByOffset(logicalOffset);

            if (localOff == 0 && remaining >= node.Piece.Length) {
                // Remove entire node
                long pieceLen = node.Piece.Length;
                RemoveNode(node);
                PieceCount--;
                remaining -= pieceLen;
            } else if (localOff == 0) {
                // Trim from the start of the piece
                node.Piece = new Piece(
                    node.Piece.Source,
                    node.Piece.Offset + remaining,
                    node.Piece.Length - remaining);
                FixSubtreeLengthsUp(node);
                remaining = 0;
            } else if (localOff + remaining >= node.Piece.Length) {
                // Trim from the end of the piece
                long trimmed = node.Piece.Length - localOff;
                node.Piece = new Piece(node.Piece.Source, node.Piece.Offset, localOff);
                FixSubtreeLengthsUp(node);
                remaining -= trimmed;
            } else {
                // Delete from the middle — split into two pieces
                var origPiece = node.Piece;
                node.Piece = new Piece(origPiece.Source, origPiece.Offset, localOff);

                var trailingPiece = new Piece(
                    origPiece.Source,
                    origPiece.Offset + localOff + remaining,
                    origPiece.Length - localOff - remaining);

                var trailingNode = new PieceNode(trailingPiece);
                InsertAfter(node, trailingNode);
                FixSubtreeLengthsUp(trailingNode);
                FixSubtreeLengthsUp(node);
                PieceCount++;
                remaining = 0;
            }
        }
    }

    /// <summary>
    /// Reads bytes from the logical document into the destination buffer.
    /// </summary>
    public delegate ReadOnlySpan<byte> SpanReader(PieceSource source, long offset, int length);

    public int Read(long logicalOffset, Span<byte> destination, SpanReader reader)
    {
        if (Root is null || destination.Length == 0)
            return 0;

        long docLen = TotalLength;
        if (logicalOffset >= docLen)
            return 0;

        int toRead = (int)Math.Min(destination.Length, docLen - logicalOffset);
        int totalRead = 0;

        while (totalRead < toRead) {
            var (node, localOff) = FindByOffset(logicalOffset);
            int chunkLen = (int)Math.Min(node.Piece.Length - localOff, toRead - totalRead);
            var span = reader(node.Piece.Source, node.Piece.Offset + localOff, chunkLen);
            span.CopyTo(destination[totalRead..]);
            totalRead += chunkLen;
            logicalOffset += chunkLen;
        }

        return totalRead;
    }

    /// <summary>
    /// Iterates all pieces in order (left-to-right / in-order traversal).
    /// </summary>
    public IEnumerable<Piece> InOrder()
    {
        if (Root is null) yield break;

        var stack = new Stack<PieceNode>();
        var current = Root;

        while (current is not null || stack.Count > 0) {
            while (current is not null) {
                stack.Push(current);
                current = current.Left;
            }

            current = stack.Pop();
            yield return current.Piece;
            current = current.Right;
        }
    }

    // ─── Red-Black Tree Mechanics ───────────────────────────────────────

    private void InsertBefore(PieceNode target, PieceNode newNode)
    {
        if (target.Left is null) {
            target.Left = newNode;
            newNode.Parent = target;
        } else {
            var pred = GetRightmost(target.Left);
            pred.Right = newNode;
            newNode.Parent = pred;
        }

        FixSubtreeLengthsUp(newNode);
        InsertFixup(newNode);
    }

    private void InsertAfter(PieceNode target, PieceNode newNode)
    {
        if (target.Right is null) {
            target.Right = newNode;
            newNode.Parent = target;
        } else {
            var succ = GetLeftmost(target.Right);
            succ.Left = newNode;
            newNode.Parent = succ;
        }

        FixSubtreeLengthsUp(newNode);
        InsertFixup(newNode);
    }

    private void InsertFixup(PieceNode node)
    {
        while (node != Root && node.Parent is { IsRed: true }) {
            var parent = node.Parent!;
            var grandparent = parent.Parent!;

            if (parent == grandparent.Left) {
                var uncle = grandparent.Right;
                if (uncle is { IsRed: true }) {
                    parent.IsRed = false;
                    uncle.IsRed = false;
                    grandparent.IsRed = true;
                    node = grandparent;
                } else {
                    if (node == parent.Right) {
                        RotateLeft(parent);
                        node = parent;
                        parent = node.Parent!;
                    }

                    parent.IsRed = false;
                    grandparent.IsRed = true;
                    RotateRight(grandparent);
                }
            } else {
                var uncle = grandparent.Left;
                if (uncle is { IsRed: true }) {
                    parent.IsRed = false;
                    uncle.IsRed = false;
                    grandparent.IsRed = true;
                    node = grandparent;
                } else {
                    if (node == parent.Left) {
                        RotateRight(parent);
                        node = parent;
                        parent = node.Parent!;
                    }

                    parent.IsRed = false;
                    grandparent.IsRed = true;
                    RotateLeft(grandparent);
                }
            }
        }

        Root!.IsRed = false;
    }

    private void RemoveNode(PieceNode node)
    {
        // Standard BST removal followed by RB fixup
        PieceNode? replacement;
        PieceNode? fixNode;
        PieceNode? fixParent;
        bool needsFix;

        if (node.Left is not null && node.Right is not null) {
            // Two children — swap with in-order successor
            var successor = GetLeftmost(node.Right);
            node.Piece = successor.Piece;
            node.UpdateSubtreeLength();
            FixSubtreeLengthsUp(node);
            node = successor;
        }

        replacement = node.Left ?? node.Right;
        fixParent = node.Parent;
        needsFix = !node.IsRed;

        if (replacement is not null) {
            replacement.Parent = node.Parent;
            if (node.Parent is null)
                Root = replacement;
            else if (node == node.Parent.Left)
                node.Parent.Left = replacement;
            else
                node.Parent.Right = replacement;

            fixNode = replacement;
        } else if (node.Parent is null) {
            Root = null;
            PieceCount = 0;
            return;
        } else {
            fixNode = null;
            fixParent = node.Parent;

            if (node == node.Parent.Left)
                node.Parent.Left = null;
            else
                node.Parent.Right = null;
        }

        FixSubtreeLengthsUp(fixParent);

        if (needsFix)
            DeleteFixup(fixNode, fixParent);
    }

    private void DeleteFixup(PieceNode? node, PieceNode? parent)
    {
        while (node != Root && (node is null || !node.IsRed)) {
            if (parent is null) break;

            if (node == parent.Left) {
                var sibling = parent.Right;
                if (sibling is { IsRed: true }) {
                    sibling.IsRed = false;
                    parent.IsRed = true;
                    RotateLeft(parent);
                    sibling = parent.Right;
                }

                if (sibling is null) break;

                if ((sibling.Left is null || !sibling.Left.IsRed) &&
                    (sibling.Right is null || !sibling.Right.IsRed)) {
                    sibling.IsRed = true;
                    node = parent;
                    parent = node.Parent;
                } else {
                    if (sibling.Right is null || !sibling.Right.IsRed) {
                        sibling.Left?.IsRed = false;
                        sibling.IsRed = true;
                        RotateRight(sibling);
                        sibling = parent.Right;
                    }

                    sibling!.IsRed = parent.IsRed;
                    parent.IsRed = false;
                    sibling.Right?.IsRed = false;
                    RotateLeft(parent);
                    node = Root;
                    break;
                }
            } else {
                var sibling = parent.Left;
                if (sibling is { IsRed: true }) {
                    sibling.IsRed = false;
                    parent.IsRed = true;
                    RotateRight(parent);
                    sibling = parent.Left;
                }

                if (sibling is null) break;

                if ((sibling.Right is null || !sibling.Right.IsRed) &&
                    (sibling.Left is null || !sibling.Left.IsRed)) {
                    sibling.IsRed = true;
                    node = parent;
                    parent = node.Parent;
                } else {
                    if (sibling.Left is null || !sibling.Left.IsRed) {
                        sibling.Right?.IsRed = false;
                        sibling.IsRed = true;
                        RotateLeft(sibling);
                        sibling = parent.Left;
                    }

                    sibling!.IsRed = parent.IsRed;
                    parent.IsRed = false;
                    sibling.Left?.IsRed = false;
                    RotateRight(parent);
                    node = Root;
                    break;
                }
            }
        }

        node?.IsRed = false;
    }

    private void RotateLeft(PieceNode node)
    {
        var right = node.Right!;
        node.Right = right.Left;
        right.Left?.Parent = node;

        right.Parent = node.Parent;
        if (node.Parent is null)
            Root = right;
        else if (node == node.Parent.Left)
            node.Parent.Left = right;
        else
            node.Parent.Right = right;

        right.Left = node;
        node.Parent = right;

        node.UpdateSubtreeLength();
        right.UpdateSubtreeLength();
    }

    private void RotateRight(PieceNode node)
    {
        var left = node.Left!;
        node.Left = left.Right;
        left.Right?.Parent = node;

        left.Parent = node.Parent;
        if (node.Parent is null)
            Root = left;
        else if (node == node.Parent.Right)
            node.Parent.Right = left;
        else
            node.Parent.Left = left;

        left.Right = node;
        node.Parent = left;

        node.UpdateSubtreeLength();
        left.UpdateSubtreeLength();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PieceNode GetLeftmost(PieceNode node)
    {
        while (node.Left is not null)
            node = node.Left;
        return node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PieceNode GetRightmost(PieceNode node)
    {
        while (node.Right is not null)
            node = node.Right;
        return node;
    }

    private static void FixSubtreeLengthsUp(PieceNode? node)
    {
        while (node is not null) {
            node.UpdateSubtreeLength();
            node = node.Parent;
        }
    }
}
