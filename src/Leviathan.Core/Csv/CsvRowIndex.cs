using System.Runtime.CompilerServices;

namespace Leviathan.Core.Csv;

/// <summary>
/// Sparse index of CSV record (row) byte-offsets, built by scanning the file with
/// a state machine that tracks quoted fields. Unlike <see cref="Indexing.LineIndex"/>,
/// newlines inside quoted fields are not treated as record boundaries.
///
/// Stores every Nth record offset in a sparse array for O(1) scrollbar positioning
/// over arbitrarily large files.
/// </summary>
public sealed class CsvRowIndex
{
  private readonly long[] _sparseOffsets;
  private long _totalRowCount;
  private volatile bool _isComplete;
  private readonly int _sparseFactor;
  private int _sparseEntryCount;

  /// <summary>Total number of CSV records found so far (excluding header if applicable).</summary>
  public long TotalRowCount => Volatile.Read(ref _totalRowCount);

  /// <summary>True when the background scan has finished.</summary>
  public bool IsComplete => _isComplete;

  /// <summary>Number of sparse index entries available (thread-safe).</summary>
  public int SparseEntryCount => Volatile.Read(ref _sparseEntryCount);

  /// <summary>The sparse factor (number of records between stored entries).</summary>
  public int SparseFactor => _sparseFactor;

  /// <summary>
  /// The byte offset where the first data row starts (past the header row, if any).
  /// Set after scanning completes if the dialect has a header.
  /// </summary>
  public long FirstDataRowOffset { get; private set; }

  /// <summary>The number of columns detected in the first row (used for column count).</summary>
  public int ColumnCount { get; private set; }

  /// <summary>Scanner state carried between chunks.</summary>
  private ScanState _state = ScanState.Normal;

  public CsvRowIndex(int sparseFactor = 1000, int initialCapacity = 16384)
  {
    _sparseFactor = sparseFactor;
    _sparseOffsets = new long[initialCapacity];
  }

  /// <summary>
  /// Returns the byte offset of the Nth sparse entry
  /// (i.e., record <c>index * sparseFactor</c>).
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public long GetSparseOffset(int sparseIndex)
  {
    if ((uint)sparseIndex >= (uint)SparseEntryCount)
      throw new ArgumentOutOfRangeException(nameof(sparseIndex));
    return _sparseOffsets[sparseIndex];
  }

  /// <summary>
  /// Estimates the row number for a given byte offset using the sparse index.
  /// </summary>
  public long EstimateRowForOffset(long byteOffset, long fileLength)
  {
    if (fileLength == 0 || _totalRowCount == 0) return 0;

    int lo = 0, hi = SparseEntryCount - 1;
    while (lo <= hi)
    {
      int mid = lo + (hi - lo) / 2;
      if (_sparseOffsets[mid] <= byteOffset)
        lo = mid + 1;
      else
        hi = mid - 1;
    }

    long baseRow = hi >= 0 ? (long)(hi + 1) * _sparseFactor : 0;
    return Math.Min(baseRow, _totalRowCount);
  }

  /// <summary>
  /// Scans a chunk of bytes for CSV record boundaries using a quote-aware
  /// state machine. State is carried across calls, so chunks can be processed
  /// sequentially from a large file.
  /// </summary>
  /// <param name="data">Pointer to the chunk data.</param>
  /// <param name="length">Length of the chunk in bytes.</param>
  /// <param name="baseOffset">Byte offset of this chunk within the file.</param>
  /// <param name="dialect">The CSV dialect being used.</param>
  /// <param name="ct">Cancellation token.</param>
  public unsafe void ScanChunk(byte* data, long length, long baseOffset, CsvDialect dialect, CancellationToken ct)
  {
    byte quote = dialect.Quote;
    byte escape = dialect.Escape;
    long rowsSoFar = Volatile.Read(ref _totalRowCount);
    ScanState state = _state;
    bool canCancel = ct.CanBeCanceled;
    int cancelCheckCounter = 0;

    for (long pos = 0; pos < length; pos++)
    {
      if (canCancel && (cancelCheckCounter++ & 0x3FF) == 0)
        ct.ThrowIfCancellationRequested();

      byte b = data[pos];

      switch (state)
      {
        case ScanState.Normal:
          if (b == quote && quote != 0)
          {
            state = ScanState.InQuotedField;
          }
          else if (b == (byte)'\n')
          {
            RecordRow(ref rowsSoFar, baseOffset + pos + 1);
          }
          else if (b == (byte)'\r')
          {
            // Peek for \n (CRLF)
            if (pos + 1 < length && data[pos + 1] == (byte)'\n')
            {
              pos++; // skip the \n
            }
            RecordRow(ref rowsSoFar, baseOffset + pos + 1);
          }
          break;

        case ScanState.InQuotedField:
          if (escape == quote)
          {
            // RFC 4180: doubled quote escaping
            if (b == quote)
            {
              if (pos + 1 < length && data[pos + 1] == quote)
              {
                pos++; // skip escaped quote
              }
              else
              {
                state = ScanState.Normal;
              }
            }
          }
          else
          {
            // Backslash-style escaping
            if (b == escape)
            {
              pos++; // skip next char
            }
            else if (b == quote)
            {
              state = ScanState.Normal;
            }
          }
          break;
      }
    }

    _state = state;
    Volatile.Write(ref _totalRowCount, rowsSoFar);
  }

  /// <summary>Records a row boundary and stores sparse entries.</summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void RecordRow(ref long rowsSoFar, long nextRowOffset)
  {
    rowsSoFar++;

    // Capture the first data row offset (after the header)
    if (rowsSoFar == 1)
      FirstDataRowOffset = nextRowOffset;

    if (rowsSoFar % _sparseFactor == 0)
    {
      int idx = (int)(rowsSoFar / _sparseFactor) - 1;
      if (idx < _sparseOffsets.Length)
      {
        _sparseOffsets[idx] = nextRowOffset;
        Volatile.Write(ref _sparseEntryCount, Math.Max(_sparseEntryCount, idx + 1));
      }
    }
  }

  /// <summary>
  /// Sets the column count (typically from parsing the first row).
  /// </summary>
  public void SetColumnCount(int count) => ColumnCount = count;

  /// <summary>Marks the index as complete.</summary>
  public void MarkComplete() => _isComplete = true;

  private enum ScanState : byte
  {
    Normal,
    InQuotedField
  }
}
