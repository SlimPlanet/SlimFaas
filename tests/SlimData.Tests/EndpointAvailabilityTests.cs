using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace SlimData.Tests;

public sealed class EndpointAvailabilityTests
{
    [Theory]
    [InlineData("leadership", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("timeout", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("not-leader", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("quorum", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("unavailable", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("client", StatusCodes.Status499ClientClosedRequest)]
    [InlineData("client-and-leadership", StatusCodes.Status499ClientClosedRequest)]
    [InlineData("invalid", StatusCodes.Status400BadRequest)]
    [InlineData("unexpected", StatusCodes.Status500InternalServerError)]
    public async Task Data_endpoint_distinguishes_cluster_unavailability_from_client_cancellation(
        string failure, int expectedStatus)
    {
        await VerifyFailureAsync(failure, expectedStatus, responseStarted: false);
    }

    [Theory]
    [InlineData("leadership")]
    [InlineData("timeout")]
    [InlineData("not-leader")]
    [InlineData("quorum")]
    [InlineData("unavailable")]
    [InlineData("client")]
    public async Task Availability_failure_after_response_start_aborts_the_incomplete_response(string failure)
    {
        await VerifyFailureAsync(failure, StatusCodes.Status200OK, responseStarted: true);
    }

    private static async Task VerifyFailureAsync(string failure, int expectedStatus, bool responseStarted)
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using var state = new SlimPersistentState(directory);
            using var leadership = new CancellationTokenSource();
            using var client = new CancellationTokenSource();
            var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
            cluster.SetupGet(value => value.LeadershipToken).Returns(leadership.Token);
            var protocol = new Mock<ISlimDataProtocolCompatibility>(MockBehavior.Strict);
            protocol.SetupGet(value => value.IsCompatible).Returns(true);
            var logger = new Mock<ILogger>();
            logger.Setup(value => value.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(value => value.CreateLogger("SlimData.Endpoints")).Returns(logger.Object);
            await using ServiceProvider services = new ServiceCollection()
                .AddSingleton(loggerFactory.Object)
                .AddSingleton(new SlimDataInfo(3262))
                .AddSingleton(cluster.Object)
                .AddSingleton(protocol.Object)
                .AddSingleton(state)
                .BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Connection.LocalPort = 3262;
            context.Request.Path = "/SlimData/CommandBatch";
            var lifetime = new Mock<IHttpRequestLifetimeFeature>();
            lifetime.SetupGet(value => value.RequestAborted).Returns(client.Token);
            context.Features.Set(lifetime.Object);
            if (responseStarted)
            {
                var response = new Mock<IHttpResponseFeature>(MockBehavior.Strict);
                response.SetupGet(value => value.HasStarted).Returns(true);
                response.SetupGet(value => value.StatusCode).Returns(StatusCodes.Status200OK);
                context.Features.Set(response.Object);
            }

            await Endpoints.DoAsync(context, (_, _, source) =>
            {
                if (failure is "leadership" or "client-and-leadership")
                    leadership.Cancel();
                if (failure is "client" or "client-and-leadership")
                    client.Cancel();
                if (source!.IsCancellationRequested)
                    return Task.FromCanceled(source.Token);

                return Task.FromException(failure switch
                {
                    "timeout" => new OperationCanceledException("Synthetic server timeout."),
                    "not-leader" => new NotLeaderException(),
                    "quorum" => new QuorumUnreachableException(),
                    "unavailable" => new SlimDataUnavailableException("Synthetic quorum outage."),
                    "invalid" => new InvalidDataException("Synthetic invalid request."),
                    _ => new InvalidOperationException("Synthetic unexpected failure.")
                });
            });

            Assert.Equal(expectedStatus, context.Response.StatusCode);
            lifetime.Verify(value => value.Abort(), responseStarted ? Times.Once() : Times.Never());
            logger.Verify(value => value.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                failure == "unexpected" ? Times.Once() : Times.Never());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
