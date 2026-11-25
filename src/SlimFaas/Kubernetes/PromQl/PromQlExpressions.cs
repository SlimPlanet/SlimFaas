using ZLinq;

namespace SlimFaas.Kubernetes;

internal abstract class ValueNode
{
    public virtual bool IsScalar => false;
    public abstract EvalValue Eval(EvalContext ctx);
    public abstract void CollectMetricNames(HashSet<string> names);
}

internal enum Aggregation { Sum, Min, Max, Average, Count }

// PromQL operators preserve series. Only the public autoscaling API implicitly
// reduces a final vector to one number, including its historical max_over_time rule.
internal readonly record struct EvalValue(double Scalar, Dictionary<SeriesKey, double>? Vector,
    Aggregation DefaultReduction = Aggregation.Sum)
{
    public double AsScalar() => Vector is null ? Scalar : Reduce(Vector, DefaultReduction);

    public static double Reduce(Dictionary<SeriesKey, double> vector, Aggregation operation)
    {
        var accumulator = new PromQlAccumulator();
        foreach (double value in vector.Values) accumulator.Add(operation, value);
        return accumulator.Result(operation);
    }

    public static EvalValue FromScalar(double value) => new(value, null);
    public static EvalValue FromVector(Dictionary<SeriesKey, double> values, Aggregation reduction = Aggregation.Sum) =>
        new(double.NaN, values, reduction);
}

internal sealed class NumberNode(double value) : ValueNode
{
    public override bool IsScalar => true;
    public override EvalValue Eval(EvalContext ctx) => EvalValue.FromScalar(value);
    public override void CollectMetricNames(HashSet<string> names) { }
}

internal sealed class SelectorNode(MetricSelector selector, SampleFunction function, long window) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx) => EvalValue.FromVector(ctx.Select(selector, function, window),
        function == SampleFunction.Max ? Aggregation.Max : Aggregation.Sum);
    public override void CollectMetricNames(HashSet<string> names) => names.Add(selector.MetricName);
}

internal sealed class ClampMinNode(ValueNode vector, ValueNode minimum) : ValueNode
{
    public override EvalValue Eval(EvalContext ctx)
    {
        EvalValue input = vector.Eval(ctx);
        double floor = minimum.Eval(ctx).Scalar;
        return EvalValue.FromVector(input.Vector!.AsValueEnumerable()
            .ToDictionary(static entry => entry.Key, entry => Math.Max(entry.Value, floor)), input.DefaultReduction);
    }

    public override void CollectMetricNames(HashSet<string> names)
    {
        vector.CollectMetricNames(names);
        minimum.CollectMetricNames(names);
    }
}

