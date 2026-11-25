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
        if (vector.Count == 0) return double.NaN;
        var values = vector.Values.AsValueEnumerable();
        return operation switch
        {
            Aggregation.Min => values.Min(),
            Aggregation.Max => values.Max(),
            Aggregation.Average => values.Average(),
            Aggregation.Count => vector.Count,
            _ => values.Sum()
        };
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

internal sealed class BinaryNode(ValueNode left, string op, ValueNode right, bool returnBool = false) : ValueNode
{
    public override bool IsScalar { get; } = left.IsScalar && right.IsScalar;

    public override EvalValue Eval(EvalContext ctx)
    {
        var a = left.Eval(ctx);
        var b = right.Eval(ctx);
        if (a.Vector is null && b.Vector is null)
            return EvalValue.FromScalar(Apply(a.Scalar, b.Scalar));

        var result = new Dictionary<SeriesKey, double>();
        foreach (var (key, sample) in a.Vector ?? b.Vector!)
        {
            double lhs = a.Vector is null ? a.Scalar : sample;
            double rhs = b.Scalar;
            if (b.Vector is not null && !b.Vector.TryGetValue(key, out rhs)) continue;
            double value = Apply(lhs, rhs);
            if (IsComparison && !returnBool)
            {
                if (value == 0) continue;
                value = sample;
            }
            result.Add(key, value);
        }
        return EvalValue.FromVector(result, a.Vector is null ? b.DefaultReduction : a.DefaultReduction);
    }

    private bool IsComparison => op is "==" or "!=" or "<" or "<=" or ">" or ">=";

    private double Apply(double a, double b) => op switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => a / b,
        "==" => a == b ? 1 : 0,
        "!=" => a != b ? 1 : 0,
        "<" => a < b ? 1 : 0,
        "<=" => a <= b ? 1 : 0,
        ">" => a > b ? 1 : 0,
        ">=" => a >= b ? 1 : 0,
        _ => throw new InvalidOperationException($"Unknown operator {op}")
    };

    public override void CollectMetricNames(HashSet<string> names)
    {
        left.CollectMetricNames(names);
        right.CollectMetricNames(names);
    }
}

internal sealed class AggregateNode(ValueNode inner, Aggregation operation, string[] by) : ValueNode
{
    public override bool IsScalar => inner.IsScalar;

    public override EvalValue Eval(EvalContext ctx)
    {
        var value = inner.Eval(ctx);
        if (value.Vector is null) return value; // SlimFaas also accepts scalar aggregation arguments.
        if (by.Length == 0)
        {
            if (value.Vector.Count == 0) return value;
            return EvalValue.FromVector(new() { [default] = EvalValue.Reduce(value.Vector, operation) });
        }

        var groups = new Dictionary<SeriesKey, double>();
        var counts = operation == Aggregation.Average ? new Dictionary<SeriesKey, int>() : null;
        foreach (var (series, sample) in value.Vector)
        {
            var key = series.Group(by);
            var next = operation == Aggregation.Count ? 1 : sample;
            if (groups.TryGetValue(key, out double previous))
                next = operation switch
                {
                    Aggregation.Min => Math.Min(previous, next),
                    Aggregation.Max => Math.Max(previous, next),
                    _ => previous + next
                };
            groups[key] = next;
            if (counts is not null) counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        if (counts is not null)
            foreach (var (key, count) in counts) groups[key] /= count;
        return EvalValue.FromVector(groups);
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
