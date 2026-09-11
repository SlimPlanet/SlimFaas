using SlimFaasBenchmark;

if (args.Length == 0)
{
    BenchmarkCli.PrintUsage();
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "target" => await BenchmarkTarget.RunAsync(CommandArguments.Parse(args[1..])),
        "run" => await BenchmarkRunner.RunAsync(CommandArguments.Parse(args[1..])),
        "compare" => await BenchmarkComparer.RunAsync(CommandArguments.Parse(args[1..])),
        _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
