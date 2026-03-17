using Leviathan.Core.DataModel;
using Leviathan.Core.IO;

namespace Leviathan.Core;

/// <summary>
/// Represents a single open document backed by a memory-mapped file and a piece table.
/// This is the primary API surface for reading and editing file content.
/// </summary>
public sealed class Document : IDisposable
{
    private MappedFileSource? _fileSource;
    private readonly AppendBuffer _appendBuffer;
    private readonly PieceTree _tree;
    private readonly UndoManager _undoManager;
    private bool _disposed;
    private bool _isModified;

    /// <summary>
    /// Indicates whether the document has been modified since it was opened or last saved.
    /// </summary>
    public bool IsModified => _isModified;

    /// <summary>
    /// Logical length of the document including all edits.
    /// </summary>
    public long Length => _tree.TotalLength;

    /// <summary>
    /// Path to the underlying file, if any.
    /// </summary>
    public string? FilePath => _fileSource?.FilePath;

    /// <summary>The underlying memory-mapped file source, or null for new documents.</summary>
    internal IO.MappedFileSource? FileSource => _fileSource;

    /// <summary>Whether there are undoable edits.</summary>
    public bool CanUndo => _undoManager.CanUndo;

    /// <summary>Whether there are redoable edits.</summary>
    public bool CanRedo => _undoManager.CanRedo;

    /// <summary>
    /// Opens an existing file for viewing/editing.
    /// </summary>
    public Document(string filePath)
    {
        _fileSource = new MappedFileSource(filePath);
        _appendBuffer = new AppendBuffer();
        _tree = new PieceTree();
        _undoManager = new UndoManager(_appendBuffer);

        if (_fileSource.Length > 0) {
            _tree.Init(new Piece(PieceSource.Original, 0, _fileSource.Length));
        }
    }

    /// <summary>
    /// Creates an empty document (e.g., for "New File").
    /// </summary>
    public Document()
    {
        _fileSource = null;
        _appendBuffer = new AppendBuffer();
        _tree = new PieceTree();
        _undoManager = new UndoManager(_appendBuffer);
    }

    /// <summary>
    /// Reads bytes from the logical document into the buffer.
    /// Returns the number of bytes actually read.
    /// </summary>
    public int Read(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _tree.Read(offset, buffer, ResolveSpan);
    }

    /// <summary>
    /// Inserts raw bytes at the given logical offset.
    /// Records an undo entry so the operation can be reversed with <see cref="Undo"/>.
    /// </summary>
    public void Insert(long offset, ReadOnlySpan<byte> data)
    {
        Insert(offset, data, offset);
    }

