using System.Globalization;

namespace SlimFaas.Kubernetes;

internal abstract class ValueNode
{
    public abstract EvalValue Eval(EvalContext ctx);

    // Populates names with every distinct metric name referenced by this node and its children.
    public abstract void CollectMetricNames(HashSet<string> names);
}

// Result of evaluating a node: either a scalar or a bucket map (le → value) for histograms.
internal readonly record struct EvalValue(double Scalar, Dictionary<string, double>? ByLe)
{
    public bool IsScalar => ByLe is null;

    public double AsScalar() =>
        IsScalar
            ? Scalar
            : ByLe is { Count: > 0 }
                ? ByLe.Values.Sum()
                : double.NaN;

    public static EvalValue FromScalar(double x) => new(x, null);
    public static EvalValue FromByLe(Dictionary<string, double> buckets) => new(double.NaN, buckets);
}

internal sealed class NumberNode(double value) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx) => EvalValue.FromScalar(value);
    public override void CollectMetricNames(HashSet<string> names) { }
}

internal sealed class BinaryNode(ValueNode left, string op, ValueNode right) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var a = left.Eval(ctx).AsScalar();
        var b = right.Eval(ctx).AsScalar();
        return op switch
        {
            "+" => EvalValue.FromScalar(a + b),
            "-" => EvalValue.FromScalar(a - b),
            "*" => EvalValue.FromScalar(a * b),
            "/" => EvalValue.FromScalar(SafeDiv(a, b)),
            _ => throw new InvalidOperationException($"Unknown operator {op}")
        };
    }

    public override void CollectMetricNames(HashSet<string> names)
    {
        left.CollectMetricNames(names);
        right.CollectMetricNames(names);
    }

    internal static double SafeDiv(double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b))
            return double.NaN;

        if (b == 0.0)
            return a == 0.0 ? double.NaN : double.PositiveInfinity;

        return a / b;
    }
}

internal sealed class SumNode(ValueNode inner, string? byLabel) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var v = inner.Eval(ctx);
        if (v.IsScalar) return v;

        if (byLabel is null)
        {
            // sum(...) without "by" → collapse to a scalar.
            if (v.ByLe is null) return EvalValue.FromScalar(double.NaN);
            return EvalValue.FromScalar(v.ByLe.Values.Sum());
        }

        // sum by (le)(...) → preserve the bucket dictionary for histogram_quantile.
        return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));
    }

    public override void CollectMetricNames(HashSet<string> names) => inner.CollectMetricNames(names);
}

internal sealed class MinNode(ValueNode inner, string? byLabel) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var v = inner.Eval(ctx);
        if (v.IsScalar) return v;

        if (byLabel is not null)
            return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));

        if (v.ByLe is null || v.ByLe.Count == 0) return EvalValue.FromScalar(double.NaN);
        return EvalValue.FromScalar(v.ByLe.Values.Min());
    }

    public override void CollectMetricNames(HashSet<string> names) => inner.CollectMetricNames(names);
}

internal sealed class MaxNode(ValueNode inner, string? byLabel) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var v = inner.Eval(ctx);
        if (v.IsScalar) return v;

        if (byLabel is not null)
            return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));

        if (v.ByLe is null || v.ByLe.Count == 0) return EvalValue.FromScalar(double.NaN);
        return EvalValue.FromScalar(v.ByLe.Values.Max());
    }

    public override void CollectMetricNames(HashSet<string> names) => inner.CollectMetricNames(names);
}

internal sealed class AvgNode(ValueNode inner, string? byLabel) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var v = inner.Eval(ctx);
        if (v.IsScalar) return v;

        if (byLabel is not null)
            return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));

        if (v.ByLe is null || v.ByLe.Count == 0) return EvalValue.FromScalar(double.NaN);
        return EvalValue.FromScalar(v.ByLe.Values.Average());
    }

    public override void CollectMetricNames(HashSet<string> names) => inner.CollectMetricNames(names);
}

