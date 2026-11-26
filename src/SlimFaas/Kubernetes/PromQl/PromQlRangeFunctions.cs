using SlimFaas.Workers;

namespace SlimFaas.Kubernetes;

internal enum SampleFunction { Instant, Rate, Increase, InstantRate, Max, Min, Average, Sum, Count, Last, Present }

internal static class PromQlRangeFunctions
{
    // The caller supplies samples in (now - window, now], in timestamp order.
    internal static bool TryEvaluate(SampleFunction function, IReadOnlyList<MetricPoint> points,
        int first, int end, long now, long window, out double value)
    {
        var last = points[end - 1];
        value = last.Value;
        int count = end - first;
        if (function is SampleFunction.Instant or SampleFunction.Last) return true;
        if (function is SampleFunction.Count or SampleFunction.Present)
        {
            value = function == SampleFunction.Count ? count : 1;
            return true;
        }

        if (function is SampleFunction.Rate or SampleFunction.Increase or SampleFunction.InstantRate)
        {
            if (count < 2) return false;
            if (function == SampleFunction.InstantRate)
            {
                var previous = points[end - 2];
                value = (last.Value < previous.Value ? last.Value : last.Value - previous.Value)
                    / (last.Timestamp - previous.Timestamp);
                return true;
            }

            var initial = points[first];
            double delta = last.Value - initial.Value;
            double previousValue = initial.Value;
            for (int i = first + 1; i < end; i++)
            {
                double current = points[i].Value;
                if (current < previousValue) delta += previousValue;
                previousValue = current;
            }

            double elapsed = last.Timestamp - initial.Timestamp;
            double spacing = elapsed / (count - 1);
            double before = BoundaryExtension(window - (now - initial.Timestamp), spacing);
            double after = BoundaryExtension(now - last.Timestamp, spacing);
            if (delta > 0 && initial.Value >= 0)
                before = Math.Min(before, elapsed * (initial.Value / delta));
            value = delta * ((elapsed + before + after) / elapsed);
            if (function == SampleFunction.Rate) value /= window;
            return true;
        }

        Aggregation operation = function switch
        {
            SampleFunction.Min => Aggregation.Min,
            SampleFunction.Max => Aggregation.Max,
            SampleFunction.Average => Aggregation.Average,
            _ => Aggregation.Sum
        };
        var accumulator = new PromQlAccumulator();
        for (int i = first; i < end; i++) accumulator.Add(operation, points[i].Value);
        value = accumulator.Result(operation);
        return true;
    }

    // A distant boundary indicates incomplete coverage. Prometheus extends by half
    // a typical scrape interval in that case and never extrapolates a counter below zero.
    private static double BoundaryExtension(double gap, double spacing) =>
        gap >= 1.1 * spacing ? spacing / 2 : gap;
}
