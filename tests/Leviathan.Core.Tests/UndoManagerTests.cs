using Leviathan.Core.DataModel;
using Leviathan.Core.IO;

namespace Leviathan.Core.Tests;

public class UndoManagerTests
{
    private static (UndoManager Manager, AppendBuffer Buffer) CreateManager()
    {
        AppendBuffer buffer = new();
        UndoManager manager = new(buffer);
        return (manager, buffer);
    }

    private static Document CreateDocWithContent(byte[] content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return new Document(path);
    }

    // ─── Basic push / state ────────────────────────────────────────────

    [Fact]
    public void Initial_State_CannotUndoOrRedo()
    {
        var (mgr, _) = CreateManager();
        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
        Assert.Equal(0, mgr.UndoCount);
        Assert.Equal(0, mgr.RedoCount);
    }

    [Fact]
    public void Push_MakesCanUndoTrue()
    {
        var (mgr, _) = CreateManager();
        var action = new DeleteAction(0, [0x41]);
        mgr.Push(action, 0, 0);

        Assert.True(mgr.CanUndo);
        Assert.Equal(1, mgr.UndoCount);
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        using Document doc = new();
        doc.Insert(0, [0x41, 0x42, 0x43]);
        doc.Undo();
        Assert.True(doc.CanRedo);

        doc.Insert(0, [0xFF]);
        Assert.False(doc.CanRedo);
    }

    // ─── Undo / Redo round-trip via Document ───────────────────────────

    [Fact]
    public void Undo_AfterInsert_RemovesInsertedBytes()
    {
        using Document doc = new();
        doc.Insert(0, [0x41, 0x42, 0x43]); // "ABC"

        Assert.Equal(3, doc.Length);

        long? cursor = doc.Undo();

        Assert.Equal(0, doc.Length);
        Assert.NotNull(cursor);
        Assert.Equal(0L, cursor!.Value);
    }

    [Fact]
    public void Redo_AfterUndoInsert_ReInsertsBytes()
    {
        using Document doc = new();
        doc.Insert(0, [0x41, 0x42, 0x43]);
        doc.Undo();
        Assert.Equal(0, doc.Length);

        long? cursor = doc.Redo();

        Assert.Equal(3, doc.Length);
        Span<byte> buf = stackalloc byte[3];
        doc.Read(0, buf);
        Assert.Equal(0x41, buf[0]);
        Assert.Equal(0x42, buf[1]);
        Assert.Equal(0x43, buf[2]);
        Assert.Equal(3L, cursor!.Value);
    }

    [Fact]
    public void Undo_AfterDelete_ReInsertsDeletedBytes()
    {
        string path = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [0x41, 0x42, 0x43, 0x44, 0x45]);
            using Document doc = new(path);
            doc.Delete(1, 3); // delete "BCD"

            Assert.Equal(2, doc.Length);

            long? cursor = doc.Undo();

            Assert.Equal(5, doc.Length);
            Span<byte> buf = stackalloc byte[5];
            doc.Read(0, buf);
            Assert.True(buf.SequenceEqual("ABCDE"u8));
            Assert.Equal(1L, cursor!.Value);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redo_AfterUndoDelete_ReDeletesBytes()
    {
        string path = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [0x41, 0x42, 0x43, 0x44, 0x45]);
            using Document doc = new(path);
            doc.Delete(1, 3);
            doc.Undo();
            Assert.Equal(5, doc.Length);

            long? cursor = doc.Redo();