    /// <summary>
    /// Inserts raw bytes at the given logical offset, recording the cursor
    /// position before the edit for undo restoration.
    /// </summary>
    public void Insert(long offset, ReadOnlySpan<byte> data, long cursorBefore)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data.IsEmpty) return;

        int appendOffset = _appendBuffer.Append(data);
        var piece = new Piece(PieceSource.Append, appendOffset, data.Length);
        _tree.Insert(offset, piece);
        _isModified = true;

        var action = new InsertAction(offset, data.Length, appendOffset);
        _undoManager.Push(action, cursorBefore, offset + data.Length);
    }

    /// <summary>
    /// Deletes a range of bytes from the logical document.
    /// Captures the deleted bytes for undo and records an undo entry.
    /// </summary>
    public void Delete(long offset, long length)
    {
        Delete(offset, length, offset);
    }

    /// <summary>
    /// Deletes a range of bytes from the logical document, recording the
    /// cursor position before the edit for undo restoration.
    /// </summary>
    public void Delete(long offset, long length, long cursorBefore)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (length <= 0) return;

        // Capture the bytes being deleted for undo.
        byte[] deletedData = new byte[length];
        _tree.Read(offset, deletedData, ResolveSpan);

        _tree.Delete(offset, length);
        _isModified = true;

        var action = new DeleteAction(offset, deletedData);
        _undoManager.Push(action, cursorBefore, offset);
    }

    // ─── Undo / Redo ───────────────────────────────────────────────────

    /// <summary>
    /// Undoes the most recent edit.
    /// Returns the cursor offset to restore, or null if nothing to undo.
    /// </summary>
    public long? Undo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long? cursor = _undoManager.Undo(this);
        if (cursor.HasValue)
            _isModified = true;
        return cursor;
    }

    /// <summary>
    /// Redoes the most recently undone edit.
    /// Returns the cursor offset to restore, or null if nothing to redo.
    /// </summary>
    public long? Redo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long? cursor = _undoManager.Redo(this);
        if (cursor.HasValue)
            _isModified = true;
        return cursor;
    }

    /// <summary>
    /// Begins an undo group.  All edits until <see cref="EndUndoGroup"/> are
    /// treated as a single undoable step.
    /// </summary>
    public void BeginUndoGroup(long cursorBefore)
    {
        _undoManager.BeginGroup(cursorBefore);
    }

    /// <summary>Ends the current undo group.</summary>
    public void EndUndoGroup(long cursorAfter)
    {
        _undoManager.EndGroup(cursorAfter);
    }

    /// <summary>
    /// Breaks coalescing so the next edit starts a new undo entry.
    /// Call after cursor jumps or operation-type changes.
    /// </summary>
    public void BreakCoalescing()
    {
        _undoManager.BreakCoalescing();
    }

    // ─── Internal methods for UndoManager ──────────────────────────────

    /// <summary>
    /// Inserts bytes without recording an undo entry.
    /// Used internally by <see cref="UndoManager"/> during undo/redo.
    /// </summary>
    internal void InsertInternal(long offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        int appendOffset = _appendBuffer.Append(data);
        var piece = new Piece(PieceSource.Append, appendOffset, data.Length);
        _tree.Insert(offset, piece);
        _isModified = true;
    }

    /// <summary>
    /// Deletes bytes without recording an undo entry.
    /// Used internally by <see cref="UndoManager"/> during undo/redo.
    /// </summary>
    internal void DeleteInternal(long offset, long length)
    {
        if (length <= 0) return;
        _tree.Delete(offset, length);
        _isModified = true;
    }

    /// <summary>
    /// Reads bytes from the AppendBuffer.  Used by <see cref="UndoManager"/>
    /// to retrieve insert data for redo operations.
    /// </summary>
    internal byte[] ReadAppendBuffer(int offset, int length)
    {
        byte[] data = new byte[length];
        _appendBuffer.GetSpan(offset, length).CopyTo(data);
        return data;
    }

    /// <summary>
    /// Saves all edits atomically: streams pieces to a temp file, then swaps.
    /// When saving back to the source file, the memory-mapped handle is released
    /// before the swap and reopened afterwards so the OS file lock is not held.
    /// </summary>
    public void SaveTo(string destinationPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        destinationPath = Path.GetFullPath(destinationPath);
        string tempPath = destinationPath + ".tmp";

        bool isSameFile = _fileSource is not null &&
            string.Equals(destinationPath, _fileSource.FilePath, StringComparison.OrdinalIgnoreCase);

        try {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024)) {
                byte[] heapBuffer = new byte[64 * 1024];

                foreach (var piece in _tree.InOrder()) {
                    long remaining = piece.Length;
                    long srcOffset = piece.Offset;

                    while (remaining > 0) {
                        int chunkLen = (int)Math.Min(remaining, heapBuffer.Length);
                        var span = ResolveSpan(piece.Source, srcOffset, chunkLen);
                        span.CopyTo(heapBuffer);
                        fs.Write(heapBuffer, 0, chunkLen);
                        srcOffset += chunkLen;
                        remaining -= chunkLen;
                    }
                }

                fs.Flush(flushToDisk: true);
            }

            // Release the memory-mapped lock before swapping when overwriting the source file.
            if (isSameFile) {
                _fileSource!.Dispose();
                _fileSource = null;
            }

            // Atomic swap
            File.Move(tempPath, destinationPath, overwrite: true);

            // Release the old file source when saving to a different path.
            if (!isSameFile && _fileSource is not null) {
                _fileSource.Dispose();
                _fileSource = null;
            }

            // Migrate undo data before resetting the append buffer.
            _undoManager.MigrateForSave();

            // Reopen the file and rebuild the piece tree so the document reflects the saved state.
            var newSource = new MappedFileSource(destinationPath);
            _fileSource = newSource;
            _appendBuffer.Reset();

            if (newSource.Length > 0) {
                _tree.Init(new Piece(PieceSource.Original, 0, newSource.Length));
            } else {
                _tree.Clear();
            }

            _isModified = false;
        } catch {
            // If the mmap was released but something went wrong, try to reopen it
            // so the document remains usable (best effort).
            if (isSameFile && _fileSource is null && File.Exists(destinationPath)) {
                try { _fileSource = new MappedFileSource(destinationPath); } catch { /* best effort */ }
            }

            // Clean up temp file on failure — original is untouched
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Resolves a piece reference to actual bytes.
    /// </summary>
    private ReadOnlySpan<byte> ResolveSpan(PieceSource source, long offset, int length)
    {
        return source switch {
            PieceSource.Original => _fileSource!.GetSpan(offset, length),
            PieceSource.Append => _appendBuffer.GetSpan((int)offset, length),
            _ => throw new InvalidOperationException("Unknown piece source.")
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fileSource?.Dispose();
        _appendBuffer.Dispose();
    }
}
