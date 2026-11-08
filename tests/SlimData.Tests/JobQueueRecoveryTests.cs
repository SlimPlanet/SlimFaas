using System.Collections.Immutable;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas;
using SlimFaas.Jobs;
using Xunit;

namespace SlimData.Tests;

public sealed class JobQueueRecoveryTests
{
    [Fact]
    public async Task Accepted_job_remains_retryable_when_its_reserving_worker_disappears()
    {
        RetryInformation? policy = null;
        var database = new Mock<IDatabaseService>();
        database.Setup(service => service.ListLeftPushAsync(
                "Job:task", It.IsAny<byte[]>(), It.IsAny<RetryInformation>(), null))
            .Callback<string, byte[], RetryInformation, string?>((_, _, retry, _) => policy = retry)
            .ReturnsAsync("accepted-job");
        await new JobQueue(database.Object).EnqueueAsync("task", [42]);
        Assert.NotNull(policy);

        const long start = 1;
        long expired = start + ((policy.RetryTimeoutSeconds + policy.Retries[0] + 1L) * TimeSpan.TicksPerSecond);
        var reserved = new QueueElement(new byte[] { 42 }, "accepted-job", start, policy.RetryTimeoutSeconds,
            [.. policy.Retries], [new QueueHttpTryElement(start, "crashed-worker")], [.. policy.HttpStatusRetries]);
        var state = new SlimDataState(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty.Add("Job:task", [reserved]));

        await SlimDataInterpreter.DoListRightPopAsync(new ListRightPopCommand
        {
            Key = "Job:task", Count = 1, NowTicks = expired, IdTransaction = "replacement-1", ReservedIps = []
        }, state);

        var retained = Assert.Single(state.Queues["Job:task"]);
        Assert.Equal(504, Assert.Single(retained.RetryQueueElements).HttpCode);
        await SlimDataInterpreter.DoListRightPopAsync(new ListRightPopCommand
        {
            Key = "Job:task", Count = 1,
            NowTicks = expired + (policy.Retries[0] * TimeSpan.TicksPerSecond) + 1,
            IdTransaction = "replacement-2", ReservedIps = []
        }, state);
        Assert.Equal("replacement-2", Assert.Single(state.Queues["Job:task"]).RetryQueueElements[^1].IdTransaction);
        Assert.Equal(2, retained.NumberOfTries());
    }
}
