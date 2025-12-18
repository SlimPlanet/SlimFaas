using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace SlimFaas.Tests;

public sealed class RaftLeaderPublicPortRedirectFilterTests
{
    [Fact]
    public async Task Follower_redirects_to_leader_on_incoming_public_port()
    {
        // Arrange
        var leaderEp = new HttpEndPoint(new System.Uri("http://leader-pod:3262"));

        var leaderMember = new Mock<IClusterMember>(MockBehavior.Strict);
        leaderMember.SetupGet(m => m.EndPoint).Returns(leaderEp);

        var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
        cluster.Setup(c => c.TryGetLeaseToken(out It.Ref<CancellationToken>.IsAny)).Returns(false);
        cluster.SetupGet(c => c.Leader).Returns(leaderMember.Object);

        var ports = new Mock<ISlimFaasPorts>(MockBehavior.Strict);
        ports.SetupGet(p => p.Ports).Returns(new[] { 3262, 30021, 30022 });

        var logger = Mock.Of<ILogger<RaftLeaderPublicPortRedirectFilter>>();

        var filter = new RaftLeaderPublicPortRedirectFilter(cluster.Object, ports.Object, logger);

        var http = new DefaultHttpContext();
        var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        http.RequestServices = services;
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("any-follower", 30021);
        http.Connection.LocalPort = 30021;
        http.Request.Path = "/data/files";
        http.Request.QueryString = new QueryString("?id=oidc2&ttl=10000");
        http.Response.Body = new MemoryStream();

        var ctx = new TestInvocationContext(http);

        static ValueTask<object?> Next(EndpointFilterInvocationContext _) =>
            ValueTask.FromResult<object?>(Results.Ok("OK"));

        // Act
        var resultObj = await filter.InvokeAsync(ctx, Next);
        Assert.NotNull(resultObj);
        Assert.IsAssignableFrom<IResult>(resultObj);

        await ((IResult)resultObj!).ExecuteAsync(http);

        // Assert
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, http.Response.StatusCode);
        Assert.Equal("http://leader-pod:30021/data/files?id=oidc2&ttl=10000",
            http.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Leader_calls_next_no_redirect()
    {
        // Arrange
        var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
        cluster.Setup(c => c.TryGetLeaseToken(out It.Ref<CancellationToken>.IsAny)).Returns(true);

        var ports = new Mock<ISlimFaasPorts>(MockBehavior.Strict);
        ports.SetupGet(p => p.Ports).Returns(new[] { 3262, 30021 });

        var logger = Mock.Of<ILogger<RaftLeaderPublicPortRedirectFilter>>();

        var filter = new RaftLeaderPublicPortRedirectFilter(cluster.Object, ports.Object, logger);

        var http = new DefaultHttpContext();
        var services = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        http.RequestServices = services;
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("leader", 30021);
        http.Connection.LocalPort = 30021;
        http.Request.Path = "/data/files";
        http.Response.Body = new MemoryStream();

        var ctx = new TestInvocationContext(http);

        // Act
        var resultObj = await filter.InvokeAsync(ctx, _ => ValueTask.FromResult<object?>(Results.Ok("OK")));
        await ((IResult)resultObj!).ExecuteAsync(http);

        // Assert
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Location"));
    }

    private sealed class TestInvocationContext : EndpointFilterInvocationContext
    {
        private readonly object?[] _args = new object?[0];

        public TestInvocationContext(HttpContext httpContext) => HttpContext = httpContext;

        public override HttpContext HttpContext { get; }

        public override object?[] Arguments => _args;

        public override T GetArgument<T>(int index) => (T)_args[index]!;
    }
}
