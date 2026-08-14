using BenchmarkDotNet.Attributes;
using SlimFaas.Benchmarks.Support;
using SlimFaas.Kubernetes;

namespace SlimFaas.Benchmarks;

/// <summary>
/// Theme 4 — recurring work of the periodic services.
/// - <see cref="ScheduleLastTicks"/>: evaluation of a wake-up schedule, called for
///   every scheduled function on every 1 s tick of the ScaleReplicasWorker.
///   Before/after caching the NodaTime ForId resolution.
/// - <see cref="RegisterAlreadyKnownQuery"/>: re-registration of an already-known
///   PromQL query, called for every trigger on every deployment synchronization
///   (3 s). Before/after the cache of already-parsed queries.
/// </summary>
[MemoryDiagnoser]
public class SchedulingBenchmarks
{
    private const string Query =
        "sum(rate(http_server_requests_seconds_count{namespace=\"bench\",job=\"function-10\"}[1m]))";

    private DeploymentInformation _functionWithSchedule = null!;
    private RequestedMetricsRegistry _registry = null!;
    private DateTime _nowUtc;

    [GlobalSetup]
    public void Setup()
    {
        var deployments = BenchData.BuildDeployments(functionCount: 1, podsPerFunction: 1);
        _functionWithSchedule = deployments.Functions[0] with
        {
            Schedule = new ScheduleConfig
            {
                TimeZoneID = "Europe/Paris",
                Default = new DefaultSchedule
                {
                    WakeUp = ["07:00", "12:30", "18:00"],
                    ScaleDownTimeout =
                    [
                        new ScaleDownTimeout { Time = "21:00", Value = 60 },
                        new ScaleDownTimeout { Time = "23:00", Value = 10 }
                    ]
                }
            }
        };
        _nowUtc = new DateTime(2026, 8, 11, 14, 0, 0, DateTimeKind.Utc);

        _registry = new RequestedMetricsRegistry();
        _registry.RegisterFromQuery(Query);
    }

    [Benchmark]
    public long? ScheduleLastTicks() =>
        ReplicasService.GetLastTicksFromSchedule(_functionWithSchedule, _nowUtc);

    [Benchmark]
    public void RegisterAlreadyKnownQuery() => _registry.RegisterFromQuery(Query);
}
