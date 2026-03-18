using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.VSDiagnostics;
using Leviathan.Core.Csv;

namespace Leviathan.GUI.Benchmarks;
[CPUUsageDiagnoser]
public class CsvFieldParserBenchmarks
{
    private byte[] _record;
    private CsvDialect _dialect;
    [GlobalSetup]
    public void Setup()
    {
        string sample = "1,2,\"a,quoted\",simple,\"with \"\"escaped\"\" quotes\"";
        _record = Encoding.UTF8.GetBytes(sample);
        _dialect = new CsvDialect((byte)',', (byte)'\"', 0);
    }

    [Benchmark]
    public int ParseRecord_CountFields()
    {
        Span<CsvField> fields = stackalloc CsvField[32];
        int count = CsvFieldParser.ParseRecord(_record, _dialect, fields);
        return count;
    }

    [Benchmark]
    public string Unescape_QuotedField()
    {
        Span<CsvField> fields = stackalloc CsvField[32];
        int count = CsvFieldParser.ParseRecord(_record, _dialect, fields);
        if (count <= 2)
            return string.Empty;
        Span<byte> dest = stackalloc byte[1024];
        int written = CsvFieldParser.UnescapeField(_record, fields[2], _dialect, dest);
        return Encoding.UTF8.GetString(dest.Slice(0, written));
    }
}