internal sealed class BinaryNode(ValueNode left, string op, ValueNode right, bool returnBool = false,
    LabelSelection? matching = null) : ValueNode
{
    public override bool IsScalar { get; } = left.IsScalar && right.IsScalar;

    public override EvalValue Eval(EvalContext ctx)
    {
        EvalValue a = left.Eval(ctx);
        EvalValue b = right.Eval(ctx);
        if (a.Vector is null && b.Vector is null)
            return EvalValue.FromScalar(Apply(a.Scalar, b.Scalar));

        var result = new Dictionary<SeriesKey, double>();
        if (matching is not null)
        {
            MatchVectors(a.Vector!, b.Vector!, result);
        }
        else
        {
            foreach (var (key, sample) in a.Vector ?? b.Vector!)
            {
                double lhs = a.Vector is null ? a.Scalar : sample;
                double rhs = b.Scalar;
                if (b.Vector is not null && !b.Vector.TryGetValue(key, out rhs)) continue;
                AddSample(result, key, lhs, rhs, sample);
            }
        }
        return EvalValue.FromVector(result, a.Vector is null ? b.DefaultReduction : a.DefaultReduction);
    }

    private void MatchVectors(Dictionary<SeriesKey, double> lhs, Dictionary<SeriesKey, double> rhs,
        Dictionary<SeriesKey, double> result)
    {
        if (lhs.Count == 0 || rhs.Count == 0) return;
        var rightByLabels = new Dictionary<SeriesKey, double>();
        foreach (var (key, value) in rhs)
        {
            if (!rightByLabels.TryAdd(matching!.Project(key), value))
                throw new FormatException("Right-hand series are not unique for the selected matching labels");
        }

        var matched = new HashSet<SeriesKey>();
        foreach (var (key, value) in lhs)
        {
            SeriesKey group = matching!.Project(key);
            if (!rightByLabels.TryGetValue(group, out double rightValue)) continue;
            if (!matched.Add(group))
                throw new FormatException("Left-hand series are not unique for the selected matching labels");
            AddSample(result, group, value, rightValue, value);
        }
    }

    private void AddSample(Dictionary<SeriesKey, double> result, SeriesKey key, double lhs, double rhs, double sample)
    {
        double value = Apply(lhs, rhs);
        if (IsComparison && !returnBool)
        {
            if (value == 0) return;
            value = sample;
        }
        result.Add(key, value);
    }

    private bool IsComparison => op is "==" or "!=" or "<" or "<=" or ">" or ">=";

    private double Apply(double a, double b) => op switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => a / b,
        "==" => EqualSamples(a, b) ? 1 : 0,
        "!=" => EqualSamples(a, b) ? 0 : 1,
        "<" => a < b ? 1 : 0,
        "<=" => a <= b ? 1 : 0,
        ">" => a > b ? 1 : 0,
        ">=" => a >= b ? 1 : 0,
        _ => throw new InvalidOperationException($"Unknown operator {op}")
    };

    // PromQL comparisons use exact IEEE 754 equality, including NaN != NaN.
    private static bool EqualSamples(double a, double b) => !double.IsNaN(a) && a.Equals(b);

    public override void CollectMetricNames(HashSet<string> names)
    {
        left.CollectMetricNames(names);
        right.CollectMetricNames(names);
    }
}

internal sealed class AggregateNode(ValueNode inner, Aggregation operation, LabelSelection? grouping) : ValueNode
{
    public override bool IsScalar => inner.IsScalar;

    public override EvalValue Eval(EvalContext ctx)
    {
        var value = inner.Eval(ctx);
        if (value.Vector is null) return value; // SlimFaas also accepts scalar aggregation arguments.
        if (grouping is null || grouping.SelectsNoLabels)
        {
            if (value.Vector.Count == 0) return value;
            return EvalValue.FromVector(new() { [default] = EvalValue.Reduce(value.Vector, operation) });
        }

        var groups = new Dictionary<SeriesKey, PromQlAccumulator>();
        foreach (var (series, sample) in value.Vector)
        {
            SeriesKey key = grouping.Project(series);
            var accumulator = groups.GetValueOrDefault(key);
            accumulator.Add(operation, sample);
            groups[key] = accumulator;
        }
        return EvalValue.FromVector(groups.AsValueEnumerable()
            .ToDictionary(static group => group.Key, group => group.Value.Result(operation)));
    }

    public override void CollectMetricNames(HashSet<string> names) => inner.CollectMetricNames(names);
}

// The variadic min/max forms are SlimFaas extensions used by existing configurations.
internal sealed class VariadicExtremaNode(List<ValueNode> args, bool minimum) : ValueNode
{
    public override bool IsScalar => true;

    public override EvalValue Eval(EvalContext ctx)
    {
        double result = double.NaN;
        foreach (var arg in args)
        {
            var value = arg.Eval(ctx).AsScalar();
            if (double.IsNaN(value)) continue;
            result = double.IsNaN(result) ? value : minimum ? Math.Min(result, value) : Math.Max(result, value);
        }
        return EvalValue.FromScalar(result);
    }

    public override void CollectMetricNames(HashSet<string> names)
    {
        foreach (var arg in args) arg.CollectMetricNames(names);
    }
}
