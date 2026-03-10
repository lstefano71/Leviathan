using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace Leviathan.Core.IO;

/// <summary>
/// Wraps a memory-mapped file providing zero-copy read access to arbitrarily large files.
/// The OS manages paging — only accessed regions are loaded into physical RAM.
/// </summary>
public sealed class MappedFileSource : IDisposable
{
  private readonly MemoryMappedFile _mmf;
  private readonly MemoryMappedViewAccessor _accessor;
  private readonly unsafe byte* _pointer;
  private bool _disposed;

  public long Length { get; }
  public string FilePath { get; }

  public unsafe MappedFileSource(string filePath)
  {
    ArgumentException.ThrowIfNullOrEmpty(filePath);
    filePath = Path.GetFullPath(filePath);

    if (!File.Exists(filePath))
      throw new FileNotFoundException("File not found.", filePath);

    FilePath = filePath;
    var fileInfo = new FileInfo(filePath);
    Length = fileInfo.Length;

    if (Length == 0) {
      // Empty file — no mapping needed, keep pointer null.
      _mmf = null!;
      _accessor = null!;
      _pointer = null;
      return;
    }

    _mmf = MemoryMappedFile.CreateFromFile(
        filePath,
        FileMode.Open,
        mapName: null,
        capacity: 0,
        MemoryMappedFileAccess.Read);

    _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

    byte* ptr = null;
    _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
    _pointer = ptr + _accessor.PointerOffset;
  }

  /// <summary>
  /// Returns a span over the requested region of the file.
  /// Callers must not hold the span across Dispose.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public unsafe ReadOnlySpan<byte> GetSpan(long offset, int length)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (Length == 0 || length == 0)
      return ReadOnlySpan<byte>.Empty;

    if ((ulong)offset + (ulong)length > (ulong)Length)
      throw new ArgumentOutOfRangeException(nameof(offset));

    return new ReadOnlySpan<byte>(_pointer + offset, length);
  }

  /// <summary>
  /// Reads a single byte at the given offset.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public unsafe byte ReadByte(long offset)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);

    if ((ulong)offset >= (ulong)Length)
      throw new ArgumentOutOfRangeException(nameof(offset));

    return _pointer[offset];
  }

  public unsafe void Dispose()
  {
    if (_disposed) return;
    _disposed = true;

    if (_pointer != null)
      _accessor.SafeMemoryMappedViewHandle.ReleasePointer();

    _accessor?.Dispose();
    _mmf?.Dispose();
  }
}
