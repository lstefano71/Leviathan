using BenchmarkDotNet.Attributes;

using Leviathan.GUI.Helpers;

using Microsoft.VSDiagnostics;

namespace Leviathan.GUI.Benchmarks;

[CPUUsageDiagnoser]
public class HexFormatterAddressBenchmarks
{
    private long _offset;
    [GlobalSetup]
    public void Setup()
    {
        _offset = 0x1234_5678_9ABC_DEF0;
    }

    [Benchmark]
    public string FormatOffsetHex16()
    {
        return HexFormatter.FormatOffset(_offset, 16);
    }

    [Benchmark]
    public string FormatOffsetHex8()
    {
        return HexFormatter.FormatOffset(0x1234_5678, 8);
    }

    [Benchmark]
    public string FormatOffsetDecimalPadLeft()
    {
        return _offset.ToString().PadLeft(16);
    }
}