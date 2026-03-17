using Leviathan.Core.DataModel;
using Leviathan.Core.IO;

namespace Leviathan.Core;

/// <summary>
/// Represents one undoable step: the edit action plus cursor positions
/// before and after the edit, so the UI can restore the caret on undo/redo.
/// </summary>
public sealed class UndoEntry
{
    public EditAction Action { get; }
    public long CursorOffsetBefore { get; }
    public long CursorOffsetAfter { get; internal set; }

    public long DataBytes => Action.DataBytes;

    public UndoEntry(EditAction action, long cursorOffsetBefore, long cursorOffsetAfter)
    {
        Action = action;
        CursorOffsetBefore = cursorOffsetBefore;
        CursorOffsetAfter = cursorOffsetAfter;
    }
}

/// <summary>
/// Manages multi-step undo/redo for a <see cref="Document"/>.
/// Supports compound grouping, typing coalescing, a configurable memory
/// budget, and migration of AppendBuffer references before save.
/// </summary>
public sealed class UndoManager
{
    private readonly AppendBuffer _appendBuffer;
    private readonly List<UndoEntry> _undoStack = new();
    private readonly List<UndoEntry> _redoStack = new();
    private long _totalDataBytes;

    // ─── Grouping ──────────────────────────────────────────────────────
    private int _groupDepth;
    private List<(EditAction Action, long CursorBefore, long CursorAfter)>? _groupActions;
    private long _groupCursorBefore;

    // ─── Coalescing ────────────────────────────────────────────────────
    private bool _coalescingEnabled = true;

    /// <summary>
    /// Maximum total bytes of undo data retained.  When exceeded the oldest
    /// entries are evicted.  Default 256 MB.  Set to 0 to disable the cap.
    /// </summary>
    public long MaxDataBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Whether there are any operations that can be undone.</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>Whether there are any operations that can be redone.</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Number of entries on the undo stack.</summary>
    public int UndoCount => _undoStack.Count;

    /// <summary>Number of entries on the redo stack.</summary>
    public int RedoCount => _redoStack.Count;

    public UndoManager(AppendBuffer appendBuffer)
    {
        _appendBuffer = appendBuffer;
    }

    // ─── Push ──────────────────────────────────────────────────────────

    /// <summary>
    /// Records a new edit action.  Clears the redo stack and enforces the
    /// memory budget by evicting the oldest entries when necessary.
    /// </summary>
    public void Push(EditAction action, long cursorBefore, long cursorAfter)
    {
        if (_groupDepth > 0) {
            _groupActions!.Add((action, cursorBefore, cursorAfter));
            return;
        }

        // Try coalescing with the top of the undo stack.
        if (_coalescingEnabled && _redoStack.Count == 0 && TryCoalesce(action, cursorAfter))
            return;

        // New edit clears redo and re-enables coalescing.
        ClearRedoStack();
        _coalescingEnabled = true;

        var entry = new UndoEntry(action, cursorBefore, cursorAfter);
        _undoStack.Add(entry);
        _totalDataBytes += entry.DataBytes;
        EnforceMemoryBudget();
    }

