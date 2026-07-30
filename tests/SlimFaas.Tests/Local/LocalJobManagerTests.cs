using SlimFaas.Kubernetes;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalJobManagerTests
{
    [Fact]
    public async Task CreateAsync_IsIdempotentAndTracksSuccessfulExit()
    {
        await using var fixture = new JobFixture(["--info"], backoffLimit: 0);
        ProcessCreateJobCommand command = fixture.Command("job-full-name");

        await fixture.Manager.CreateAsync(command, CancellationToken.None);
        await fixture.Manager.CreateAsync(command, CancellationToken.None);

        Job job = await fixture.WaitForAsync(JobStatus.Succeeded);
        Assert.Equal("job-full-name", job.Name);
        Assert.Single(fixture.Manager.Snapshot());
    }

    [Fact]
    public async Task FailedProcess_HonorsBackoffLimitAndBecomesFailed()
    {
        await using var fixture = new JobFixture(["definitely-not-a-real-assembly.dll"], backoffLimit: 1);

        await fixture.Manager.CreateAsync(fixture.Command("failed-job"), CancellationToken.None);

        Job job = await fixture.WaitForAsync(JobStatus.Failed);
        Assert.Equal("element-id", job.ElementId);
    }

    [Fact]
    public async Task Configuration_MapsKubernetesJobAnnotations()
    {
        var annotations = new Dictionary<string, string>
        {
            [JobAnnotationNames.Job] = "true",
            [JobAnnotationNames.DefaultVisibility] = "Public",
            [JobAnnotationNames.NumberParallelJob] = "3",
            [JobAnnotationNames.DependsOn] = "function-one,function-two",
            [JobAnnotationNames.Schedules] =
                """[{"Schedule":"*/5 * * * *","Args":["39"],"DependsOn":["function-two"]}]"""
        };
        await using var fixture = new JobFixture(["--info"], backoffLimit: 4, annotations: annotations);

        SlimfaasJob configuration = fixture.Manager.Configuration.Configurations["test-job"];
        Assert.Equal("Public", configuration.Visibility);
        Assert.Equal(3, configuration.NumberParallelJob);
        Assert.Equal(["function-one", "function-two"], configuration.DependsOn);

        ScheduleCreateJob schedule = Assert.Single(fixture.Manager.Configuration.Schedules!["test-job"]);
        Assert.Equal("*/5 * * * *", schedule.Schedule);
        Assert.Equal(["39"], schedule.Args);
        Assert.Equal(["function-two"], schedule.DependsOn);
        Assert.Equal(4, schedule.BackoffLimit);
        Assert.Equal(60, schedule.TtlSecondsAfterFinished);
        Assert.Equal("Never", schedule.RestartPolicy);
    }

    private sealed class JobFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly LocalStateStore _state;

        public JobFixture(
            List<string> arguments,
            int backoffLimit,
            Dictionary<string, string>? annotations = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
            string statePath = Path.Combine(_root, "state");
            Directory.CreateDirectory(_root);
            string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var job = new LocalJobManifest
            {
                Command = [dotnet, .. arguments],
                WorkingDirectory = _root,
                Annotations = annotations ??
                              new Dictionary<string, string>
                              {
                                  [JobAnnotationNames.Job] = "true"
                              },
                BackoffLimit = backoffLimit,
                TtlSecondsAfterFinished = 60
            };
            var manifest = new LocalManifest
            {
                Name = "job-test",
                Cluster = new LocalClusterManifest
                {
                    Nodes = 1,
                    EntrypointPort = 43000,
                    NodeHttpPortBase = 43001,
                    RaftPortBase = 43002
                },
                Jobs = new Dictionary<string, LocalJobManifest> { ["test-job"] = job }
            };
            var loaded = new LoadedLocalManifest(
                manifest,
                Path.Combine(_root, "manifest.yaml"),
                _root,
                statePath,
                IsPersistent: true);
            _state = LocalStateStore.Open(loaded, clean: false);
            Manager = new LocalJobManager(loaded, _state, "test-token");
            Manager.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public LocalJobManager Manager { get; }

        public ProcessCreateJobCommand Command(string fullName)
            => new(
                Namespace: "job-test",
                Name: "test-job",
                CreateJob: new CreateJob(
                    Args: [],
                    BackoffLimit: Manager.Configuration.Configurations["test-job"].BackoffLimit,
                    TtlSecondsAfterFinished: 60),
                ElementId: "element-id",
                JobFullName: fullName,
                InQueueTimestamp: DateTime.UtcNow.Ticks);

        public async Task<Job> WaitForAsync(JobStatus expected)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                Job? job = Manager.Snapshot().SingleOrDefault();
                if (job?.Status == expected)
                    return job;
                await Task.Delay(100);
            }

            Job? last = Manager.Snapshot().SingleOrDefault();
            throw new Xunit.Sdk.XunitException(
                $"Job did not reach {expected}; last status was {last?.Status.ToString() ?? "<missing>"}.");
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            _state.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
