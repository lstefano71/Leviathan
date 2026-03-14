using Leviathan.Core.IO;

namespace Leviathan.Core.Csv;

/// <summary>
/// Manages background scanning of a memory-mapped file to build the
/// <see cref="CsvRowIndex"/>. Processes the file in large chunks,
/// carrying quote-state across chunk boundaries.
/// </summary>
public sealed class CsvRowIndexer : IDisposable
{
  private readonly MappedFileSource _source;
  private readonly CsvRowIndex _index;
  private readonly CsvDialect _dialect;
  private readonly CancellationTokenSource _cts;
  private Task? _scanTask;

  private const int ChunkSize = 4 * 1024 * 1024; // 4 MB chunks

  /// <summary>The sparse row index being built.</summary>
  public CsvRowIndex Index => _index;

  public CsvRowIndexer(MappedFileSource source, CsvDialect dialect, int sparseFactor = 1000)
  {
    _source = source;
    _dialect = dialect;
    _index = new CsvRowIndex(sparseFactor);
    _cts = new CancellationTokenSource();

    // Determine column count from the first row
    DetectColumnCount();
  }

  /// <summary>
  /// Starts scanning the file for CSV record boundaries on a background thread.
  /// Returns immediately; check <see cref="CsvRowIndex.IsComplete"/>.
  /// </summary>
  public void StartScan()
  {
    _scanTask = Task.Run(() => {
      try {
        ScanAll(_cts.Token);
      } catch (OperationCanceledException) when (_cts.IsCancellationRequested) {
        // expected during disposal / file switches
      }
    }, _cts.Token);
  }

  private unsafe void ScanAll(CancellationToken ct)
  {
    long remaining = _source.Length;
    long offset = 0;

    while (remaining > 0 && !ct.IsCancellationRequested)
    {
      int chunkLen = (int)Math.Min(remaining, ChunkSize);
      ReadOnlySpan<byte> span = _source.GetSpan(offset, chunkLen);

      fixed (byte* ptr = span) {
        _index.ScanChunk(ptr, chunkLen, offset, _dialect, ct);
      }

      offset += chunkLen;
      remaining -= chunkLen;
    }

    if (!ct.IsCancellationRequested)
      _index.MarkComplete();
  }

  /// <summary>
  /// Reads the first row to determine the column count.
  /// </summary>
  private void DetectColumnCount()
  {
    int sampleSize = (int)Math.Min(8192, _source.Length);
    if (sampleSize == 0)
    {
      _index.SetColumnCount(0);
      return;
    }

    ReadOnlySpan<byte> sample = _source.GetSpan(0, sampleSize);

    // Find the end of the first record (quote-aware)
    int pos = 0;
    bool inQuoted = false;
    byte quote = _dialect.Quote;

    while (pos < sample.Length)
    {
      byte b = sample[pos];

      if (inQuoted)
      {
        if (b == quote)
        {
          if (pos + 1 < sample.Length && sample[pos + 1] == quote)
          {
            pos += 2;
            continue;
          }
          inQuoted = false;
        }
        pos++;
        continue;
      }

      if (b == quote && quote != 0)
      {
        inQuoted = true;
        pos++;
        continue;
      }

      if (b == (byte)'\n' || b == (byte)'\r')
        break;

      pos++;
    }

    ReadOnlySpan<byte> firstRow = sample[..pos];
    Span<CsvField> fields = stackalloc CsvField[256];
    int count = CsvFieldParser.ParseRecord(firstRow, _dialect, fields);
    _index.SetColumnCount(count);
  }

  public void Dispose()
  {
    _cts.Cancel();
    try { _scanTask?.Wait(); } catch (AggregateException) { /* expected */ }
    _cts.Dispose();
  }
}
