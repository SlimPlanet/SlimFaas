using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace SlimFaas.Benchmarks;

// SlimFaas micro-benchmark harness.
//
// Usage:
//   dotnet run -c Release --project benchmarks/SlimFaas.Benchmarks -- --filter '*'
//   dotnet run -c Release --project benchmarks/SlimFaas.Benchmarks -- --filter '*DeploymentsSnapshot*'
//
// The configuration is deliberately fast (ShortRun + InProcess): the goal is to
// compare a before/after on the same machine in the same session, not to produce
// publishable absolute numbers. Results are recorded in
// docs/performance-benchmarks.md for every performance commit.
//
// Cross-version comparison (same benchmark code compiled against two commits, both
// measured in the same session): benchmarks/compare-versions.sh, results in
// docs/performance-benchmarks-cross-version.md. The brief JSON export below feeds
// benchmarks/compare-microbenchmarks.py.
public static class Program
{
    public static void Main(string[] args)
    {
        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithId("ShortRunInProcess"))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(JsonExporter.Brief)
            .AddColumn(RankColumn.Arabic)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
