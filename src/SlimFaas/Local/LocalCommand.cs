using System.Runtime.InteropServices;

namespace SlimFaas.Local;

public static class LocalCommand
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "local", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            ParsedCommand command = Parse(args);
            LoadedLocalManifest loaded = LocalManifestLoader.Load(command.Files, command.EnvironmentFiles);
            if (loaded.Manifest.Cluster.Nodes == 2)
            {
                Console.Error.WriteLine(
                    "warning: a two-node Raft cluster cannot tolerate the loss of either node.");
            }
            if (loaded.Manifest.Jobs.Values.Any(job => job.Resources is not null))
            {
                Console.Error.WriteLine(
                    "warning: Job CPU and memory resources are accepted for API compatibility " +
                    "but are not enforced in process mode.");
            }

            if (command.Verb == "validate")
            {
                Console.WriteLine(
                    $"Configuration from {loaded.ManifestPaths.Count} YAML file(s) is valid " +
                    $"({loaded.Manifest.Functions.Count} function(s), " +
                    $"{loaded.Manifest.Jobs.Count} job(s), " +
<<<<<<< HEAD
                    $"{loaded.Manifest.Processes.Count} auxiliary process(es), " +
=======
>>>>>>> origin/main
                    $"{loaded.Manifest.Cluster.Nodes} node(s)).");
                return 0;
            }

            using var stopping = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopping.Cancel();
            };
            Console.CancelKeyPress += handler;
            PosixSignalRegistration? termination = null;
            if (!OperatingSystem.IsWindows())
            {
                termination = PosixSignalRegistration.Create(
                    PosixSignal.SIGTERM,
                    context =>
                    {
                        context.Cancel = true;
                        stopping.Cancel();
                    });
            }
            try
            {
                var supervisor = new LocalSupervisor(loaded, command.Clean);
                await supervisor.RunAsync(stopping.Token);
            }
            finally
            {
                termination?.Dispose();
                Console.CancelKeyPress -= handler;
            }

            return 0;
        }
        catch (LocalManifestException exception)
        {
            Console.Error.WriteLine($"slimfaas local: {exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"slimfaas local: {exception}");
            return 1;
        }
    }

    internal static ParsedCommand Parse(string[] args)
    {
        if (args.Length < 2 ||
            (!string.Equals(args[1], "validate", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(args[1], "up", StringComparison.OrdinalIgnoreCase)))
        {
            throw Usage();
        }

        string verb = args[1].ToLowerInvariant();
        var files = new List<string>();
        var environmentFiles = new List<string>();
        bool clean = false;
        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-f":
                case "--file":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                        throw Usage();
                    files.Add(args[index]);
                    break;
                case "--env-file":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                        throw Usage();
                    environmentFiles.Add(args[index]);
                    break;
                case "--clean" when verb == "up":
                    clean = true;
                    break;
                default:
                    throw Usage();
            }
        }

        if (files.Count == 0)
            files.Add(LocalManifestLoader.DefaultFileName);

        return new ParsedCommand(verb, files, environmentFiles, clean);
    }

    private static LocalManifestException Usage()
        => new(
            "usage: slimfaas local validate [-f file]... [--env-file file]...\n" +
            "       slimfaas local up [-f file]... [--env-file file]... [--clean]");

    internal sealed record ParsedCommand(
        string Verb,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> EnvironmentFiles,
        bool Clean);
}
