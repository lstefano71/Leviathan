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
    /// Starts scanning the file for CSV record boundaries.
    /// The first chunk is processed synchronously so that row data is immediately
    /// available for the initial draw. The remainder runs on a background thread;
    /// check <see cref="CsvRowIndex.IsComplete"/>.
    /// </summary>
    public void StartScan()
    {
        long totalLength = _source.Length;
        long initialChunkLen = Math.Min(totalLength, ChunkSize);

        // Process first chunk synchronously so TotalRowCount > 0 before first draw.
        if (initialChunkLen > 0)
            ScanRange(0, initialChunkLen, _cts.Token);

        long remaining = totalLength - initialChunkLen;
        if (remaining <= 0) {
            // Entire file scanned synchronously.
            _index.MarkComplete();
            return;
        }

        long bgOffset = initialChunkLen;
        _scanTask = Task.Run(() => {
            try {
                ScanRange(bgOffset, totalLength - bgOffset, _cts.Token);
                if (!_cts.IsCancellationRequested)
                    _index.MarkComplete();
            } catch (OperationCanceledException) when (_cts.IsCancellationRequested) {
                // expected during disposal / file switches
            }
        }, _cts.Token);
    }

    /// <summary>Scans <paramref name="length"/> bytes starting at <paramref name="offset"/>.</summary>
    private unsafe void ScanRange(long offset, long length, CancellationToken ct)
    {
        long end = offset + length;
        while (offset < end && !ct.IsCancellationRequested) {
            int chunkLen = (int)Math.Min(end - offset, ChunkSize);
            ReadOnlySpan<byte> span = _source.GetSpan(offset, chunkLen);

            fixed (byte* ptr = span) {
                _index.ScanChunk(ptr, chunkLen, offset, _dialect, ct);
            }

            offset += chunkLen;
        }
    }

    /// <summary>
    /// Reads the first row to determine the column count.
    /// </summary>
    private void DetectColumnCount()
    {
        int sampleSize = (int)Math.Min(8192, _source.Length);
        if (sampleSize == 0) {
            _index.SetColumnCount(0);
            return;
        }

        ReadOnlySpan<byte> sample = _source.GetSpan(0, sampleSize);

        // Find the end of the first record (quote-aware)
        int pos = 0;
        bool inQuoted = false;
        byte quote = _dialect.Quote;

        while (pos < sample.Length) {
            byte b = sample[pos];

            if (inQuoted) {
                if (b == quote) {
                    if (pos + 1 < sample.Length && sample[pos + 1] == quote) {
                        pos += 2;
                        continue;
                    }
                    inQuoted = false;
                }
                pos++;
                continue;
            }

            if (b == quote && quote != 0) {
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
