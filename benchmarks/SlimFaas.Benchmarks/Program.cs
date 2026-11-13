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
// ShortRun + InProcess is the default for quick comparisons. --extended uses
// six warmups and twelve measured iterations for more stable relative estimates.
// Keep the same machine and configuration for before/after measurements;
// docs/performance-benchmarks.md links the reports and their limitations.
public static class Program
{
    public static void Main(string[] args)
    {
        bool extended = args.Contains("--extended", StringComparer.Ordinal);
        args = args.Where(static argument => argument != "--extended").ToArray();
        Job job = extended
            ? Job.Default.WithWarmupCount(6).WithIterationCount(12)
                .WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(500))
            : Job.ShortRun;
        var config = DefaultConfig.Instance
            .AddJob(job
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithId(extended ? "ExtendedInProcess" : "ShortRunInProcess"))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumn(RankColumn.Arabic)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