internal sealed class HistogramQuantileNode(double phi, ValueNode bucketsExpr) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var v = bucketsExpr.Eval(ctx);
        if (v.IsScalar) return EvalValue.FromScalar(double.NaN);

        var buckets = v.ByLe!;
        var points = new List<(double le, double count)>();
        foreach (var (k, c) in buckets)
        {
            if (string.Equals(k, "+Inf", StringComparison.OrdinalIgnoreCase))
                points.Add((double.PositiveInfinity, c));
            else if (double.TryParse(k, NumberStyles.Float, CultureInfo.InvariantCulture, out var ub))
                points.Add((ub, c));
        }
        points.Sort((a, b) => a.le.CompareTo(b.le));
        if (points.Count == 0) return EvalValue.FromScalar(double.NaN);

        // Prometheus histogram_quantile: linear interpolation within each bucket.
        double rank = phi * points[^1].count;
        double prevLe = 0.0;
        double prevCount = 0.0;
        foreach (var (le, count) in points)
        {
            if (count >= rank)
            {
                if (double.IsInfinity(le)) return EvalValue.FromScalar(prevLe);
                var bucketCount = count - prevCount;
                if (bucketCount <= 0) return EvalValue.FromScalar(le);
                var posInBucket = (rank - prevCount) / bucketCount;
                return EvalValue.FromScalar(prevLe + (le - prevLe) * posInBucket);
            }
            prevLe = le;
            prevCount = count;
        }

        return EvalValue.FromScalar(points[^1].le);
    }

    public override void CollectMetricNames(HashSet<string> names) => bucketsExpr.CollectMetricNames(names);
}

internal sealed class MaxOverTimeNode(MetricSelector selector, TimeSpan window) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        var series = ctx.SelectSeries(selector, window);
        double? globalMax = null;

        foreach (var sl in series.Values)
        {
            if (sl.Count == 0) continue;
            var localMax = sl.Values.Max();
            if (double.IsNaN(localMax)) continue;
            globalMax = globalMax is null ? localMax : Math.Max(globalMax.Value, localMax);
        }

        return EvalValue.FromScalar(globalMax ?? double.NaN);
    }

    public override void CollectMetricNames(HashSet<string> names) => names.Add(selector.MetricName);
}

internal sealed class SelectorSumNode(MetricSelector selector, TimeSpan? window) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        if (window is null)
        {
            // Instant selector: sum of the most-recent value per series.
            var series = ctx.SelectSeries(selector, window: null);
            if (series.Count == 0) return EvalValue.FromScalar(double.NaN);
            double total = 0.0;
            foreach (var sl in series.Values)
                if (sl.Count > 0) total += sl.Values[^1];
            return EvalValue.FromScalar(total);
        }

        // Range selector: delegate to the shared rate calculation.
        return new RateNode(selector, window.Value).Eval(ctx);
    }

    public override void CollectMetricNames(HashSet<string> names) => names.Add(selector.MetricName);
}

internal sealed class VariadicMinNode(List<ValueNode> args) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        double? acc = null;
        foreach (var a in args)
        {
            var v = a.Eval(ctx).AsScalar();
            if (double.IsNaN(v)) continue;
            acc = acc is null ? v : Math.Min(acc.Value, v);
        }
        return EvalValue.FromScalar(acc ?? double.NaN);
    }

    public override void CollectMetricNames(HashSet<string> names)
    {
        foreach (var arg in args) arg.CollectMetricNames(names);
    }
}

internal sealed class VariadicMaxNode(List<ValueNode> args) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        double? acc = null;
        foreach (var a in args)
        {
            var v = a.Eval(ctx).AsScalar();
            if (double.IsNaN(v)) continue;
            acc = acc is null ? v : Math.Max(acc.Value, v);
        }
        return EvalValue.FromScalar(acc ?? double.NaN);
    }

    public override void CollectMetricNames(HashSet<string> names)
    {
        foreach (var arg in args) arg.CollectMetricNames(names);
    }
}
