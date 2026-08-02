using SlimFaas.Kubernetes;
using SlimFaas.Local;
using System.Text.Json;

namespace SlimFaas.Tests.Local;

public sealed class LocalManifestLoaderTests
{
    [Fact]
    public void Load_MergesMappingsAndReplacesSequencesFromLeftToRight()
    {
        using var directory = new TemporaryDirectory();
        string basePath = directory.Write(
            "base.yaml",
            ValidYaml(
                """
                environment:
                  KEEP: base
                  SHARED: base
                  CaseKey: upper
                """).Replace(
                    "SlimFaas/Function: \"true\"",
                    "SlimFaas/Function: \"true\"\n      Custom/Key: upper",
                    StringComparison.Ordinal));
        string overlayPath = directory.Write(
            "dev.yaml",
            """
            functions:
              function-one:
                command: ["dotnet", "run", "--dev"]
                Environment:
                  SHARED: dev
                  ADDED: dev
                  casekey: lower
                annotations:
                  SlimFaas/ReplicasAtStart: "0"
                  custom/key: lower
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load([basePath, overlayPath], []);
        LocalFunctionManifest function = loaded.Manifest.Functions["function-one"];

        Assert.Equal(["dotnet", "run", "--dev"], function.Command);
        Assert.Equal("base", function.Environment["KEEP"]);
        Assert.Equal("dev", function.Environment["SHARED"]);
        Assert.Equal("dev", function.Environment["ADDED"]);
        Assert.Equal("upper", function.Environment["CaseKey"]);
        Assert.Equal("lower", function.Environment["casekey"]);
        Assert.Equal("true", function.Annotations["SlimFaas/Function"]);
        Assert.Equal("0", function.Annotations["SlimFaas/ReplicasAtStart"]);
        Assert.Equal("upper", function.Annotations["Custom/Key"]);
        Assert.Equal("lower", function.Annotations["custom/key"]);
        Assert.Equal(
            [Path.GetFullPath(basePath), Path.GetFullPath(overlayPath)],
            loaded.ManifestPaths);
    }

    [Fact]
    public void Load_NullDeletesInheritedDictionaryValuesAndFunctions()
    {
        using var directory = new TemporaryDirectory();
        string basePath = directory.Write(
            "base.yaml",
            ValidYaml(
                    """
                    environment:
                      KEEP: base
                      REMOVE: inherited
                    """)
                + "\n  function-two:\n" +
                "    command: [\"dotnet\", \"--info\"]\n" +
                "    workingDirectory: .\n" +
                "    annotations:\n" +
                "      SlimFaas/Function: \"true\"\n");
        string overlayPath = directory.Write(
            "dev.yaml",
            """
            functions:
              function-one:
                environment:
                  REMOVE: null
                  LITERAL_NULL: "null"
              function-two: null
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load([basePath, overlayPath], []);

        LocalFunctionManifest function = loaded.Manifest.Functions["function-one"];
        Assert.Equal("base", function.Environment["KEEP"]);
        Assert.False(function.Environment.ContainsKey("REMOVE"));
        Assert.Equal("null", function.Environment["LITERAL_NULL"]);
        Assert.False(loaded.Manifest.Functions.ContainsKey("function-two"));
    }

    [Fact]
    public void Load_DoesNotExpandAValueRemovedByAnOverlay()
    {
        using var directory = new TemporaryDirectory();
        const string variable = "SLIMFAAS_LOCAL_TEST_OVERRIDDEN_MISSING_A71F";
        Environment.SetEnvironmentVariable(variable, null);
        string basePath = directory.Write(
            "base.yaml",
            ValidYaml(
                "environment:\n" +
                $"  VALUE: \"${{{variable}}}\"\n"));
        string overlayPath = directory.Write(
            "dev.yaml",
            """
            functions:
              function-one:
                environment:
                  VALUE: local-value
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load([basePath, overlayPath], []);

        Assert.Equal("local-value", loaded.Manifest.Functions["function-one"].Environment["VALUE"]);
    }

    [Fact]
    public void Load_UsesProcessEnvironmentThenLaterEnvironmentFiles()
    {
        using var directory = new TemporaryDirectory();
        const string processName = "SLIMFAAS_LOCAL_TEST_PROCESS_PRIORITY_F12A";
        const string fileName = "SLIMFAAS_LOCAL_TEST_FILE_PRIORITY_F12A";
        const string specialName = "SLIMFAAS_LOCAL_TEST_SPECIAL_F12A";
        string? previous = Environment.GetEnvironmentVariable(processName);
        Environment.SetEnvironmentVariable(processName, "from-process");
        try
        {
            string basePath = directory.Write(
                "base.yaml",
                ValidYaml(
                    "environment:\n" +
                    $"  PROCESS_VALUE: \"${{{processName}}}\"\n" +
                    $"  FILE_VALUE: \"${{{fileName}}}\"\n" +
                    $"  SPECIAL_VALUE: \"${{{specialName}}}\"\n"));
            string firstEnvironmentPath = directory.Write(
                "first.env",
                $"""
                 {processName}=from-first-file
                 {fileName}=from-first-file
                 {specialName}="p:a #b \"quoted\""
                 """);
            string secondEnvironmentPath = directory.Write(
                "second.env",
                $"{fileName}=from-second-file # local override");

            LoadedLocalManifest loaded = LocalManifestLoader.Load(
                [basePath],
                [firstEnvironmentPath, secondEnvironmentPath]);
            IReadOnlyDictionary<string, string> environment =
                loaded.Manifest.Functions["function-one"].Environment;

            Assert.Equal("from-process", environment["PROCESS_VALUE"]);
            Assert.Equal("from-second-file", environment["FILE_VALUE"]);
            Assert.Equal("p:a #b \"quoted\"", environment["SPECIAL_VALUE"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(processName, previous);
        }
    }

    [Fact]
    public void Load_InterpolatesTypedStructuralValues()
    {
        using var directory = new TemporaryDirectory();
        const string variable = "SLIMFAAS_LOCAL_TEST_NODE_COUNT_02D4";
        Environment.SetEnvironmentVariable(variable, null);
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "nodes: 1",
                $"nodes: ${{{variable}}}",
                StringComparison.Ordinal).Replace(
                "raftPortBase: 41002",
                "raftPortBase: 41010",
                StringComparison.Ordinal));
        string environmentPath = directory.Write("nodes.env", $"{variable}=2");

        LoadedLocalManifest loaded = LocalManifestLoader.Load([path], [environmentPath]);

        Assert.Equal(2, loaded.Manifest.Cluster.Nodes);
    }

    [Fact]
    public void Load_DoesNotExposeInterpolatedValuesInDeserializeErrors()
    {
        using var directory = new TemporaryDirectory();
        const string variable = "SLIMFAAS_LOCAL_TEST_REDACTED_C91E";
        const string secret = "do-not-print-this-secret-C91E";
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "nodes: 1",
                $"nodes: \"${{{variable}}}\"",
                StringComparison.Ordinal));
        string environmentPath = directory.Write("secret.env", $"{variable}={secret}");

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load([path], [environmentPath]));

        Assert.DoesNotContain(secret, error.Message);
    }

    [Fact]
    public void Load_DoesNotExposeInterpolatedValuesInValidationErrors()
    {
        using var directory = new TemporaryDirectory();
        const string variable = "SLIMFAAS_LOCAL_TEST_REDACTED_VALIDATION_D73B";
        const string secret = "missing-secret-directory-D73B";
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "workingDirectory: .",
                $"workingDirectory: \"${{{variable}}}\"",
                StringComparison.Ordinal));
        string environmentPath = directory.Write("secret.env", $"{variable}={secret}");

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load([path], [environmentPath]));

        Assert.DoesNotContain(secret, error.Message);
        Assert.Contains("***", error.Message);
    }

    [Fact]
    public void Load_RejectsCaseInsensitiveDuplicateStructuralKeysInTheirSource()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "duplicate.yaml",
            ValidYaml(
                """
                Environment:
                  FIRST: one
                environment:
                  SECOND: two
                """));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(Path.GetFullPath(path), error.Message);
        Assert.Contains("duplicate key", error.Message);
    }

    [Fact]
    public void Load_RejectsMultipleDocumentsInTheirSource()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "multiple.yaml",
            $"{ValidYaml()}{Environment.NewLine}---{Environment.NewLine}functions: {{}}");

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(Path.GetFullPath(path), error.Message);
        Assert.Contains("exactly one YAML document", error.Message);
    }

    [Fact]
    public void Load_ResolvesRuntimePathsRelativeToTheFirstManifest()
    {
        using var directory = new TemporaryDirectory();
        string baseDirectory = Path.Combine(directory.Path, "base");
        string overlayDirectory = Path.Combine(directory.Path, "overlay");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(overlayDirectory);
        string basePath = Path.Combine(baseDirectory, "base.yaml");
        string overlayPath = Path.Combine(overlayDirectory, "dev.yaml");
        File.WriteAllText(basePath, ValidYaml());
        File.WriteAllText(
            overlayPath,
            """
            functions:
              function-one:
                workingDirectory: .
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load([basePath, overlayPath], []);

        Assert.Equal(Path.GetFullPath(baseDirectory), loaded.BaseDirectory);
    }

    [Fact]
    public void Load_AcceptsCaseInsensitiveStructureAndKubernetesAnnotations()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "slimfaas.local.yaml",
            """
            schemaVersion: 1
            name: parser-test
            cluster:
              nodes: 1
              entrypointPort: 41000
              nodeHttpPortBase: 41001
              raftPortBase: 41002
            processPorts:
              from: 41100
              to: 41103
            state:
              mode: ephemeral
            functions:
              fibonacci:
                command: ["dotnet", "run", "--urls=http://127.0.0.1:{port}"]
                workingDirectory: .
                annotations:
                  SlimFaas/Function: "true"
                  SlimFaas/ReplicasMin: "0"
                  SlimFaas/ReplicasAtStart: "1"
                  SlimFaas/DefaultVisibility: "Private"
                  prometheus.io/port: "{port}"
                  SlimFaas/Scale: '{"ReplicaMax": 3, "Triggers": [], "Behavior": {}}'
                Environment:
                  ASPNETCORE_URLS: "http://127.0.0.1:{port}"
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);
        LocalFunctionManifest function = Assert.Single(loaded.Manifest.Functions).Value;
        FunctionMetadata metadata = FunctionMetadataParser.Parse(function.Annotations, "fibonacci");

        Assert.Equal("http://127.0.0.1:{port}", function.Environment["ASPNETCORE_URLS"]);
        Assert.Equal(FunctionVisibility.Private, metadata.Visibility);
        Assert.Equal(3, metadata.Scale?.ReplicaMax);
        Assert.Equal(41000, loaded.Manifest.Cluster.EntrypointPort);
        Assert.Equal(41001, loaded.Manifest.Cluster.NodeHttpPortBase);
    }

    [Fact]
    public void Load_AcceptsKubernetesJobAnnotations()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml() +
            """

            jobs:
              batch-job:
                command: ["dotnet", "--info"]
                workingDirectory: .
                annotations:
                  SlimFaas/Job: "true"
                  SlimFaas/DefaultVisibility: "Public"
                  SlimFaas/NumberParallelJob: "2"
                  SlimFaas/DependsOn: "function-one, function-two"
                  SlimFaas/Schedules: '[{"Schedule":"*/5 * * * *","Args":["39"],"DependsOn":["function-two"]}]'
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);
        LocalJobManifest job = Assert.Single(loaded.Manifest.Jobs).Value;
        JobMetadata metadata = JobMetadataParser.Parse(job.Annotations);

        Assert.Equal(FunctionVisibility.Public, metadata.Visibility);
        Assert.Equal(2, metadata.NumberParallelJob);
        Assert.Equal(["function-one", "function-two"], metadata.DependsOn);
        ScheduleCreateJob schedule = Assert.Single(metadata.Schedules);
        Assert.Equal("*/5 * * * *", schedule.Schedule);
        Assert.Equal(["39"], schedule.Args);
        Assert.Equal(["function-two"], schedule.DependsOn);
    }

