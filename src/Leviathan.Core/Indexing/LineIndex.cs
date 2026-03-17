using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Leviathan.Core.Indexing;

/// <summary>
/// Background indexer that scans a memory-mapped file for newline (0x0A) positions
/// using SIMD/Hardware Intrinsics at GB/s throughput.
///
/// Stores every Nth line break in a sparse array for O(1) scrollbar positioning.
/// For UTF-16 LE files (<paramref name="charWidth"/> = 2), scans for 0x000A code units
/// at 2-byte-aligned positions to avoid false positives from 0x0A appearing as the
/// high byte of unrelated code units.
/// </summary>
public sealed class LineIndex
{
    private long[] _sparseOffsets; // offset of every Nth newline
    private long _totalLineCount;
    private volatile bool _isComplete;
    private readonly int _sparseFactor;
    private readonly int _charWidth;
    private int _sparseEntryCount;

    /// <summary>Total number of hard line breaks found so far.</summary>
    public long TotalLineCount => Volatile.Read(ref _totalLineCount);

    /// <summary>True when the background scan has finished.</summary>
    public bool IsComplete => _isComplete;

    /// <summary>Number of sparse index entries available (thread-safe).</summary>
    public int SparseEntryCount => Volatile.Read(ref _sparseEntryCount);

    /// <summary>The sparse factor (number of newlines between stored entries).</summary>
    public int SparseFactor => _sparseFactor;

