namespace SlimFaas;

using Prometheus;
using System.Collections.Concurrent;

public class DynamicGaugeService
{
    private sealed record LabeledGaugeInfo(Gauge Gauge, string[] LabelNames);

    // Gauges sans labels (compat rétro)
    private readonly ConcurrentDictionary<string, Gauge> _gauges = new();

    // Gauges avec labels (ex: function=...)
    private readonly ConcurrentDictionary<string, LabeledGaugeInfo> _labeledGauges = new();

    private Gauge GetOrCreateGauge(string name, string help)
    {
        return _gauges.GetOrAdd(name, _ => Metrics.CreateGauge(name, help));
    }

    private LabeledGaugeInfo GetOrCreateLabeledGauge(
        string name,
        string help,
        IReadOnlyDictionary<string, string> labels)
    {
        return _labeledGauges.GetOrAdd(name, _ =>
        {
            // On fige l’ordre des labels au premier appel
            var labelNames = labels.Keys
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            var gauge = Metrics.CreateGauge(name, help, labelNames);
            return new LabeledGaugeInfo(gauge, labelNames);
        });
    }

    // ----- API existante (sans labels) -----

    public void SetGaugeValue(string name, double value, string help = "")
    {
        var gauge = GetOrCreateGauge(name, help);
        gauge.Set(value);
    }

    public double GetGaugeValue(string name, string help = "")
    {
        var gauge = GetOrCreateGauge(name, help);
        return gauge.Value;
    }

    // ----- Nouvelle API (avec labels) -----

    public void SetGaugeValue(
        string name,
        double value,
        string help,
        IReadOnlyDictionary<string, string> labels)
    {
        var info = GetOrCreateLabeledGauge(name, help, labels);

        var labelValues = new string[info.LabelNames.Length];
        for (var i = 0; i < info.LabelNames.Length; i++)
        {
            labels.TryGetValue(info.LabelNames[i], out var v);
            labelValues[i] = v ?? string.Empty;
        }

        info.Gauge.WithLabels(labelValues).Set(value);
    }

    public double GetGaugeValue(
        string name,
        string help,
        IReadOnlyDictionary<string, string> labels)
    {
        var info = GetOrCreateLabeledGauge(name, help, labels);

        var labelValues = new string[info.LabelNames.Length];
        for (var i = 0; i < info.LabelNames.Length; i++)
        {
            labels.TryGetValue(info.LabelNames[i], out var v);
            labelValues[i] = v ?? string.Empty;
        }

        return info.Gauge.WithLabels(labelValues).Value;
    }
}
