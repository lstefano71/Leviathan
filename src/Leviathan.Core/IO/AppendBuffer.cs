using System.Buffers;
using System.Runtime.CompilerServices;

namespace Leviathan.Core.IO;

/// <summary>
/// An arena-style append-only buffer for newly typed bytes.
/// Backed by ArrayPool to minimise GC pressure.
/// </summary>
public sealed class AppendBuffer : IDisposable
{
  private byte[] _buffer;
  private int _position;
  private bool _disposed;

  private const int InitialCapacity = 4 * 1024 * 1024; // 4 MB

  public int Length => _position;

  public AppendBuffer()
  {
    _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    _position = 0;
  }

  /// <summary>
  /// Appends a single byte and returns the offset where it was stored.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public int Append(byte value)
  {
    EnsureCapacity(1);
    int offset = _position;
    _buffer[_position++] = value;
    return offset;
  }

  /// <summary>
  /// Appends a span of bytes and returns the starting offset.
  /// </summary>
  public int Append(ReadOnlySpan<byte> data)
  {
    EnsureCapacity(data.Length);
    int offset = _position;
    data.CopyTo(_buffer.AsSpan(_position));
    _position += data.Length;
    return offset;
  }

  /// <summary>
  /// Returns a read-only span over previously appended bytes.
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public ReadOnlySpan<byte> GetSpan(int offset, int length)
  {
    if ((uint)(offset + length) > (uint)_position)
      throw new ArgumentOutOfRangeException(nameof(offset));

    return _buffer.AsSpan(offset, length);
  }

  private void EnsureCapacity(int additionalBytes)
  {
    if (_position + additionalBytes <= _buffer.Length)
      return;

    int newCapacity = Math.Max(_buffer.Length * 2, _position + additionalBytes);
    byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
    _buffer.AsSpan(0, _position).CopyTo(newBuffer);
    ArrayPool<byte>.Shared.Return(_buffer);
    _buffer = newBuffer;
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    ArrayPool<byte>.Shared.Return(_buffer);
    _buffer = null!;
  }
}
