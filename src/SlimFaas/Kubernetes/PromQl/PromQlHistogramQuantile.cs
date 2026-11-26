using System.Globalization;

namespace SlimFaas.Kubernetes;

internal sealed class HistogramQuantileNode(ValueNode quantile, ValueNode expression) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        double phi = quantile.Eval(ctx).Scalar;
        var buckets = expression.Eval(ctx).Vector!;
        var histograms = new Dictionary<SeriesKey, SortedList<double, double>>();
        foreach (var (key, count) in buckets)
        {
            var label = key.Get("le");
            double bound;
            if (label == "+Inf") bound = double.PositiveInfinity;
            else if (!double.TryParse(label, NumberStyles.Float, CultureInfo.InvariantCulture, out bound) ||
                     double.IsNaN(bound)) continue;

            var group = key.WithoutBucket();
            if (!histograms.TryGetValue(group, out var points)) histograms[group] = points = [];
            points[bound] = points.GetValueOrDefault(bound) + count;
        }
        var values = new Dictionary<SeriesKey, double>();
        foreach (var (key, points) in histograms) values[key] = Quantile(phi, points);
        return EvalValue.FromVector(values);
    }

    private static double Quantile(double phi, SortedList<double, double> buckets)
    {
        if (double.IsNaN(phi)) return double.NaN;
        if (phi < 0) return double.NegativeInfinity;
        if (phi > 1) return double.PositiveInfinity;
        if (buckets.Count < 2 || !double.IsPositiveInfinity(buckets.Keys[^1])) return double.NaN;

        double total = NormalizeCounts(buckets);
        if (total == 0) return double.NaN;
        double rank = phi * total;
        for (int i = 0; i < buckets.Count - 1; i++)
        {
            double count = buckets.Values[i];
            if (count < rank) continue;
            double upper = buckets.Keys[i];
            if (i == 0 && upper <= 0) return upper;
            double lower = i == 0 ? 0 : buckets.Keys[i - 1];
            double before = i == 0 ? 0 : buckets.Values[i - 1];
            return lower + (upper - lower) * ((rank - before) / (count - before));
        }
        return buckets.Keys[^2];
    }

    private static double NormalizeCounts(SortedList<double, double> buckets)
    {
        // Classic histogram buckets are cumulative. Small floating point differences
        // between bucket rates and decreases in malformed input must not reverse ranks.
        double previous = buckets.Values[0];
        for (int i = 1; i < buckets.Count; i++)
        {
            double count = buckets.Values[i];
            if (count < previous || Math.Abs(count - previous) <= 1e-12 * Math.Abs(count + previous))
                buckets.SetValueAtIndex(i, previous);
            else previous = count;
        }

        return previous;
    }

    public override void CollectMetricNames(HashSet<string> names)
    {
        quantile.CollectMetricNames(names);
        expression.CollectMetricNames(names);
    }
}