            Assert.Equal(2, doc.Length);
            Span<byte> buf = stackalloc byte[2];
            doc.Read(0, buf);
            Assert.Equal(0x41, buf[0]);
            Assert.Equal(0x45, buf[1]);
            Assert.Equal(1L, cursor!.Value);
        } finally {
            File.Delete(path);
        }
    }

    // ─── Multiple undo/redo steps ──────────────────────────────────────

    [Fact]
    public void MultipleUndoRedo_RestoresCorrectly()
    {
        using Document doc = new();
        doc.BreakCoalescing();
        doc.Insert(0, [0x41]); // 'A'
        doc.BreakCoalescing();
        doc.Insert(1, [0x42]); // 'B'
        doc.BreakCoalescing();
        doc.Insert(2, [0x43]); // 'C'

        Assert.Equal(3, doc.Length);

        doc.Undo(); // remove 'C'
        Assert.Equal(2, doc.Length);

        doc.Undo(); // remove 'B'
        Assert.Equal(1, doc.Length);

        doc.Redo(); // re-insert 'B'
        Assert.Equal(2, doc.Length);

        Span<byte> buf = stackalloc byte[2];
        doc.Read(0, buf);
        Assert.Equal(0x41, buf[0]);
        Assert.Equal(0x42, buf[1]);
    }

    [Fact]
    public void Undo_EmptyStack_ReturnsNull()
    {
        using Document doc = new();
        Assert.Null(doc.Undo());
    }

    [Fact]
    public void Redo_EmptyStack_ReturnsNull()
    {
        using Document doc = new();
        Assert.Null(doc.Redo());
    }

    // ─── Coalescing ────────────────────────────────────────────────────

    [Fact]
    public void Coalescing_ConsecutiveInserts_MergedIntoOne()
    {
        using Document doc = new();
        doc.Insert(0, [0x41]); // 'A'
        doc.Insert(1, [0x42]); // 'B'
        doc.Insert(2, [0x43]); // 'C'

        // All three should be coalesced into 1 undo entry.
        Assert.True(doc.CanUndo);
        doc.Undo();
        Assert.Equal(0, doc.Length);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void Coalescing_BrokenByCursorJump_CreatesSeparateEntries()
    {
        using Document doc = new();
        doc.Insert(0, [0x41]); // 'A' at 0
        doc.BreakCoalescing();
        doc.Insert(1, [0x42]); // 'B' at 1 (non-adjacent from coalescing perspective after break)

        // Should be 2 separate entries because coalescing was broken.
        doc.Undo(); // undo 'B'
        Assert.Equal(1, doc.Length);
        doc.Undo(); // undo 'A'
        Assert.Equal(0, doc.Length);
    }

    [Fact]
    public void Coalescing_BackspaceDeletes_MergedIntoOne()
    {
        string path = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [0x41, 0x42, 0x43, 0x44]);
            using Document doc = new(path);

            // Backspace from offset 3: delete at 2, then at 1
            doc.Delete(2, 1, 3); // delete 'C', cursor was at 3
            doc.Delete(1, 1, 2); // delete 'B', cursor was at 2

            // Should coalesce into one undo step.
            doc.Undo();
            Assert.Equal(4, doc.Length);
            Assert.False(doc.CanUndo);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Coalescing_ForwardDeletes_MergedIntoOne()
    {
        string path = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [0x41, 0x42, 0x43, 0x44]);
            using Document doc = new(path);

            // Forward delete at offset 1: both deletes at same offset
            doc.Delete(1, 1, 1); // delete 'B'
            doc.Delete(1, 1, 1); // delete 'C'

            // Should coalesce into one undo step.
            doc.Undo();
            Assert.Equal(4, doc.Length);
            Assert.False(doc.CanUndo);
        } finally {
            File.Delete(path);
        }
    }

    // ─── Grouping ──────────────────────────────────────────────────────

    [Fact]
    public void Group_CompoundOperation_UndoesAsOne()
    {
        string path = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [0x41, 0x42, 0x43]);
            using Document doc = new(path);

            // Replace byte at offset 1: delete + insert as group.
            doc.BeginUndoGroup(1);
            doc.Delete(1, 1);
            doc.Insert(1, [0xFF]);
            doc.EndUndoGroup(1);

            // Single undo should reverse both.
            doc.Undo();
            Assert.Equal(3, doc.Length);
            Span<byte> buf = stackalloc byte[3];
            doc.Read(0, buf);
            Assert.True(buf.SequenceEqual("ABC"u8));
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Group_EmptyGroup_DoesNotPush()
    {
        var (mgr, _) = CreateManager();
        mgr.BeginGroup(0);
        mgr.EndGroup(0);

        Assert.False(mgr.CanUndo);
    }

    [Fact]
    public void Group_SingleAction_NoCompoundWrapper()
    {
        var (mgr, _) = CreateManager();
        mgr.BeginGroup(0);
        mgr.Push(new DeleteAction(0, [0x41]), 0, 0);
        mgr.EndGroup(0);

        Assert.Equal(1, mgr.UndoCount);
    }

    // ─── Memory budget ─────────────────────────────────────────────────

    [Fact]
    public void MemoryBudget_EvictsOldestWhenExceeded()
    {
        var (mgr, _) = CreateManager();
        mgr.MaxDataBytes = 10; // tiny budget

        // Push 15 bytes of delete data across 3 actions.
        mgr.Push(new DeleteAction(0, new byte[5]), 0, 0);
        mgr.BreakCoalescing();
        mgr.Push(new DeleteAction(5, new byte[5]), 5, 5);
        mgr.BreakCoalescing();
        mgr.Push(new DeleteAction(10, new byte[5]), 10, 10);

        // Oldest entry should have been evicted.
        Assert.True(mgr.UndoCount < 3);
    }

    [Fact]
    public void MemoryBudget_ZeroMeansUnlimited()
    {
        var (mgr, _) = CreateManager();
        mgr.MaxDataBytes = 0;

        for (int i = 0; i < 100; i++) {
            mgr.Push(new DeleteAction(i, new byte[1000]), i, i);
            mgr.BreakCoalescing();
        }

        Assert.Equal(100, mgr.UndoCount);
    }

    // ─── Save migration ────────────────────────────────────────────────

    [Fact]
    public void MigrateForSave_PreservesUndoAcrossSave()
    {
        string path = Path.GetTempFileName();
        string savePath = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [0x41, 0x42, 0x43]);
            using Document doc = new(path);

            doc.Insert(1, [0xFF]);
            Assert.Equal(4, doc.Length);

            doc.SaveTo(savePath);

            // After save, undo should still work.
            long? cursor = doc.Undo();
            Assert.NotNull(cursor);
            Assert.Equal(3, doc.Length);
        } finally {
            File.Delete(path);
            File.Delete(savePath);
        }
    }

    [Fact]
    public void MigrateForSave_RedoAfterSave_StillWorks()
    {
        string path = Path.GetTempFileName();
        string savePath = Path.GetTempFileName();
        try {
            File.WriteAllBytes(path, [0x41, 0x42, 0x43]);
            using Document doc = new(path);

            doc.Insert(1, [0xFF]);
            doc.Undo();

            doc.SaveTo(savePath);

            // Redo after save should still work.
            long? cursor = doc.Redo();
            Assert.NotNull(cursor);
            Assert.Equal(4, doc.Length);
        } finally {
            File.Delete(path);
            File.Delete(savePath);
        }
    }

    // ─── Clear ─────────────────────────────────────────────────────────

    [Fact]
    public void Clear_ResetsAllState()
    {
        var (mgr, _) = CreateManager();
        mgr.Push(new DeleteAction(0, [0x41]), 0, 0);
        mgr.Clear();

        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
        Assert.Equal(0, mgr.UndoCount);
    }

    // ─── CanUndo / CanRedo transitions ─────────────────────────────────

    [Fact]
    public void CanUndo_CanRedo_TransitionsCorrectly()
    {
        using Document doc = new();

        Assert.False(doc.CanUndo);
        Assert.False(doc.CanRedo);

        doc.Insert(0, [0x41]);
        Assert.True(doc.CanUndo);
        Assert.False(doc.CanRedo);

        doc.Undo();
        Assert.False(doc.CanUndo);
        Assert.True(doc.CanRedo);

        doc.Redo();
        Assert.True(doc.CanUndo);
        Assert.False(doc.CanRedo);
    }
}