    /// <summary>
    /// Constructs a line index. The sparse array stores every <paramref name="sparseFactor"/>th newline.
    /// </summary>
    /// <param name="charWidth">Minimum character width in bytes (1 for UTF-8/Windows-1252, 2 for UTF-16 LE).</param>
    public LineIndex(int charWidth = 1, int sparseFactor = 1000, int initialCapacity = 65536)
    {
        _charWidth = charWidth;
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
    /// When <c>_charWidth == 2</c> (UTF-16 LE), scans for <c>0x000A</c> code units at
    /// 2-byte-aligned positions using <see cref="Vector256{T}"/> / <see cref="Vector128{T}"/>
    /// of <see langword="ushort"/>.
    /// </summary>
    public unsafe void ScanChunk(byte* data, long length, long baseOffset, CancellationToken ct)
    {
        if (_charWidth == 2)
            ScanChunkUtf16(data, length, baseOffset, ct);
        else
            ScanChunkByte(data, length, baseOffset, ct);
    }

    /// <summary>Single-byte path (UTF-8 / Windows-1252): scan for raw 0x0A bytes.</summary>
    private unsafe void ScanChunkByte(byte* data, long length, long baseOffset, CancellationToken ct)
    {
        long linesSoFar = Volatile.Read(ref _totalLineCount);
        long pos = 0;
        bool canCancel = ct.CanBeCanceled;
        int cancelCheckCounter = 0;

        // Use Vector256 path when available (AVX2)
        if (Vector256.IsHardwareAccelerated && length >= 32) {
            var needle = Vector256.Create((byte)0x0A);
            long vectorEnd = length - 32;

            while (pos <= vectorEnd) {
                if (canCancel && (cancelCheckCounter++ & 0xFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var chunk = Vector256.Load(data + pos);
                var cmp = Vector256.Equals(chunk, needle);
                uint mask = cmp.ExtractMostSignificantBits();
                long chunkBaseOffset = baseOffset + pos;

                while (mask != 0) {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    RecordNewline(ref linesSoFar, chunkBaseOffset + bit);
                    mask &= mask - 1;
                }

                pos += 32;
            }
        }
        // Fallback to Vector128 (SSE2 / NEON)
        else if (Vector128.IsHardwareAccelerated && length >= 16) {
            var needle = Vector128.Create((byte)0x0A);
            long vectorEnd = length - 16;

            while (pos <= vectorEnd) {
                if (canCancel && (cancelCheckCounter++ & 0xFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var chunk = Vector128.Load(data + pos);
                var cmp = Vector128.Equals(chunk, needle);
                uint mask = cmp.ExtractMostSignificantBits();
                long chunkBaseOffset = baseOffset + pos;

                while (mask != 0) {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    RecordNewline(ref linesSoFar, chunkBaseOffset + bit);
                    mask &= mask - 1;
                }

                pos += 16;
            }
        }

        // Scalar tail
        for (; pos < length; pos++) {
            if (canCancel && (cancelCheckCounter++ & 0x3FF) == 0)
                ct.ThrowIfCancellationRequested();

            if (data[pos] == 0x0A)
                RecordNewline(ref linesSoFar, baseOffset + pos);
        }

        Volatile.Write(ref _totalLineCount, linesSoFar);
    }

    /// <summary>
    /// UTF-16 LE path: scan for 0x000A code units at 2-byte-aligned positions.
    /// Uses <see cref="Vector256{T}"/> of <see langword="ushort"/> (16 code units / 32 bytes)
    /// or <see cref="Vector128{T}"/> of <see langword="ushort"/> (8 code units / 16 bytes).
    /// </summary>
    private unsafe void ScanChunkUtf16(byte* data, long length, long baseOffset, CancellationToken ct)
    {
        long linesSoFar = Volatile.Read(ref _totalLineCount);
        ushort* wideData = (ushort*)data;
        long codeUnitCount = length / 2;
        long pos = 0; // index in code units

        // Vector256<ushort>: 16 code units per iteration (32 bytes)
        if (Vector256.IsHardwareAccelerated && codeUnitCount >= 16) {
            var needle = Vector256.Create((ushort)0x000A);
            long vectorEnd = codeUnitCount - 16;

            while (pos <= vectorEnd) {
                ct.ThrowIfCancellationRequested();

                var chunk = Vector256.Load(wideData + pos);
                var cmp = Vector256.Equals(chunk, needle);
                uint mask = cmp.ExtractMostSignificantBits();

                while (mask != 0) {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    long byteOffset = (pos + bit) * 2;
                    RecordNewline(ref linesSoFar, baseOffset + byteOffset);
                    mask &= mask - 1;
                }

                pos += 16;
            }
        }
        // Vector128<ushort>: 8 code units per iteration (16 bytes)
        else if (Vector128.IsHardwareAccelerated && codeUnitCount >= 8) {
            var needle = Vector128.Create((ushort)0x000A);
            long vectorEnd = codeUnitCount - 8;

            while (pos <= vectorEnd) {
                ct.ThrowIfCancellationRequested();

                var chunk = Vector128.Load(wideData + pos);
                var cmp = Vector128.Equals(chunk, needle);
                uint mask = cmp.ExtractMostSignificantBits();

                while (mask != 0) {
                    int bit = BitOperations.TrailingZeroCount(mask);
                    long byteOffset = (pos + bit) * 2;
                    RecordNewline(ref linesSoFar, baseOffset + byteOffset);
                    mask &= mask - 1;
                }

                pos += 8;
            }
        }

        // Scalar tail — remaining code units
        for (; pos < codeUnitCount; pos++) {
            if (wideData[pos] == 0x000A)
                RecordNewline(ref linesSoFar, baseOffset + pos * 2);
        }

        Volatile.Write(ref _totalLineCount, linesSoFar);
    }

    /// <summary>Records a newline hit: increments the count and stores sparse entries.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordNewline(ref long linesSoFar, long byteOffset)
    {
        linesSoFar++;

        if (linesSoFar % _sparseFactor == 0) {
            int idx = (int)(linesSoFar / _sparseFactor) - 1;
            if (idx >= _sparseOffsets.Length)
                GrowSparseArray(idx);

            _sparseOffsets[idx] = byteOffset;
            Volatile.Write(ref _sparseEntryCount, Math.Max(_sparseEntryCount, idx + 1));
        }
    }

    /// <summary>
    /// Doubles the sparse offset array so that <paramref name="requiredIndex"/> fits.
    /// Called only from the single background scanner thread.
    /// </summary>
    private void GrowSparseArray(int requiredIndex)
    {
        int newCapacity = _sparseOffsets.Length;
        while (newCapacity <= requiredIndex)
            newCapacity *= 2;

        long[] grown = new long[newCapacity];
        Array.Copy(_sparseOffsets, grown, _sparseOffsets.Length);
        _sparseOffsets = grown;
    }

    /// <summary>
    /// Marks the index as complete (called by the background scan task).
    /// </summary>
    public void MarkComplete() => _isComplete = true;
}