    // ─── Coalescing ────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to merge <paramref name="action"/> into the top undo entry.
    /// Returns true if coalesced (no new entry pushed).
    /// </summary>
    private bool TryCoalesce(EditAction action, long cursorAfter)
    {
        if (_undoStack.Count == 0)
            return false;

        UndoEntry top = _undoStack[^1];

        // Coalesce single-byte inserts at adjacent offsets (typing).
        if (action is InsertAction ins && ins.Length == 1
            && top.Action is InsertAction topIns
            && ins.Offset == topIns.Offset + topIns.Length) {

            // Build merged MigratedData containing all bytes.
            byte[] topBytes = topIns.MigratedData
                ?? ReadFromAppendBuffer(topIns.AppendBufferOffset, topIns.Length);
            byte[] newByte = ReadFromAppendBuffer(ins.AppendBufferOffset, 1);

            byte[] merged = new byte[topBytes.Length + 1];
            topBytes.CopyTo(merged, 0);
            merged[^1] = newByte[0];

            var mergedAction = new InsertAction(topIns.Offset, merged.Length, topIns.AppendBufferOffset);
            mergedAction.MigratedData = merged;

            _totalDataBytes -= top.DataBytes;
            _undoStack[^1] = new UndoEntry(mergedAction, top.CursorOffsetBefore, cursorAfter);
            _totalDataBytes += mergedAction.DataBytes;
            return true;
        }

        // Coalesce single-byte backspace deletes at adjacent offsets.
        if (action is DeleteAction del && del.DeletedData.Length == 1
            && top.Action is DeleteAction topDel) {

            // Backspace: new delete offset == top offset - 1
            if (del.Offset == topDel.Offset - 1) {
                byte[] mergedData = new byte[topDel.DeletedData.Length + 1];
                mergedData[0] = del.DeletedData[0];
                topDel.DeletedData.CopyTo(mergedData, 1);
                var mergedAction = new DeleteAction(del.Offset, mergedData);
                _totalDataBytes -= top.DataBytes;
                _undoStack[^1] = new UndoEntry(mergedAction, top.CursorOffsetBefore, cursorAfter);
                _totalDataBytes += mergedAction.DataBytes;
                return true;
            }

            // Forward delete (Delete key): new delete offset == top offset
            if (del.Offset == topDel.Offset) {
                byte[] mergedData = new byte[topDel.DeletedData.Length + 1];
                topDel.DeletedData.CopyTo(mergedData, 0);
                mergedData[^1] = del.DeletedData[0];
                var mergedAction = new DeleteAction(topDel.Offset, mergedData);
                _totalDataBytes -= top.DataBytes;
                _undoStack[^1] = new UndoEntry(mergedAction, top.CursorOffsetBefore, cursorAfter);
                _totalDataBytes += mergedAction.DataBytes;
                return true;
            }
        }

        return false;
    }

    private byte[] ReadFromAppendBuffer(int offset, int length)
    {
        byte[] data = new byte[length];
        _appendBuffer.GetSpan(offset, length).CopyTo(data);
        return data;
    }

    /// <summary>
    /// Breaks coalescing so the next push starts a new undo entry even if
    /// it would otherwise merge with the previous one.
    /// Call after cursor jumps, pauses, or when switching operation types.
    /// </summary>
    public void BreakCoalescing()
    {
        _coalescingEnabled = false;
    }

    // ─── Undo / Redo ───────────────────────────────────────────────────

    /// <summary>
    /// Undoes the most recent edit.  Applies the inverse operation to the
    /// document and moves the entry to the redo stack.
    /// Returns the cursor offset to restore, or null if the stack was empty.
    /// </summary>
    public long? Undo(Document document)
    {
        if (_undoStack.Count == 0)
            return null;

        _coalescingEnabled = false;

        UndoEntry entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        ApplyInverse(document, entry.Action);

        _redoStack.Add(entry);
        return entry.CursorOffsetBefore;
    }

    /// <summary>
    /// Redoes the most recently undone edit.
    /// Returns the cursor offset to restore, or null if the stack was empty.
    /// </summary>
    public long? Redo(Document document)
    {
        if (_redoStack.Count == 0)
            return null;

        _coalescingEnabled = false;

        UndoEntry entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        ApplyForward(document, entry.Action);

        _undoStack.Add(entry);
        return entry.CursorOffsetAfter;
    }

    // ─── Apply helpers ─────────────────────────────────────────────────

    private void ApplyInverse(Document document, EditAction action)
    {
        switch (action) {
            case CompoundAction compound:
                for (int i = compound.Children.Length - 1; i >= 0; i--)
                    ApplyInverse(document, compound.Children[i]);
                break;

            case InsertAction insert:
                document.DeleteInternal(insert.Offset, insert.Length);
                break;

            case DeleteAction delete:
                document.InsertInternal(delete.Offset, delete.DeletedData);
                break;
        }
    }

    private void ApplyForward(Document document, EditAction action)
    {
        switch (action) {
            case CompoundAction compound:
                for (int i = 0; i < compound.Children.Length; i++)
                    ApplyForward(document, compound.Children[i]);
                break;

            case InsertAction insert:
                byte[] insertData = insert.MigratedData
                    ?? ReadFromAppendBuffer(insert.AppendBufferOffset, insert.Length);
                document.InsertInternal(insert.Offset, insertData);
                break;

            case DeleteAction delete:
                document.DeleteInternal(delete.Offset, delete.DeletedData.Length);
                break;
        }
    }

