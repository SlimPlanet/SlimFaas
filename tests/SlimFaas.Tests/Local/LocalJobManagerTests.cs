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

    private sealed class JobFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly LocalStateStore _state;

        public JobFixture(List<string> arguments, int backoffLimit)
        {
            _root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
            string statePath = Path.Combine(_root, "state");
            Directory.CreateDirectory(_root);
            string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var job = new LocalJobManifest
            {
                Command = [dotnet, .. arguments],
                WorkingDirectory = _root,
                BackoffLimit = backoffLimit,
                TtlSecondsAfterFinished = 60
            };
            var manifest = new LocalManifest
            {
                Name = "job-test",
                Cluster = new LocalClusterManifest
                {
                    Nodes = 1,
                    GatewayPort = 43000,
                    HttpPortBase = 43001,
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
            Manager = new LocalJobManager(loaded, _state);
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
