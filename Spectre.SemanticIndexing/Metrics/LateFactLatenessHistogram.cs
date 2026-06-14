namespace Spectre.SemanticIndexing.Metrics;

internal sealed class LateFactLatenessHistogram
{
    private const long MinuteNanos = 60L * 1_000_000_000L;
    private const int MaximumTrackedMinutes = 14 * 24 * 60;
    private const int OverflowBucket = MaximumTrackedMinutes + 1;
    private readonly long[] _fenwickTree = new long[OverflowBucket + 2];
    private long _count;

    public void Record(long latenessNanos, SemanticIndexingMetrics metrics)
    {
        latenessNanos = Math.Max(0, latenessNanos);
        metrics.LateFactLatenessMaxNanos = Math.Max(metrics.LateFactLatenessMaxNanos, latenessNanos);

        var minuteUpperBound = CeilingDivide(latenessNanos, MinuteNanos);
        var bucket = minuteUpperBound <= MaximumTrackedMinutes
            ? checked((int)minuteUpperBound)
            : OverflowBucket;

        Add(bucket);
        _count++;
        metrics.LateFactLatenessP50Nanos = PercentileUpperBound(0.50);
        metrics.LateFactLatenessP95Nanos = PercentileUpperBound(0.95);
        metrics.LateFactLatenessP99Nanos = PercentileUpperBound(0.99);
    }

    private long PercentileUpperBound(double percentile)
    {
        var rank = checked((long)Math.Ceiling(_count * percentile));
        var bucket = FindByCumulativeCount(rank);
        return bucket == OverflowBucket ? long.MaxValue : bucket * MinuteNanos;
    }

    private static long CeilingDivide(long value, long divisor) =>
        value == 0 ? 0 : checked(((value - 1) / divisor) + 1);

    private void Add(int zeroBasedBucket)
    {
        for (var index = zeroBasedBucket + 1; index < _fenwickTree.Length; index += index & -index)
        {
            _fenwickTree[index]++;
        }
    }

    private int FindByCumulativeCount(long rank)
    {
        var index = 0;
        long cumulative = 0;
        var bit = HighestPowerOfTwoBelow(_fenwickTree.Length);

        while (bit != 0)
        {
            var next = index + bit;
            if (next < _fenwickTree.Length && cumulative + _fenwickTree[next] < rank)
            {
                index = next;
                cumulative += _fenwickTree[next];
            }

            bit >>= 1;
        }

        return index;
    }

    private static int HighestPowerOfTwoBelow(int value)
    {
        var result = 1;
        while (checked(result << 1) < value)
        {
            result <<= 1;
        }

        return result;
    }
}