    // ─── Grouping ──────────────────────────────────────────────────────

    /// <summary>
    /// Begins an undo group.  All subsequent edits until <see cref="EndGroup"/>
    /// are recorded as children of a single <see cref="CompoundAction"/>.
    /// Groups can be nested; only the outermost group produces an entry.
    /// </summary>
    public void BeginGroup(long cursorBefore)
    {
        if (_groupDepth == 0) {
            _groupActions = new();
            _groupCursorBefore = cursorBefore;
        }
        _groupDepth++;
    }

    /// <summary>
    /// Ends an undo group.  If this closes the outermost group, a
    /// <see cref="CompoundAction"/> is pushed onto the undo stack.
    /// </summary>
    public void EndGroup(long cursorAfter)
    {
        if (_groupDepth <= 0)
            return;

        _groupDepth--;
        if (_groupDepth > 0)
            return;

        List<(EditAction Action, long CursorBefore, long CursorAfter)> actions = _groupActions!;
        _groupActions = null;

        if (actions.Count == 0)
            return;

        if (actions.Count == 1) {
            ClearRedoStack();
            _coalescingEnabled = true;
            var entry = new UndoEntry(actions[0].Action, _groupCursorBefore, cursorAfter);
            _undoStack.Add(entry);
            _totalDataBytes += entry.DataBytes;
            EnforceMemoryBudget();
            return;
        }

        EditAction[] children = new EditAction[actions.Count];
        for (int i = 0; i < actions.Count; i++)
            children[i] = actions[i].Action;

        var compound = new CompoundAction(children);
        ClearRedoStack();
        _coalescingEnabled = true;
        var compoundEntry = new UndoEntry(compound, _groupCursorBefore, cursorAfter);
        _undoStack.Add(compoundEntry);
        _totalDataBytes += compoundEntry.DataBytes;
        EnforceMemoryBudget();
    }

    /// <summary>Whether a group is currently being recorded.</summary>
    public bool IsGroupActive => _groupDepth > 0;

    // ─── Save migration ────────────────────────────────────────────────

    /// <summary>
    /// Copies bytes referenced by <see cref="InsertAction.AppendBufferOffset"/>
    /// into <see cref="InsertAction.MigratedData"/> so the actions survive an
    /// <see cref="AppendBuffer.Reset"/>.  Call this immediately before saving.
    /// </summary>
    public void MigrateForSave()
    {
        MigrateStack(_undoStack);
        MigrateStack(_redoStack);
    }

    private void MigrateStack(List<UndoEntry> stack)
    {
        for (int i = 0; i < stack.Count; i++)
            MigrateAction(stack[i].Action);
    }

    private void MigrateAction(EditAction action)
    {
        switch (action) {
            case CompoundAction compound:
                for (int i = 0; i < compound.Children.Length; i++)
                    MigrateAction(compound.Children[i]);
                break;

            case InsertAction insert:
                if (insert.MigratedData is null) {
                    insert.MigratedData = ReadFromAppendBuffer(insert.AppendBufferOffset, insert.Length);
                }
                break;
        }
    }

    // ─── Clear / Budget ────────────────────────────────────────────────

    /// <summary>Clears all undo and redo history.</summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _totalDataBytes = 0;
        _groupDepth = 0;
        _groupActions = null;
    }

    private void ClearRedoStack()
    {
        if (_redoStack.Count == 0)
            return;

        for (int i = 0; i < _redoStack.Count; i++)
            _totalDataBytes -= _redoStack[i].DataBytes;
        _redoStack.Clear();
    }

    private void EnforceMemoryBudget()
    {
        if (MaxDataBytes <= 0)
            return;

        while (_totalDataBytes > MaxDataBytes && _undoStack.Count > 1) {
            UndoEntry oldest = _undoStack[0];
            _totalDataBytes -= oldest.DataBytes;
            _undoStack.RemoveAt(0);
        }
    }
}