    [Fact]
    public void Load_AcceptsLocalProcessDependenciesForFunctionsJobsAndSchedules()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "SlimFaas/Function: \"true\"",
                "SlimFaas/Function: \"true\"\n" +
                "      SlimFaas/DependsOn: \"processes:database-emulator\"",
                StringComparison.Ordinal) +
            """

            jobs:
              batch-job:
                command: ["dotnet", "--info"]
                workingDirectory: .
                annotations:
                  SlimFaas/Job: "true"
                  SlimFaas/DependsOn: "processes:database-emulator"
                  SlimFaas/Schedules: '[{"Schedule":"*/5 * * * *","Args":[],"DependsOn":["processes:database-emulator"]}]'
            processes:
              database-emulator:
                command: ["dotnet", "--info"]
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        Assert.Equal(
            "processes:database-emulator",
            FunctionMetadataParser.Parse(
                loaded.Manifest.Functions["function-one"].Annotations,
                "function-one").DependsOn.Single());
        JobMetadata job = JobMetadataParser.Parse(
            loaded.Manifest.Jobs["batch-job"].Annotations);
        Assert.Equal("processes:database-emulator", job.DependsOn.Single());
        Assert.Equal(
            "processes:database-emulator",
            Assert.Single(job.Schedules).DependsOn?.Single());
    }

    [Theory]
    [InlineData("processes:missing", "does not match an entry under processes")]
    [InlineData("processes:", "must name a process")]
    public void Load_RejectsInvalidLocalProcessDependencies(
        string dependency,
        string expectedMessage)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "SlimFaas/Function: \"true\"",
                $"SlimFaas/Function: \"true\"\n" +
                $"      SlimFaas/DependsOn: \"{dependency}\"",
                StringComparison.Ordinal));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("parallelism: 2", "parallelism")]
    [InlineData("visibility: Public", "visibility")]
    [InlineData("dependsOn: [function-one]", "dependsOn")]
    [InlineData("schedules: []", "schedules")]
    public void Load_RejectsRemovedLocalJobMetadataFields(string field, string fieldName)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml() +
            $$"""

            jobs:
              batch-job:
                command: ["dotnet", "--info"]
                workingDirectory: .
                annotations:
                  SlimFaas/Job: "true"
                {{field}}
            """);

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(fieldName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SlimFaas/Job: \"false\"", "SlimFaas/Job")]
    [InlineData("SlimFaas/DefaultVisibility: Internal", "SlimFaas/DefaultVisibility")]
    [InlineData("SlimFaas/NumberParallelJob: \"0\"", "SlimFaas/NumberParallelJob")]
    [InlineData("SlimFaas/Schedules: not-json", "SlimFaas/Schedules")]
    [InlineData("SlimFaas/Schedules: '[{\"Schedule\":\"invalid\",\"Args\":[]}]'", "SlimFaas/Schedules")]
    public void Load_RejectsInvalidKubernetesJobAnnotations(string annotation, string expected)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml() +
            $$"""

            jobs:
              batch-job:
                command: ["dotnet", "--info"]
                workingDirectory: .
                annotations:
                  {{annotation}}
            """);

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Load_UsesCanonicalClusterPortDefaults()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml()
                .Replace("  entrypointPort: 41000\n", "", StringComparison.Ordinal)
                .Replace("  nodeHttpPortBase: 41001\n", "", StringComparison.Ordinal));

        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        Assert.Equal(30020, loaded.Manifest.Cluster.EntrypointPort);
        Assert.Equal(30021, loaded.Manifest.Cluster.NodeHttpPortBase);
        Assert.Equal("Error", loaded.Manifest.Cluster.NodeLogLevel);
    }

    [Theory]
    [InlineData("Trace")]
    [InlineData("Debug")]
    [InlineData("Information")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Critical")]
    [InlineData("None")]
    [InlineData("warning")]
    public void Load_AcceptsNodeLogLevels(string level)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "  raftPortBase: 41002",
                $"  raftPortBase: 41002\n  nodeLogLevel: {level}",
                StringComparison.Ordinal));

        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        Assert.Equal(level, loaded.Manifest.Cluster.NodeLogLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Verbose")]
    [InlineData("3")]
    public void Load_RejectsInvalidNodeLogLevel(string level)
    {
        using var directory = new TemporaryDirectory();
        string renderedLevel = string.IsNullOrEmpty(level) ? "\"\"" : level;
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "  raftPortBase: 41002",
                $"  raftPortBase: 41002\n  nodeLogLevel: {renderedLevel}",
                StringComparison.Ordinal));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains("cluster.nodeLogLevel", error.Message);
    }

    [Theory]
    [InlineData("entrypointPort", "gatewayPort")]
    [InlineData("nodeHttpPortBase", "httpPortBase")]
    public void Load_RejectsRemovedClusterPortNames(string canonicalName, string removedName)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(canonicalName, removedName, StringComparison.Ordinal));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(removedName, error.Message);
    }

    [Fact]
    public void Load_ExpandsEnvironmentAndTemplateOnlyWhenReplicaStarts()
    {
        using var directory = new TemporaryDirectory();
        const string variable = "SLIMFAAS_LOCAL_TEST_VALUE";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "Development");
        try
        {
            string path = directory.Write(
                "local.yaml",
                ValidYaml(
                    """
                    environment:
                      ASPNETCORE_ENVIRONMENT: "${SLIMFAAS_LOCAL_TEST_VALUE}"
                      ASPNETCORE_URLS: "http://127.0.0.1:{port}"
                    """));

            LoadedLocalManifest loaded = LocalManifestLoader.Load(path);
            LocalFunctionManifest function = loaded.Manifest.Functions["function-one"];
            Assert.Equal("Development", function.Environment["ASPNETCORE_ENVIRONMENT"]);
            Assert.Equal("http://127.0.0.1:{port}", function.Environment["ASPNETCORE_URLS"]);
            Assert.Equal(
                "http://127.0.0.1:41234",
                LocalManifestLoader.ExpandTemplate(function.Environment["ASPNETCORE_URLS"], 41234, 0));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Load_RejectsMissingEnvironmentVariable()
    {
        using var directory = new TemporaryDirectory();
        const string variable = "SLIMFAAS_LOCAL_TEST_MISSING_7B3E";
        Environment.SetEnvironmentVariable(variable, null);
        string path = directory.Write(
            "local.yaml",
            ValidYaml($"environment:\n      VALUE: \"${{{variable}}}\"\n"));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(variable, error.Message);
    }

    [Fact]
    public void Load_RejectsInvalidEnvironmentFileWithoutEchoingItsContents()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write("local.yaml", ValidYaml());
        const string invalid = "sensitive-value-without-an-equals-sign";
        string environmentPath = directory.Write("invalid.env", invalid);

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load([path], [environmentPath]));

        Assert.Contains(Path.GetFullPath(environmentPath), error.Message);
        Assert.Contains("line 1", error.Message);
        Assert.DoesNotContain(invalid, error.Message);
    }

    [Fact]
    public void Load_RejectsOverlappingClusterAndProcessPorts()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace("from: 41100", "from: 41001", StringComparison.Ordinal));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains("must not overlap", error.Message);
    }

    [Fact]
    public void Load_AcceptsDebugUrlWithExplicitPortAndBasePathWithoutCommand()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml()
                .Replace(
                    "    command: [\"dotnet\", \"--info\"]\n    workingDirectory: .\n",
                    "    debugUrl: \"http://127.0.0.1:5051/debug/base\"\n",
                    StringComparison.Ordinal));

        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        LocalFunctionManifest function = loaded.Manifest.Functions["function-one"];
        Assert.Equal("http://127.0.0.1:5051/debug/base", function.DebugUrl);
        Assert.Empty(function.Command);
    }

    [Theory]
    [InlineData("https://127.0.0.1:5051", "absolute HTTP URL")]
    [InlineData("http://127.0.0.1", "explicit port")]
    [InlineData("http://127.0.0.1:0", "port must be between 1 and 65535")]
    [InlineData("127.0.0.1:5051", "absolute HTTP URL")]
    [InlineData("http://127.0.0.1:5051/debug?value=1", "must not contain a query")]
    [InlineData("http://127.0.0.1:5051/debug#section", "must not contain a fragment")]
    public void Load_RejectsInvalidDebugUrls(string debugUrl, string expectedMessage)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "    command: [\"dotnet\", \"--info\"]",
                $"    debugUrl: \"{debugUrl}\"",
                StringComparison.Ordinal));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData(41000)]
    [InlineData(41001)]
    [InlineData(41002)]
    public void Load_RejectsDebugUrlThatUsesAClusterPort(int port)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "    command: [\"dotnet\", \"--info\"]",
                $"    debugUrl: \"http://127.0.0.1:{port}\"",
                StringComparison.Ordinal));

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains("must not overlap entrypoint, node HTTP, or Raft ports", error.Message);
    }

    [Fact]
    public void Load_RejectsFixedProcessPortThatConflictsWithLocalDebugUrl()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "    command: [\"dotnet\", \"--info\"]",
                "    debugUrl: \"http://localhost:5051\"",
                StringComparison.Ordinal) +
            """

            processes:
              frontend:
                command: ["dotnet", "--info"]
                port: 5051
            """);

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains("conflicts with functions.function-one.debugUrl", error.Message);
    }

    [Fact]
    public void Load_ParsesAndMergesAuxiliaryProcessesWithCaseInsensitiveStructuralKeys()
    {
        using var directory = new TemporaryDirectory();
        const string variable = "SLIMFAAS_LOCAL_PROCESS_SECRET_75F2";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "from-process-environment");
        try
        {
            string basePath = directory.Write(
                "base.yaml",
                ValidYaml() +
                """

                processes:
                  fibonacci-front:
                    command: ["npm", "run", "dev"]
                    workingDirectory: .
                    environment:
                      KEEP: inherited
                      REMOVE: inherited
                      SECRET: "${SLIMFAAS_LOCAL_PROCESS_SECRET_75F2}"
                    port: auto
                    restartPolicy: always
                  removable:
                    command: ["dotnet", "--info"]
                    restartPolicy: never
                """);
            string overlayPath = directory.Write(
                "dev.yaml",
                """
                Processes:
                  FIBONACCI-FRONT:
                    Command: ["npm", "run", "dev", "--", "--port", "{port}"]
                    Environment:
                      REMOVE: null
                      ADDED: overlay
                    RestartPolicy: OnFailure
                  removable: null
                """);

            LoadedLocalManifest loaded = LocalManifestLoader.Load([basePath, overlayPath], []);
            LocalProcessManifest process = loaded.Manifest.Processes["fibonacci-front"];

            Assert.Equal(["npm", "run", "dev", "--", "--port", "{port}"], process.Command);
            Assert.Equal("inherited", process.Environment["KEEP"]);
            Assert.Equal("overlay", process.Environment["ADDED"]);
            Assert.Equal("from-process-environment", process.Environment["SECRET"]);
            Assert.False(process.Environment.ContainsKey("REMOVE"));
            Assert.Equal("auto", process.Port);
            Assert.Equal("OnFailure", process.RestartPolicy);
            Assert.False(loaded.Manifest.Processes.ContainsKey("removable"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Load_AcceptsAnIntegerAuxiliaryProcessPort()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml() +
            """

            processes:
              frontend:
                command: ["npm", "run", "dev", "--", "--port", "{port}"]
                port: 41102
            """);

        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        Assert.Equal("41102", loaded.Manifest.Processes["frontend"].Port);
        Assert.Equal(".", loaded.Manifest.Processes["frontend"].WorkingDirectory);
        Assert.Equal("always", loaded.Manifest.Processes["frontend"].RestartPolicy);
    }

    [Theory]
    [InlineData(
        """
        processes:
          Frontend:
            command: ["dotnet", "--info"]
        """,
        "name must start")]
    [InlineData(
        """
        processes:
          frontend:
            command: ["dotnet", "--info"]
            restartPolicy: sometimes
        """,
        "restartPolicy")]
    [InlineData(
        """
        processes:
          frontend:
            command: ["npm", "--port", "{port}"]
        """,
        "does not configure port")]
    [InlineData(
        """
        processes:
          frontend:
            command: ["dotnet", "--info"]
            port: 41000
        """,
        "must not overlap entrypoint")]
    public void Load_RejectsInvalidAuxiliaryProcessConfiguration(
        string processYaml,
        string expectedMessage)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write("local.yaml", $"{ValidYaml()}{Environment.NewLine}{processYaml}");

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains(expectedMessage, error.Message);
    }

    [Fact]
    public void Load_RejectsDuplicateFixedAuxiliaryPorts()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml() +
            """

            processes:
              frontend:
                command: ["dotnet", "--info"]
                port: 42000
              watcher:
                command: ["dotnet", "--info"]
                port: 42000
            """);

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains("duplicates processes.frontend.port", error.Message);
    }

    [Fact]
    public void Load_CountsAutomaticAuxiliaryPortsAgainstPoolCapacity()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.Write(
            "local.yaml",
            ValidYaml() +
            """

            processes:
              frontend:
                command: ["dotnet", "--info"]
                port: auto
              watcher:
                command: ["dotnet", "--info"]
                port: auto
            """);

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalManifestLoader.Load(path));

        Assert.Contains("require 4", error.Message);
    }

    [Fact]
    public void StateStore_RefusesCleanWithoutMarker()
    {
        using var directory = new TemporaryDirectory();
        string state = Path.Combine(directory.Path, "owned-state");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "user-file"), "keep");
        string path = directory.Write(
                "local.yaml",
                ValidYaml().Replace(
                    "mode: ephemeral",
                    $"mode: persistent\n  directory: {state}",
                    StringComparison.Ordinal));
        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        Assert.Throws<LocalManifestException>(() => LocalStateStore.Open(loaded, clean: true));
        Assert.True(File.Exists(Path.Combine(state, "user-file")));
    }

    [Fact]
    public void StateStore_WritesVersionTwoMarkerWithCanonicalPortNames()
    {
        using var directory = new TemporaryDirectory();
        string state = Path.Combine(directory.Path, "owned-state");
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "mode: ephemeral",
                $"mode: persistent\n  directory: {state}",
                StringComparison.Ordinal));
        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        using (LocalStateStore.Open(loaded, clean: false))
        {
        }

        using JsonDocument marker = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(state, LocalStateStore.MarkerFileName)));
        JsonElement root = marker.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(41000, root.GetProperty("entrypointPort").GetInt32());
        Assert.Equal(41001, root.GetProperty("nodeHttpPortBase").GetInt32());
        Assert.False(root.TryGetProperty("gatewayPort", out _));
        Assert.False(root.TryGetProperty("httpPortBase", out _));
    }

    [Fact]
    public void StateStore_RejectsVersionOneMarkerButCleanRecreatesIt()
    {
        using var directory = new TemporaryDirectory();
        string state = Path.Combine(directory.Path, "owned-state");
        Directory.CreateDirectory(state);
        File.WriteAllText(
            Path.Combine(state, LocalStateStore.MarkerFileName),
            """
            {
              "schemaVersion": 1,
              "project": "local-test",
              "nodes": 1,
              "gatewayPort": 41000,
              "httpPortBase": 41001,
              "raftPortBase": 41002,
              "processPortFrom": 41100,
              "processPortTo": 41102,
              "portAllocations": {}
            }
            """);
        string path = directory.Write(
            "local.yaml",
            ValidYaml().Replace(
                "mode: ephemeral",
                $"mode: persistent\n  directory: {state}",
                StringComparison.Ordinal));
        LoadedLocalManifest loaded = LocalManifestLoader.Load(path);

        LocalManifestException error = Assert.Throws<LocalManifestException>(
            () => LocalStateStore.Open(loaded, clean: false));
        Assert.Contains("unsupported schema version 1", error.Message);
        Assert.Contains("--clean", error.Message);

        using (LocalStateStore.Open(loaded, clean: true))
        {
        }

        using JsonDocument marker = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(state, LocalStateStore.MarkerFileName)));
        Assert.Equal(2, marker.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    private static string ValidYaml(string functionTail = "")
    {
        string yaml =
            """
            schemaVersion: 1
            name: local-test
            cluster:
              nodes: 1
              entrypointPort: 41000
              nodeHttpPortBase: 41001
              raftPortBase: 41002
            processPorts:
              from: 41100
              to: 41102
            state:
              mode: ephemeral
            functions:
              function-one:
                command: ["dotnet", "--info"]
                workingDirectory: .
                annotations:
                  SlimFaas/Function: "true"
                  SlimFaas/ReplicasAtStart: "1"
                  SlimFaas/Scale: '{"ReplicaMax": 2}'
            """.ReplaceLineEndings("\n");
        if (string.IsNullOrWhiteSpace(functionTail))
            return yaml;

        string indentedTail = string.Join(
            "\n",
            functionTail.Trim().Split('\n').Select(line => $"    {line.TrimEnd('\r')}"));
        return $"{yaml}\n{indentedTail}\n";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SlimFaas.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
