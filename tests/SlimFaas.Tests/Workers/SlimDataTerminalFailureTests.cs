using System.Diagnostics;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Prometheus;
using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Tests.Workers;

public sealed class SlimDataTerminalFailureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Diagnostics_surfaces_terminal_WAL_failure_but_keeps_transient_failures_observable(bool terminal)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SlimDataTerminalFailureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var state = new SlimPersistentState(directory);
            await state.RestoreAsync(CancellationToken.None);
            Exception failure = terminal
                ? new UnreachableException("WAL entered a terminal state", new IOException("disk full"))
                : new IOException("temporary diagnostics failure");
            var cluster = new Mock<IRaftCluster>();
            cluster.SetupGet(value => value.AuditTrail).Throws(failure);
            await using var coordinator = new SlimDataCommandBatchCoordinator(cluster.Object,
                NullLogger<SlimDataCommandBatchCoordinator>.Instance);
            var logger = new FailureLogger();
            using var worker = new SlimFaas.Workers.SlimDataDiagnosticsWorker(state, cluster.Object, coordinator,
                new RaftAppendEntriesCommitIndexGuard(NullLogger<RaftAppendEntriesCommitIndexGuard>.Instance),
                Mock.Of<ISlimDataProtocolCompatibility>(), Mock.Of<IDatabaseService>(),
                new DynamicGaugeService(Metrics.WithCustomRegistry(Metrics.NewCustomRegistry())), logger);
            try
            {
                await worker.StartAsync(CancellationToken.None);
                if (terminal)
                {
                    UnreachableException surfaced = await Assert.ThrowsAsync<UnreachableException>(() =>
                        worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3)));
                    Assert.Same(failure, surfaced);
                }
                else
                {
                    Assert.Same(failure, await logger.Observed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
                    Assert.False(worker.ExecuteTask!.IsCompleted);
                }
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FailureLogger : ILogger<SlimFaas.Workers.SlimDataDiagnosticsWorker>
    {
        internal TaskCompletionSource<Exception> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Observed.TrySetResult(exception);
        }
    }
}
