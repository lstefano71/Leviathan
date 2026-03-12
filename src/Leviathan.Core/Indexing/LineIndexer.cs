using Leviathan.Core.IO;

namespace Leviathan.Core.Indexing;

/// <summary>
/// Manages background scanning of a memory-mapped file to build the <see cref="LineIndex"/>.
/// Processes the file in large chunks to maximize throughput.
/// </summary>
public sealed class LineIndexer : IDisposable
{
  private readonly MappedFileSource _source;
  private readonly LineIndex _index;
  private readonly CancellationTokenSource _cts;
  private Task? _scanTask;

  private const int ChunkSize = 4 * 1024 * 1024; // 4 MB chunks

  public LineIndex Index => _index;

  public LineIndexer(MappedFileSource source, int sparseFactor = 1000)
  {
    _source = source;
    _index = new LineIndex(sparseFactor);
    _cts = new CancellationTokenSource();
  }

  /// <summary>
  /// Starts scanning the file for newlines on a background thread.
  /// Returns immediately; check <see cref="LineIndex.IsComplete"/>.
  /// </summary>
  public void StartScan()
  {
    _scanTask = Task.Run(() =>
    {
      try {
        ScanAll(_cts.Token);
      } catch (OperationCanceledException) when (_cts.IsCancellationRequested) {
        // expected during disposal / file switches / save restart
      }
    }, _cts.Token);
  }

  private unsafe void ScanAll(CancellationToken ct)
  {
    long remaining = _source.Length;
    long offset = 0;

    while (remaining > 0 && !ct.IsCancellationRequested) {
      int chunkLen = (int)Math.Min(remaining, ChunkSize);
      var span = _source.GetSpan(offset, chunkLen);

      fixed (byte* ptr = span) {
        _index.ScanChunk(ptr, chunkLen, offset, ct);
      }

      offset += chunkLen;
      remaining -= chunkLen;
    }

    if (!ct.IsCancellationRequested) {
      _index.MarkComplete();
    }
  }

  public void Dispose()
  {
    _cts.Cancel();
    try { _scanTask?.Wait(); } catch (AggregateException) { /* expected */ }
    _cts.Dispose();
  }
}
