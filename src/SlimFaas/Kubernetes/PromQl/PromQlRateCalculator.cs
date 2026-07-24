namespace SlimFaas.Kubernetes;

// Shared per-series rate computation used by RateNode, RatePerBucketNode, and AvgRateNode.
// Returns false when the series has fewer than two points, a zero/negative time span, or a
// negative delta (counter reset). Callers that receive false must skip the series.
internal static class PromQlRateCalculator
{
    public static bool TryComputeRate(SortedList<long, double> sl, out double rate)
    {
        rate = 0;
        if (sl.Count < 2) return false;

        var first = sl.First();
        var last = sl.Last();
        var dt = (double)(last.Key - first.Key);
        if (dt <= 0) return false;

        var diff = last.Value - first.Value;
        if (diff < 0) return false; // counter reset – skip this series

        rate = diff / dt;
        return true;
    }
}

// sum(rate(metric[win])) or rate(non-bucket metric[win])
internal sealed class RateNode(MetricSelector selector, TimeSpan window) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var series = ctx.SelectSeries(selector, window);
        double sum = 0.0;
        int validCount = 0;

        foreach (var sl in series.Values)
        {
            if (!PromQlRateCalculator.TryComputeRate(sl, out var rate)) continue;
            sum += rate;
            validCount++;
        }

        return EvalValue.FromScalar(validCount > 0 ? sum : double.NaN);
    }

    public override void CollectMetricNames(HashSet<string> names) => names.Add(selector.MetricName);
}

// rate(bucket_metric[win]) grouped by a label (typically "le" for histograms).
internal sealed class RatePerBucketNode(MetricSelector selector, TimeSpan window, string bucketLabel) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var series = ctx.SelectSeries(selector, window);
        var byLe = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (seriesKey, sl) in series)
        {
            if (!TryExtractLabel(seriesKey, bucketLabel, out var leValue)) continue;
            if (!PromQlRateCalculator.TryComputeRate(sl, out var rate)) continue;

            if (byLe.TryGetValue(leValue, out var acc))
                byLe[leValue] = acc + rate;
            else
                byLe[leValue] = rate;
        }

        return EvalValue.FromByLe(byLe);
    }

    public override void CollectMetricNames(HashSet<string> names) => names.Add(selector.MetricName);

    private static bool TryExtractLabel(string seriesKey, string label, out string value)
    {
        value = "";
        var idx = seriesKey.IndexOf('{');
        if (idx < 0) return false;
        var j = seriesKey.LastIndexOf('}');
        if (j < idx) return false;
        var content = seriesKey.Substring(idx + 1, j - idx - 1);
        foreach (var part in content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = part[..eq];
            if (!string.Equals(k, label, StringComparison.Ordinal)) continue;
            var v = part[(eq + 1)..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                v = v.Substring(1, v.Length - 2);
            value = v;
            return true;
        }
        return false;
    }
}

// avg(rate(metric[win])) without a "by" clause: mean of per-series rates.
internal sealed class AvgRateNode(MetricSelector selector, TimeSpan window) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var series = ctx.SelectSeries(selector, window);
        double sum = 0.0;
        int count = 0;

        foreach (var sl in series.Values)
        {
            if (!PromQlRateCalculator.TryComputeRate(sl, out var rate)) continue;
            sum += rate;
            count++;
        }

        if (count == 0) return EvalValue.FromScalar(double.NaN);
        return EvalValue.FromScalar(sum / count);
    }

    public override void CollectMetricNames(HashSet<string> names) => names.Add(selector.MetricName);
}
