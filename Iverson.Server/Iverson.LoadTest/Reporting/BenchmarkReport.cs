using System.Diagnostics;
using HdrHistogram;

namespace Iverson.LoadTest.Reporting;

public sealed class BenchmarkReport
{
    private readonly LongHistogram _histogram = new(60_000_000L, 3);
    private long _errors;
    private long _count;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly Lock _lock = new();

    public void Record(long microseconds)
    {
        lock (_lock)
        {
            _histogram.RecordValue(microseconds);
            _count++;
        }
    }

    public void RecordError() => Interlocked.Increment(ref _errors);

    public void Print(string label, TextWriter? file = null)
    {
        long count;
        lock (_lock) { count = _count; }

        var elapsed = _wall.Elapsed.TotalSeconds;
        var rps     = elapsed > 0 ? count / elapsed : 0;

        string[] lines;
        if (count == 0)
        {
            lines =
            [
                $"=== {label} ===",
                $"  (no successful samples — {_errors} errors)",
            ];
        }
        else
        {
            long p50, p95, p99, p999, max;
            lock (_lock)
            {
                p50  = _histogram.GetValueAtPercentile(50.0);
                p95  = _histogram.GetValueAtPercentile(95.0);
                p99  = _histogram.GetValueAtPercentile(99.0);
                p999 = _histogram.GetValueAtPercentile(99.9);
                max  = _histogram.GetMaxValue();
            }

            lines =
            [
                $"=== {label} ===",
                $"  p50    : {p50  / 1000.0,8:F1} ms",
                $"  p95    : {p95  / 1000.0,8:F1} ms",
                $"  p99    : {p99  / 1000.0,8:F1} ms",
                $"  p999   : {p999 / 1000.0,8:F1} ms",
                $"  max    : {max  / 1000.0,8:F1} ms",
                $"  RPS    : {rps,8:F0} ops/sec",
                $"  errors : {_errors,8}",
            ];
        }

        foreach (var line in lines)
        {
            Console.WriteLine(line);
            file?.WriteLine(line);
        }
        Console.WriteLine();
        file?.WriteLine();
    }

    public static string ResultsPath(string scenario)
    {
        var dir = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "..", "docs", "performance", "results"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{DateTime.UtcNow:yyyy-MM-dd-HH-mm}-{scenario}.txt");
    }

    public static long NowMicros() =>
        (long)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
}
