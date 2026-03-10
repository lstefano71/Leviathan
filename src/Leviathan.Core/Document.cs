using Leviathan.Core.DataModel;
using Leviathan.Core.IO;

namespace Leviathan.Core;

/// <summary>
/// Represents a single open document backed by a memory-mapped file and a piece table.
/// This is the primary API surface for reading and editing file content.
/// </summary>
public sealed class Document : IDisposable
{
  private readonly MappedFileSource? _fileSource;
  private readonly AppendBuffer _appendBuffer;
  private readonly PieceTree _tree;
  private bool _disposed;

  /// <summary>
  /// Logical length of the document including all edits.
  /// </summary>
  public long Length => _tree.TotalLength;

  /// <summary>
  /// Path to the underlying file, if any.
  /// </summary>
  public string? FilePath => _fileSource?.FilePath;

  /// <summary>
  /// Opens an existing file for viewing/editing.
  /// </summary>
  public Document(string filePath)
  {
    _fileSource = new MappedFileSource(filePath);
    _appendBuffer = new AppendBuffer();
    _tree = new PieceTree();

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
  /// </summary>
  public void Insert(long offset, ReadOnlySpan<byte> data)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (data.IsEmpty) return;

    int appendOffset = _appendBuffer.Append(data);
    var piece = new Piece(PieceSource.Append, appendOffset, data.Length);
    _tree.Insert(offset, piece);
  }

  /// <summary>
  /// Deletes a range of bytes from the logical document.
  /// </summary>
  public void Delete(long offset, long length)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    _tree.Delete(offset, length);
  }

  /// <summary>
  /// Saves all edits atomically: streams pieces to a temp file, then swaps.
  /// </summary>
  public void SaveTo(string destinationPath)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);

    destinationPath = Path.GetFullPath(destinationPath);
    string tempPath = destinationPath + ".tmp";

    try {
      using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024)) {
        Span<byte> copyBuffer = stackalloc byte[0]; // will use heap for large chunks
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

      // Atomic swap
      File.Move(tempPath, destinationPath, overwrite: true);
    } catch {
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
