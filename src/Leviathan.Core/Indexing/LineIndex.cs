using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Leviathan.Core.Indexing;

/// <summary>
/// Background indexer that scans a memory-mapped file for newline (0x0A) positions
/// using SIMD/Hardware Intrinsics at GB/s throughput.
///
/// Stores every Nth line break in a sparse array for O(1) scrollbar positioning.
/// </summary>
public sealed class LineIndex
{
  private readonly long[] _sparseOffsets; // offset of every Nth newline
  private long _totalLineCount;
  private volatile bool _isComplete;
  private readonly int _sparseFactor;

  /// <summary>Total number of hard line breaks found so far.</summary>
  public long TotalLineCount => Volatile.Read(ref _totalLineCount);

  /// <summary>True when the background scan has finished.</summary>
  public bool IsComplete => _isComplete;

  /// <summary>Number of sparse index entries available.</summary>
  public int SparseEntryCount { get; private set; }

  /// <summary>The sparse factor (number of newlines between stored entries).</summary>
  public int SparseFactor => _sparseFactor;

  /// <summary>
  /// Constructs a line index. The sparse array stores every <paramref name="sparseFactor"/>th newline.
  /// </summary>
  public LineIndex(int sparseFactor = 1000, int initialCapacity = 16384)
  {
    _sparseFactor = sparseFactor;
    _sparseOffsets = new long[initialCapacity];
  }

  /// <summary>
  /// Returns the byte offset of the Nth sparse entry (i.e., line <c>index * sparseFactor</c>).
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public long GetSparseOffset(int sparseIndex)
  {
    if ((uint)sparseIndex >= (uint)SparseEntryCount)
      throw new ArgumentOutOfRangeException(nameof(sparseIndex));

    return _sparseOffsets[sparseIndex];
  }

  /// <summary>
  /// Estimates the line number for a given byte offset using the sparse index.
  /// Returns an approximate line number suitable for scrollbar positioning.
  /// </summary>
  public long EstimateLineForOffset(long byteOffset, long fileLength)
  {
    if (fileLength == 0 || _totalLineCount == 0) return 0;

    // Binary search the sparse array for the closest entry
    int lo = 0, hi = SparseEntryCount - 1;
    while (lo <= hi) {
      int mid = lo + (hi - lo) / 2;
      if (_sparseOffsets[mid] <= byteOffset)
        lo = mid + 1;
      else
        hi = mid - 1;
    }

    // hi is the index of the last sparse entry <= byteOffset
    long baseLine = hi >= 0 ? (long)(hi + 1) * _sparseFactor : 0;
    return Math.Min(baseLine, _totalLineCount);
  }

  /// <summary>
  /// Scans raw bytes for newline characters using the best available SIMD path.
  /// Designed to run on a background thread over a memory-mapped file.
  /// </summary>
  public unsafe void ScanChunk(byte* data, long length, long baseOffset, CancellationToken ct)
  {
    long linesSoFar = Volatile.Read(ref _totalLineCount);
    long pos = 0;

    // Use Vector256 path when available (AVX2)
    if (Vector256.IsHardwareAccelerated && length >= 32) {
      var needle = Vector256.Create((byte)0x0A);
      long vectorEnd = length - 32;

      while (pos <= vectorEnd) {
        ct.ThrowIfCancellationRequested();

        var chunk = Vector256.Load(data + pos);
        var cmp = Vector256.Equals(chunk, needle);
        uint mask = cmp.ExtractMostSignificantBits();

        while (mask != 0) {
          int bit = BitOperations.TrailingZeroCount(mask);
          linesSoFar++;

          if (linesSoFar % _sparseFactor == 0) {
            int idx = (int)(linesSoFar / _sparseFactor) - 1;
            if (idx < _sparseOffsets.Length) {
              _sparseOffsets[idx] = baseOffset + pos + bit;
              SparseEntryCount = Math.Max(SparseEntryCount, idx + 1);
            }
          }

          mask &= mask - 1; // clear lowest set bit
        }

        pos += 32;
      }
    }
    // Fallback to Vector128 (SSE2 / NEON)
    else if (Vector128.IsHardwareAccelerated && length >= 16) {
      var needle = Vector128.Create((byte)0x0A);
      long vectorEnd = length - 16;

      while (pos <= vectorEnd) {
        ct.ThrowIfCancellationRequested();

        var chunk = Vector128.Load(data + pos);
        var cmp = Vector128.Equals(chunk, needle);
        uint mask = cmp.ExtractMostSignificantBits();

        while (mask != 0) {
          int bit = BitOperations.TrailingZeroCount(mask);
          linesSoFar++;

          if (linesSoFar % _sparseFactor == 0) {
            int idx = (int)(linesSoFar / _sparseFactor) - 1;
            if (idx < _sparseOffsets.Length) {
              _sparseOffsets[idx] = baseOffset + pos + bit;
              SparseEntryCount = Math.Max(SparseEntryCount, idx + 1);
            }
          }

          mask &= mask - 1;
        }

        pos += 16;
      }
    }

    // Scalar tail
    for (; pos < length; pos++) {
      if (data[pos] == 0x0A) {
        linesSoFar++;

        if (linesSoFar % _sparseFactor == 0) {
          int idx = (int)(linesSoFar / _sparseFactor) - 1;
          if (idx < _sparseOffsets.Length) {
            _sparseOffsets[idx] = baseOffset + pos;
            SparseEntryCount = Math.Max(SparseEntryCount, idx + 1);
          }
        }
      }
    }

    Volatile.Write(ref _totalLineCount, linesSoFar);
  }

  /// <summary>
  /// Marks the index as complete (called by the background scan task).
  /// </summary>
  public void MarkComplete() => _isComplete = true;
}
