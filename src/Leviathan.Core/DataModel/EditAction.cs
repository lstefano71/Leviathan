namespace Leviathan.Core.DataModel;

/// <summary>
/// Base type for all undoable edit operations.
/// Each subclass captures enough state to reverse the operation.
/// </summary>
public abstract class EditAction
{
    /// <summary>Logical document offset where the edit occurred.</summary>
    public long Offset { get; }

    /// <summary>Total byte count of data held by this action (for memory budget tracking).</summary>
    public abstract long DataBytes { get; }

    protected EditAction(long offset)
    {
        Offset = offset;
    }
}

/// <summary>
/// Records an insertion. Stores enough data to undo (delete the range) and
/// to survive an AppendBuffer reset after save.
/// </summary>
public sealed class InsertAction : EditAction
{
    /// <summary>Number of bytes that were inserted.</summary>
    public int Length { get; }

    /// <summary>Offset into the AppendBuffer where the inserted bytes live.</summary>
    public int AppendBufferOffset { get; }

    /// <summary>
    /// Self-contained copy of the inserted bytes, populated by
    /// <see cref="UndoManager.MigrateForSave"/> so the action survives
    /// an AppendBuffer reset.  Null until migration.
    /// </summary>
    public byte[]? MigratedData { get; internal set; }

    public override long DataBytes => MigratedData?.Length ?? 0;

    public InsertAction(long offset, int length, int appendBufferOffset)
        : base(offset)
    {
        Length = length;
        AppendBufferOffset = appendBufferOffset;
    }
}

/// <summary>
/// Records a deletion. The deleted bytes are captured at the time of the
/// delete so they can be re-inserted on undo.
/// </summary>
public sealed class DeleteAction : EditAction
{
    /// <summary>The bytes that were removed from the document.</summary>
    public byte[] DeletedData { get; }

    public override long DataBytes => DeletedData.Length;

    public DeleteAction(long offset, byte[] deletedData)
        : base(offset)
    {
        DeletedData = deletedData;
    }
}

/// <summary>
/// Groups multiple edit actions into a single undoable step.
/// Used for compound operations such as Replace (Delete + Insert)
/// or Paste over a selection (Delete selection + Insert clipboard).
/// Children are undone in reverse order and redone in forward order.
/// </summary>
public sealed class CompoundAction : EditAction
{
    /// <summary>The child actions in execution order.</summary>
    public EditAction[] Children { get; }

    public override long DataBytes
    {
        get
        {
            long total = 0;
            for (int i = 0; i < Children.Length; i++)
                total += Children[i].DataBytes;
            return total;
        }
    }

    public CompoundAction(EditAction[] children)
        : base(children.Length > 0 ? children[0].Offset : 0)
    {
        Children = children;
    }
}
