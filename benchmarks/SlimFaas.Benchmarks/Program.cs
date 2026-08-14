using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
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
public static class Program
{
    public static void Main(string[] args)
    {
        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithId("ShortRunInProcess"))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumn(RankColumn.Arabic)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
