using SlimData;

namespace SlimFaas.Database;

internal enum SlimDataBatchLoadLevel
{
    Low,
    Medium,
    High
}

internal readonly record struct SlimDataBatchLoadSnapshot(
    double RequestsPerSecond,
    SlimDataBatchLoadLevel Level,
    AdaptiveBatchTiming Timing);

internal sealed class SlimDataBatchLoadController(
    double lowLoadRequestsPerSecond,
    Func<long>? timestampProvider = null,
    long? timestampFrequency = null)
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(5);
    private static readonly AdaptiveBatchTiming LowTiming = new(TimeSpan.Zero, TimeSpan.Zero);
    private static readonly AdaptiveBatchTiming MediumTiming = new(
        TimeSpan.FromMilliseconds(225),
        TimeSpan.FromMilliseconds(3));
    private static readonly AdaptiveBatchTiming HighTiming = new(
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(3));
    private const double HighLoadRequestsPerSecond = 4_000d / 60d;
    private const double LowLoadExitRatio = 1.2d;
    private const double HighLoadReturnRatio = 0.9d;
    private const double MinimumObservationSeconds = 0.25d;

    private readonly object _gate = new();
    private readonly Queue<long> _arrivals = new();
    private readonly Func<long> _timestampProvider =
        timestampProvider ?? System.Diagnostics.Stopwatch.GetTimestamp;
    private readonly long _timestampFrequency =
        timestampFrequency ?? System.Diagnostics.Stopwatch.Frequency;
    private SlimDataBatchLoadLevel _level = SlimDataBatchLoadLevel.Low;
    private double _requestsPerSecond;

    public void RecordArrival()
    {
        lock (_gate)
        {
            var now = _timestampProvider();
            _arrivals.Enqueue(now);
            Refresh(now);
        }
    }

    public AdaptiveBatchTiming GetTiming()
    {
        lock (_gate)
        {
            Refresh(_timestampProvider());
            return TimingFor(_level);
        }
    }

    public SlimDataBatchLoadSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            Refresh(_timestampProvider());
            return new SlimDataBatchLoadSnapshot(
                _requestsPerSecond,
                _level,
                TimingFor(_level));
        }
    }

    private void Refresh(long now)
    {
        var oldestAllowed = now -
                            (long)(ObservationWindow.TotalSeconds * _timestampFrequency);
        while (_arrivals.TryPeek(out var arrival) && arrival < oldestAllowed)
            _arrivals.Dequeue();

        if (_arrivals.Count == 0)
        {
            _requestsPerSecond = 0d;
        }
        else
        {
            var elapsed = (now - _arrivals.Peek()) / (double)_timestampFrequency;
            var observedSeconds = Math.Clamp(
                elapsed,
                MinimumObservationSeconds,
                ObservationWindow.TotalSeconds);
            _requestsPerSecond = _arrivals.Count == 1
                ? 1d / observedSeconds
                : (_arrivals.Count - 1d) / observedSeconds;
        }

        _level = _level switch
        {
            SlimDataBatchLoadLevel.Low when _requestsPerSecond > HighLoadRequestsPerSecond =>
                SlimDataBatchLoadLevel.High,
            SlimDataBatchLoadLevel.Low
                when _requestsPerSecond > lowLoadRequestsPerSecond * LowLoadExitRatio =>
                SlimDataBatchLoadLevel.Medium,
            SlimDataBatchLoadLevel.Medium
                when _requestsPerSecond <= lowLoadRequestsPerSecond =>
                SlimDataBatchLoadLevel.Low,
            SlimDataBatchLoadLevel.Medium when _requestsPerSecond > HighLoadRequestsPerSecond =>
                SlimDataBatchLoadLevel.High,
            SlimDataBatchLoadLevel.High
                when _requestsPerSecond <= lowLoadRequestsPerSecond =>
                SlimDataBatchLoadLevel.Low,
            SlimDataBatchLoadLevel.High
                when _requestsPerSecond < HighLoadRequestsPerSecond * HighLoadReturnRatio =>
                SlimDataBatchLoadLevel.Medium,
            _ => _level
        };
    }

    private static AdaptiveBatchTiming TimingFor(SlimDataBatchLoadLevel level)
        => level switch
        {
            SlimDataBatchLoadLevel.Low => LowTiming,
            SlimDataBatchLoadLevel.Medium => MediumTiming,
            SlimDataBatchLoadLevel.High => HighTiming,
            _ => MediumTiming
        };
}
