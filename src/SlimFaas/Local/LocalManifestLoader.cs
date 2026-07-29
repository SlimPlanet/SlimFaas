using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SlimFaas.Local;

public static partial class LocalManifestLoader
{
    public const string DefaultFileName = "slimfaas.local.yaml";

    public static LoadedLocalManifest Load(string path) => Load([path], []);

    public static LoadedLocalManifest Load(
        IReadOnlyList<string> paths,
        IReadOnlyList<string> environmentFilePaths)
    {
        MergedLocalYaml merged = LocalYamlConfiguration.Load(paths, environmentFilePaths);
        string manifestPath = merged.ManifestPaths[0];

        IDeserializer deserializer = new StaticDeserializerBuilder(new LocalManifestYamlContext())
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithCaseInsensitivePropertyMatching()
            .WithDuplicateKeyChecking()
            .Build();

        LocalManifest manifest;
        try
        {
            manifest = deserializer.Deserialize<LocalManifest>(merged.Yaml)
                       ?? throw new LocalManifestException("The local manifest is empty.");
        }
        catch (LocalManifestException)
        {
            throw;
        }
        catch (Exception exception)
        {
            string location = exception is YamlException yamlException
                ? $" at line {yamlException.Start.Line + 1}, column {yamlException.Start.Column + 1}"
                : "";
            throw new LocalManifestException(
                $"Unable to parse the merged local configuration{location}: " +
                merged.Sanitize(exception.Message),
                exception);
        }

        try
        {
            string baseDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
            NormalizeDictionaries(manifest);
            Validate(manifest, baseDirectory);

            bool persistent = manifest.State.Mode.Equals("persistent", StringComparison.OrdinalIgnoreCase);
            string stateDirectory = persistent
                ? Path.GetFullPath(manifest.State.Directory ?? Path.Combine(".slimfaas", manifest.Name), baseDirectory)
                : Path.Combine(Path.GetTempPath(), "SlimFaas", "local", manifest.Name, Guid.NewGuid().ToString("N"));
            if (persistent)
                ValidateStateDirectorySafety(stateDirectory, baseDirectory);

            return new LoadedLocalManifest(manifest, manifestPath, baseDirectory, stateDirectory, persistent)
            {
                ManifestPaths = merged.ManifestPaths
            };
        }
        catch (LocalManifestException exception)
        {
            string sanitized = merged.Sanitize(exception.Message);
            if (string.Equals(sanitized, exception.Message, StringComparison.Ordinal))
                throw;
            throw new LocalManifestException(sanitized, exception);
        }
    }

