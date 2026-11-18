namespace SlimFaas.Benchmarking;

internal static class BenchmarkStatistics
{
    // Both application drivers use the same interpolation on sorted samples.
    public static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0d;
        double rank = percentile * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sorted[lower];
        double fraction = rank - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }
}
