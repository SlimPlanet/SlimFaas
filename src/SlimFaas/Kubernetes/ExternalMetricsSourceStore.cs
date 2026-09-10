using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SlimFaas.Kubernetes;

internal sealed record ExternalMetricsSource(string Namespace, string Function, string Name, string Url, string Identity)
{
    internal const string Prefix = "$external/";

    internal static ExternalMetricsSource? Resolve(string ns, string function, ScaleConfig config, string name)
    {
        var matches = config.Sources.Where(s => s is not null && s.Name == name).Take(2).ToArray();
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name.Any(char.IsControl) || !Uri.TryCreate(matches[0].Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return null;

        var url = uri.AbsoluteUri;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var identity = $"{Prefix}{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(function)}/{Uri.EscapeDataString(name)}/{hash}";
        return new(ns, function, name, url, identity);
    }
}

internal sealed record ExternalSourceObservation(ScalerState State, long LastSuccess, ExternalMetricsSource? Source = null);

// Health is deliberately not persisted: a new leader must complete its own scrape.
public sealed class ExternalMetricsSourceStore
{
    private readonly ConcurrentDictionary<string, ExternalSourceObservation> _observations = new(StringComparer.Ordinal);

    internal ExternalSourceObservation Get(string identity) =>
        _observations.GetValueOrDefault(identity) ?? new(ScalerState.Unavailable, 0);

    // An isolated read model for the playground; copying health must not emit telemetry.
    internal ExternalMetricsSourceStore Capture()
    {
        var copy = new ExternalMetricsSourceStore();
        foreach (var observation in _observations.ToArray()) copy._observations[observation.Key] = observation.Value;
        return copy;
    }

    internal void Record(ExternalMetricsSource source, ScalerState state, long timestamp)
    {
        _observations.AddOrUpdate(source.Identity,
            _ => new(state, state == ScalerState.Valid ? timestamp : 0, source),
            (_, previous) => new(state, state == ScalerState.Valid ? timestamp : previous.LastSuccess, source));
        ExternalScalerTelemetry.RecordSource(source, state, Get(source.Identity).LastSuccess);
    }

    internal void Reset()
    {
        foreach (var observation in _observations.Values)
            if (observation.Source is { } source)
                ExternalScalerTelemetry.InvalidateSource(source);
        _observations.Clear();
    }

    internal void Retain(IReadOnlySet<string> identities)
    {
        foreach (var key in _observations.Keys)
            if (!identities.Contains(key) && _observations.TryRemove(key, out var observation) &&
                observation.Source is { } source)
                ExternalScalerTelemetry.RemoveSource(source);
    }
}