    public static string ExpandTemplate(string value, int port, int replicaIndex)
        => value.Replace("{port}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{replica}", replicaIndex.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static void NormalizeDictionaries(LocalManifest manifest)
    {
        manifest.Name ??= "";
        manifest.Cluster ??= new LocalClusterManifest();
        manifest.ProcessPorts ??= new LocalPortRangeManifest();
        manifest.State ??= new LocalStateManifest();
        manifest.State.Mode ??= "";
        manifest.Functions = new Dictionary<string, LocalFunctionManifest>(
            manifest.Functions ?? new Dictionary<string, LocalFunctionManifest>(),
            StringComparer.OrdinalIgnoreCase);
        manifest.Jobs = new Dictionary<string, LocalJobManifest>(
            manifest.Jobs ?? new Dictionary<string, LocalJobManifest>(),
            StringComparer.OrdinalIgnoreCase);
        manifest.Processes = new Dictionary<string, LocalProcessManifest>(
            manifest.Processes ?? new Dictionary<string, LocalProcessManifest>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (string name in manifest.Functions.Keys.ToArray())
        {
            LocalFunctionManifest function = manifest.Functions[name] ?? new LocalFunctionManifest();
            manifest.Functions[name] = function;
            function.Command ??= [];
            function.WorkingDirectory ??= "";
            function.DebugUrl = string.IsNullOrWhiteSpace(function.DebugUrl)
                ? null
                : function.DebugUrl.Trim();
            function.Health ??= new LocalHealthManifest();
            function.Health.Path ??= "";
            if (function.Shutdown is not null)
                function.Shutdown.Path ??= "";
            function.Annotations = new Dictionary<string, string>(
                function.Annotations ?? new Dictionary<string, string>(),
                StringComparer.Ordinal);
            function.Environment = new Dictionary<string, string>(
                function.Environment ?? new Dictionary<string, string>(),
                StringComparer.Ordinal);
        }

        foreach (string name in manifest.Jobs.Keys.ToArray())
        {
            LocalJobManifest job = manifest.Jobs[name] ?? new LocalJobManifest();
            manifest.Jobs[name] = job;
            job.Command ??= [];
            job.WorkingDirectory ??= "";
            job.Visibility ??= "";
            job.RestartPolicy ??= "";
            job.DependsOn ??= [];
            job.Schedules ??= [];
            job.Environment = new Dictionary<string, string>(
                job.Environment ?? new Dictionary<string, string>(),
                StringComparer.Ordinal);
            if (job.Resources is not null)
            {
                job.Resources.Requests = new Dictionary<string, string>(
                    job.Resources.Requests ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                job.Resources.Limits = new Dictionary<string, string>(
                    job.Resources.Limits ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
            }

            foreach (LocalJobScheduleManifest schedule in job.Schedules)
            {
                schedule.Cron ??= "";
                schedule.Args ??= [];
                schedule.DependsOn ??= [];
            }
        }

        foreach (string name in manifest.Processes.Keys.ToArray())
        {
            LocalProcessManifest process = manifest.Processes[name] ?? new LocalProcessManifest();
            manifest.Processes[name] = process;
            process.Command ??= [];
            process.WorkingDirectory ??= "";
            process.RestartPolicy ??= "";
            process.Environment = new Dictionary<string, string>(
                process.Environment ?? new Dictionary<string, string>(),
                StringComparer.Ordinal);
        }
    }

    private static void Validate(LocalManifest manifest, string baseDirectory)
    {
        var errors = new List<string>();
        if (manifest.SchemaVersion != 1)
            errors.Add("schemaVersion must be 1.");
        if (!NamePattern().IsMatch(manifest.Name))
            errors.Add("name must contain only letters, digits, '.', '_' or '-'.");
        if (manifest.Cluster.Nodes is < 1 or > 3)
            errors.Add("cluster.nodes must be between 1 and 3.");

        ValidatePort("cluster.gatewayPort", manifest.Cluster.GatewayPort, errors);
        ValidatePortRange("cluster.httpPortBase", manifest.Cluster.HttpPortBase, manifest.Cluster.Nodes, errors);
        ValidatePortRange("cluster.raftPortBase", manifest.Cluster.RaftPortBase, manifest.Cluster.Nodes, errors);
        ValidatePort("processPorts.from", manifest.ProcessPorts.From, errors);
        ValidatePort("processPorts.to", manifest.ProcessPorts.To, errors);
        if (manifest.ProcessPorts.From > manifest.ProcessPorts.To)
            errors.Add("processPorts.from must be less than or equal to processPorts.to.");

        var reservedPorts = new HashSet<int> { manifest.Cluster.GatewayPort };
        for (int index = 0; index < manifest.Cluster.Nodes; index++)
        {
            reservedPorts.Add(manifest.Cluster.HttpPortBase + index);
            reservedPorts.Add(manifest.Cluster.RaftPortBase + index);
        }

        if (reservedPorts.Count != 1 + manifest.Cluster.Nodes * 2)
            errors.Add("gateway, HTTP and Raft port ranges must not overlap.");
        if (reservedPorts.Any(p => p >= manifest.ProcessPorts.From && p <= manifest.ProcessPorts.To))
            errors.Add("processPorts must not overlap gateway, HTTP or Raft ports.");

        int requiredProcessPorts = 0;
        var localDebugPorts = new Dictionary<int, string>();
        foreach ((string name, LocalFunctionManifest function) in manifest.Functions)
        {
            Uri? debugUri = null;
            if (!FunctionNamePattern().IsMatch(name))
                errors.Add($"functions.{name}: name must match [a-z0-9_-] and contain 3 to 63 characters.");
            if (function.DebugUrl is not null)
            {
                if (!TryParseDebugUrl(function.DebugUrl, out debugUri, out string? debugError))
                {
                    errors.Add($"functions.{name}.debugUrl {debugError}");
                }
                else if (reservedPorts.Contains(debugUri!.Port))
                {
                    errors.Add(
                        $"functions.{name}.debugUrl port must not overlap gateway, HTTP, or Raft ports.");
                }
                else if (IsLocalDebugUrl(debugUri))
                {
                    if (localDebugPorts.TryGetValue(debugUri.Port, out string? existingFunction))
                    {
                        errors.Add(
                            $"functions.{name}.debugUrl duplicates functions.{existingFunction}.debugUrl " +
                            $"port ({debugUri.Port}).");
                    }
                    else
                    {
                        localDebugPorts.Add(debugUri.Port, name);
                        if (debugUri.Port >= manifest.ProcessPorts.From &&
                            debugUri.Port <= manifest.ProcessPorts.To)
                        {
                            requiredProcessPorts = checked(requiredProcessPorts + 1);
                        }
                    }
                }
            }
            else
            {
                if (function.Command.Count == 0 || function.Command.Any(string.IsNullOrWhiteSpace))
                    errors.Add($"functions.{name}.command must contain an executable and non-empty arguments.");
                string workingDirectory = string.IsNullOrWhiteSpace(function.WorkingDirectory)
                    ? ""
                    : Path.GetFullPath(function.WorkingDirectory, baseDirectory);
                if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                    errors.Add($"functions.{name}.workingDirectory '{workingDirectory}' does not exist.");
            }
            if (!FunctionMetadataParser.IsFunction(function.Annotations))
                errors.Add($"functions.{name}.annotations must contain SlimFaas/Function: \"true\".");

            try
            {
                FunctionMetadata metadata = FunctionMetadataParser.Parse(function.Annotations, name);
                int maximum = metadata.Scale?.ReplicaMax ??
                              Math.Max(metadata.ReplicasAtStart, metadata.ReplicasMin);
                if (debugUri is null &&
                    metadata.Scale?.ReplicaMax is int replicaMax &&
                    replicaMax < Math.Max(metadata.ReplicasAtStart, metadata.ReplicasMin))
                {
                    errors.Add(
                        $"functions.{name}.annotations: SlimFaas/Scale.ReplicaMax must be greater than or " +
                        "equal to ReplicasMin and ReplicasAtStart.");
                }
                if (debugUri is null && maximum < 0)
                    errors.Add($"functions.{name}.annotations: ReplicaMax must not be negative.");
                if (debugUri is null)
                    requiredProcessPorts = checked(requiredProcessPorts + maximum);
            }
            catch (Exception exception)
            {
                errors.Add($"functions.{name}.annotations: {exception.Message}");
            }

            ValidateHttpSettings($"functions.{name}.health", function.Health.Path,
                function.Health.PeriodSeconds, function.Health.TimeoutSeconds,
                function.Health.StartupTimeoutSeconds, errors);
            if (function.Shutdown is not null)
            {
                if (!function.Shutdown.Path.StartsWith("/", StringComparison.Ordinal))
                    errors.Add($"functions.{name}.shutdown.path must start with '/'.");
                if (function.Shutdown.TimeoutSeconds < 1)
                    errors.Add($"functions.{name}.shutdown.timeoutSeconds must be positive.");
            }
        }

        var fixedProcessPorts = new Dictionary<int, string>();
        foreach ((string name, LocalProcessManifest process) in manifest.Processes)
        {
            string prefix = $"processes.{name}";
            if (!ProcessNamePattern().IsMatch(name))
            {
                errors.Add(
                    $"{prefix}: name must start with [a-z0-9], contain only [a-z0-9_.-], " +
                    "and contain at most 63 characters.");
            }
            if (process.Command.Count == 0 || process.Command.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{prefix}.command must contain an executable and non-empty arguments.");

            string workingDirectory = string.IsNullOrWhiteSpace(process.WorkingDirectory)
                ? ""
                : Path.GetFullPath(process.WorkingDirectory, baseDirectory);
            if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                errors.Add($"{prefix}.workingDirectory '{workingDirectory}' does not exist.");

            if (!LocalProcessRestartPolicyParser.TryParse(process.RestartPolicy, out _))
                errors.Add($"{prefix}.restartPolicy must be 'always', 'onFailure', or 'never'.");

            bool usesPortTemplate = process.Command.Any(ContainsPortTemplate) ||
                                    process.Environment.Values.Any(ContainsPortTemplate);
            if (process.Port is null)
            {
                if (usesPortTemplate)
                    errors.Add($"{prefix} uses '{{port}}' but does not configure port.");
                continue;
            }

            if (process.Port.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                requiredProcessPorts = checked(requiredProcessPorts + 1);
                continue;
            }

            if (!int.TryParse(process.Port, NumberStyles.None, CultureInfo.InvariantCulture, out int fixedPort) ||
                fixedPort is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            {
                errors.Add($"{prefix}.port must be 'auto' or an integer between 1 and 65535.");
                continue;
            }

            if (reservedPorts.Contains(fixedPort))
                errors.Add($"{prefix}.port must not overlap gateway, HTTP, or Raft ports.");
            if (localDebugPorts.TryGetValue(fixedPort, out string? debugFunction))
            {
                errors.Add(
                    $"{prefix}.port conflicts with functions.{debugFunction}.debugUrl ({fixedPort}).");
            }
            if (fixedProcessPorts.TryGetValue(fixedPort, out string? existingProcess))
            {
                errors.Add(
                    $"{prefix}.port duplicates processes.{existingProcess}.port ({fixedPort}).");
            }
            else
            {
                fixedProcessPorts.Add(fixedPort, name);
            }

            if (fixedPort >= manifest.ProcessPorts.From && fixedPort <= manifest.ProcessPorts.To)
                requiredProcessPorts = checked(requiredProcessPorts + 1);
        }

        int availableProcessPorts = manifest.ProcessPorts.To - manifest.ProcessPorts.From + 1;
        if (requiredProcessPorts > availableProcessPorts)
        {
            errors.Add(
                $"processPorts contains {availableProcessPorts} ports but function maxima and local processes " +
                $"require {requiredProcessPorts}.");
        }

        foreach ((string name, LocalJobManifest job) in manifest.Jobs)
        {
            if (!FunctionNamePattern().IsMatch(name))
                errors.Add($"jobs.{name}: name must match [a-z0-9_-] and contain 3 to 63 characters.");
            if (job.Command.Count == 0 || job.Command.Any(string.IsNullOrWhiteSpace))
                errors.Add($"jobs.{name}.command must contain an executable and non-empty arguments.");
            string workingDirectory = string.IsNullOrWhiteSpace(job.WorkingDirectory)
                ? ""
                : Path.GetFullPath(job.WorkingDirectory, baseDirectory);
            if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                errors.Add($"jobs.{name}.workingDirectory '{workingDirectory}' does not exist.");
            if (job.Parallelism < 1)
                errors.Add($"jobs.{name}.parallelism must be positive.");
            if (job.TtlSecondsAfterFinished < 0)
                errors.Add($"jobs.{name}.ttlSecondsAfterFinished must be non-negative.");
            if (job.BackoffLimit < 0)
                errors.Add($"jobs.{name}.backoffLimit must be non-negative.");
            if (!Enum.TryParse<FunctionVisibility>(job.Visibility, true, out _))
                errors.Add($"jobs.{name}.visibility must be Public or Private.");
            foreach (LocalJobScheduleManifest schedule in job.Schedules)
            {
                if (string.IsNullOrWhiteSpace(schedule.Cron))
                {
                    errors.Add($"jobs.{name}.schedules.cron must not be empty.");
                    continue;
                }

                try
                {
                    ResultWithError<long> result = Cron.GetNextJobExecutionTimestamp(
                        schedule.Cron,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (!result.IsSuccess)
                    {
                        errors.Add(
                            $"jobs.{name}.schedules.cron '{schedule.Cron}' is invalid: " +
                            $"{result.Error?.Description}");
                    }
                }
                catch (Exception exception) when (
                    exception is FormatException or OverflowException or DivideByZeroException)
                {
                    errors.Add(
                        $"jobs.{name}.schedules.cron '{schedule.Cron}' is invalid: {exception.Message}");
                }
            }
        }

        if (!manifest.State.Mode.Equals("persistent", StringComparison.OrdinalIgnoreCase) &&
            !manifest.State.Mode.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("state.mode must be 'persistent' or 'ephemeral'.");
        }

        if (errors.Count > 0)
            throw new LocalManifestException(string.Join(Environment.NewLine, errors));
    }

    private static void ValidateStateDirectorySafety(string stateDirectory, string baseDirectory)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(stateDirectory);
        string root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(trimmed) ?? "");
        string userProfile = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Path.TrimEndingDirectorySeparator(baseDirectory), StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(userProfile) &&
             string.Equals(trimmed, userProfile, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LocalManifestException(
                $"state.directory '{stateDirectory}' is too broad; choose a dedicated subdirectory.");
        }
    }

    public static bool IsTcpPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    internal static Uri GetDebugUri(string debugUrl)
    {
        if (TryParseDebugUrl(debugUrl, out Uri? uri, out _))
            return uri!;
        throw new InvalidOperationException($"Invalid validated debugUrl '{debugUrl}'.");
    }

    internal static bool IsLocalDebugUrl(Uri uri)
    {
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(uri.Host, out IPAddress? address) &&
               IPAddress.IsLoopback(address);
    }

    private static bool TryParseDebugUrl(
        string value,
        [NotNullWhen(true)] out Uri? uri,
        out string? error)
    {
        uri = null;
        error = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) ||
            !parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "must be an absolute HTTP URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "must not contain user information.";
            return false;
        }

        if (!HasExplicitPort(value, parsed))
        {
            error = "must contain an explicit port.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query))
        {
            error = "must not contain a query.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "must not contain a fragment.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool HasExplicitPort(string value, Uri uri)
    {
        int authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return false;
        authorityStart += 3;
        int authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        string authority = authorityEnd < 0
            ? value[authorityStart..]
            : value[authorityStart..authorityEnd];

        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            int closingBracket = authority.IndexOf(']');
            return closingBracket >= 0 &&
                   closingBracket + 1 < authority.Length &&
                   authority[closingBracket + 1] == ':' &&
                   int.TryParse(
                       authority[(closingBracket + 2)..],
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out int ipv6Port) &&
                   ipv6Port == uri.Port;
        }

        int separator = authority.LastIndexOf(':');
        return separator > 0 &&
               int.TryParse(
                   authority[(separator + 1)..],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out int port) &&
               port == uri.Port;
    }

    private static void ValidateHttpSettings(
        string prefix,
        string path,
        int period,
        int timeout,
        int startupTimeout,
        ICollection<string> errors)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal))
            errors.Add($"{prefix}.path must start with '/'.");
        if (period < 1)
            errors.Add($"{prefix}.periodSeconds must be positive.");
        if (timeout < 1)
            errors.Add($"{prefix}.timeoutSeconds must be positive.");
        if (startupTimeout < 1)
            errors.Add($"{prefix}.startupTimeoutSeconds must be positive.");
    }

    private static void ValidatePort(string name, int port, ICollection<string> errors)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            errors.Add($"{name} must be between 1 and 65535.");
    }

    private static void ValidatePortRange(string name, int firstPort, int count, ICollection<string> errors)
    {
        ValidatePort(name, firstPort, errors);
        if (firstPort > IPEndPoint.MaxPort - count + 1)
            errors.Add($"{name} range exceeds 65535.");
    }

    private static bool ContainsPortTemplate(string value)
        => value.Contains("{port}", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9_-]{3,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionNamePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessNamePattern();
}

public sealed class LocalManifestException : Exception
{
    public LocalManifestException(string message) : base(message)
    {
    }

    public LocalManifestException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
