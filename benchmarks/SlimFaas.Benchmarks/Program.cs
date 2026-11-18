using SlimFaasBenchmark;

namespace SlimFaas.Benchmarks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            BenchmarkCli.PrintUsage();
            return 2;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "micro" => MicroBenchmarkRunner.Run(args[1..]),
                "target" => await BenchmarkTarget.RunAsync(CommandArguments.Parse(args[1..])),
                "features" => await FeatureRunner.RunAsync(CommandArguments.Parse(args[1..])),
                "sdk-target" => await BenchmarkSdkTarget.RunAsync(CommandArguments.Parse(args[1..])),
                "run" => await BenchmarkRunner.RunAsync(CommandArguments.Parse(args[1..])),
                "report" => await BenchmarkRunner.ReportAsync(CommandArguments.Parse(args[1..])),
                "compare" => await BenchmarkComparer.RunAsync(CommandArguments.Parse(args[1..])),
                "help" or "--help" or "-h" => PrintHelp(),
                // Preserve existing BenchmarkDotNet filter/list/extended invocations.
                _ when args[0].StartsWith('-', StringComparison.Ordinal) => MicroBenchmarkRunner.Run(args),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
            return 1;
        }
    }

    private static int PrintHelp()
    {
        BenchmarkCli.PrintUsage();
        return 0;
    }
}
