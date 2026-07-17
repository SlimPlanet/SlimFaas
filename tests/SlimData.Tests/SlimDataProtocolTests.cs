using System.Net;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimData.Commands;
using SlimData.Options;

namespace SlimData.Tests;

public sealed class SlimDataProtocolTests
{
    [Fact]
    public async Task Protocol_endpoint_returns_the_current_protocol_and_assembly_version()
    {
        var services = new ServiceCollection()
            .AddSingleton(new SlimDataInfo(3262))
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        context.Connection.LocalPort = 3262;

        await Endpoints.ProtocolAsync(context);

        context.Response.Body.Position = 0L;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Equal(SlimDataCommandProtocol.Current, await reader.ReadToEndAsync());
        Assert.Equal(
            SlimDataCommandProtocol.Current,
            context.Response.Headers[SlimDataCommandProtocol.HeaderName].ToString());
        Assert.Equal(
            SlimDataCommandProtocol.AssemblyVersion,
            context.Response.Headers[SlimDataCommandProtocol.AssemblyVersionHeaderName].ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SLDC/0")]
    public async Task Membership_announcement_rejects_an_incompatible_protocol(string? protocol)
    {
        var services = new ServiceCollection()
            .AddSingleton(new SlimDataInfo(3262))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.LocalPort = 3262;
        if (protocol is not null)
            context.Request.Headers[SlimDataCommandProtocol.HeaderName] = protocol;

        await Endpoints.AnnounceMemberAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Equal(
            SlimDataCommandProtocol.Current,
            context.Response.Headers[SlimDataCommandProtocol.HeaderName].ToString());
        Assert.Equal(
            SlimDataCommandProtocol.AssemblyVersion,
            context.Response.Headers[SlimDataCommandProtocol.AssemblyVersionHeaderName].ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.0+other-build")]
    public async Task Membership_announcement_accepts_a_compatible_protocol_from_another_build(
        string? assemblyVersion)
    {
        var services = new ServiceCollection()
            .AddSingleton(new SlimDataInfo(3262))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.LocalPort = 3262;
        context.Request.Headers[SlimDataCommandProtocol.HeaderName] = SlimDataCommandProtocol.Current;
        if (assemblyVersion is not null)
            context.Request.Headers[SlimDataCommandProtocol.AssemblyVersionHeaderName] = assemblyVersion;

        await Endpoints.AnnounceMemberAsync(context);

        // The request reaches endpoint validation; it is rejected only because this unit request
        // intentionally omits the candidate endpoint query parameter.
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Data_endpoint_returns_503_without_invoking_the_mutation_when_protocol_is_incompatible()
    {
        var protocol = new Mock<ISlimDataProtocolCompatibility>(MockBehavior.Strict);
        protocol.SetupGet(x => x.IsCompatible).Returns(false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new SlimDataInfo(3262));
        services.AddSingleton(protocol.Object);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Connection.LocalPort = 3262;
        var invoked = false;

        await Endpoints.DoAsync(context, (_, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "", null, false)]
    [InlineData(HttpStatusCode.OK, "SLDC/0", "SLDC/0", false)]
    [InlineData(HttpStatusCode.OK, "SLDC/1", null, false)]
    [InlineData(HttpStatusCode.OK, "SLDC/1", "SLDC/1", true)]
    public async Task Protocol_probe_requires_a_successful_endpoint_body_and_header(
        HttpStatusCode status,
        string body,
        string? header,
        bool expected)
    {
        var factory = CreateHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (header is not null)
            {
                response.Headers.TryAddWithoutValidation(SlimDataCommandProtocol.HeaderName, header);
                response.Headers.TryAddWithoutValidation(
                    SlimDataCommandProtocol.AssemblyVersionHeaderName,
                    SlimDataCommandProtocol.AssemblyVersion);
            }
            return response;
        });

        var result = await SlimDataProtocolClient.ProbeAsync(
            factory,
            new Uri("http://localhost:3263/"),
            CancellationToken.None);

        Assert.Equal(expected, result.IsCompatible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.0+different-commit")]
    public async Task Protocol_probe_accepts_a_different_or_missing_assembly_build(string? assemblyVersion)
    {
        var factory = CreateHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SlimDataCommandProtocol.Current)
            };
            response.Headers.TryAddWithoutValidation(
                SlimDataCommandProtocol.HeaderName,
                SlimDataCommandProtocol.Current);
            if (assemblyVersion is not null)
            {
                response.Headers.TryAddWithoutValidation(
                    SlimDataCommandProtocol.AssemblyVersionHeaderName,
                    assemblyVersion);
            }
            return response;
        });

        var result = await SlimDataProtocolClient.ProbeAsync(
            factory,
            new Uri("http://localhost:3263/"),
            CancellationToken.None);

        Assert.True(result.IsCompatible);
        Assert.False(result.IsUnavailable);
        Assert.Equal(assemblyVersion, result.AssemblyVersion);
    }

    [Fact]
    public async Task Protocol_probe_treats_an_unreachable_node_as_unavailable()
    {
        var factory = CreateHttpClientFactory(_ => throw new HttpRequestException("connection refused"));

        var result = await SlimDataProtocolClient.ProbeAsync(
            factory,
            new Uri("http://localhost:3263/"),
            CancellationToken.None);

        Assert.False(result.IsCompatible);
        Assert.True(result.IsUnavailable);
        Assert.Contains("unreachable", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Protocol_probe_treats_transient_http_errors_as_unavailable(HttpStatusCode statusCode)
    {
        var factory = CreateHttpClientFactory(_ => new HttpResponseMessage(statusCode));

        var result = await SlimDataProtocolClient.ProbeAsync(
            factory,
            new Uri("http://localhost:3263/"),
            CancellationToken.None);

        Assert.False(result.IsCompatible);
        Assert.True(result.IsUnavailable);
    }

    [Fact]
    public async Task Membership_coordinator_does_not_add_an_incompatible_candidate()
    {
        var cluster = new Mock<IRaftHttpCluster>(MockBehavior.Strict);
        cluster.SetupGet(x => x.LocalMemberAddress).Returns(new Uri("http://localhost:3262/"));
        cluster.As<IRaftCluster>()
            .SetupGet(x => x.Members)
            .Returns(Array.Empty<IRaftClusterMember>());
        var factory = CreateHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var coordinator = new ClusterMembershipCoordinator(
            cluster.Object,
            factory,
            Microsoft.Extensions.Options.Options.Create(new SlimDataMembershipOptions()),
            NullLogger<ClusterMembershipCoordinator>.Instance);

        var added = await coordinator.AddMemberAsync(
            new Uri("http://localhost:3263/"),
            CancellationToken.None);

        Assert.False(added);
        cluster.Verify(
            x => x.AddMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Concurrent_membership_additions_are_serialized_and_idempotent()
    {
        var endpoint = new Uri("http://localhost:3263/");
        var members = new List<IRaftClusterMember>();
        var auditTrail = new ConsensusOnlyState();

        var addStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cluster = new Mock<IRaftHttpCluster>(MockBehavior.Strict);
        cluster.SetupGet(x => x.LocalMemberAddress).Returns(new Uri("http://localhost:3262/"));
        cluster.SetupGet(x => x.AuditTrail).Returns(auditTrail);
        cluster.As<IRaftCluster>()
            .SetupGet(x => x.Members)
            .Returns(() => members.ToArray());
        cluster.Setup(x => x.AddMemberAsync(endpoint, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                addStarted.SetResult();
                await releaseAdd.Task;
                var member = new Mock<IRaftClusterMember>(MockBehavior.Strict);
                member.SetupGet(x => x.EndPoint).Returns(new UriEndPoint(endpoint));
                members.Add(member.Object);
                return true;
            });

        var factory = CreateHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SlimDataCommandProtocol.Current)
            };
            response.Headers.TryAddWithoutValidation(
                SlimDataCommandProtocol.HeaderName,
                SlimDataCommandProtocol.Current);
            response.Headers.TryAddWithoutValidation(
                SlimDataCommandProtocol.AssemblyVersionHeaderName,
                "1.0.0+rolling-update-build");
            return response;
        });
        var coordinator = new ClusterMembershipCoordinator(
            cluster.Object,
            factory,
            Microsoft.Extensions.Options.Options.Create(new SlimDataMembershipOptions()),
            NullLogger<ClusterMembershipCoordinator>.Instance);

        var announceAddition = coordinator.AddMemberAsync(endpoint, CancellationToken.None);
        await addStarted.Task;
        var reconciliationAddition = coordinator.AddMemberAsync(endpoint, CancellationToken.None);
        releaseAdd.SetResult();

        Assert.True(await announceAddition);
        Assert.True(await reconciliationAddition);
        cluster.Verify(
            x => x.AddMemberAsync(endpoint, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Local_leader_remains_compatible_while_a_follower_is_temporarily_unavailable()
    {
        var member = new Mock<IRaftClusterMember>(MockBehavior.Strict);
        member.SetupGet(x => x.EndPoint).Returns(new UriEndPoint(new Uri("http://localhost:3263/")));
        var cluster = new Mock<IRaftHttpCluster>(MockBehavior.Strict);
        cluster.SetupGet(x => x.LeadershipToken).Returns(CancellationToken.None);
        cluster.SetupGet(x => x.LocalMemberAddress).Returns(new Uri("http://localhost:3262/"));
        cluster.As<IRaftCluster>()
            .SetupGet(x => x.Members)
            .Returns([member.Object]);
        var factory = CreateHttpClientFactory(_ => throw new HttpRequestException("pod restarting"));
        var compatibility = new SlimDataProtocolCompatibility(
            NullLogger<SlimDataProtocolCompatibility>.Instance);
        var worker = new SlimDataProtocolCompatibilityWorker(
            cluster.Object,
            factory,
            compatibility,
            NullLogger<SlimDataProtocolCompatibilityWorker>.Instance);

        await worker.CheckAsync(CancellationToken.None);

        Assert.True(compatibility.IsCompatible);
        Assert.Contains("UnavailableMembers=1", compatibility.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_leader_is_incompatible_when_an_existing_member_uses_another_protocol()
    {
        var member = new Mock<IRaftClusterMember>(MockBehavior.Strict);
        member.SetupGet(x => x.EndPoint).Returns(new UriEndPoint(new Uri("http://localhost:3263/")));
        var cluster = new Mock<IRaftHttpCluster>(MockBehavior.Strict);
        cluster.SetupGet(x => x.LeadershipToken).Returns(CancellationToken.None);
        cluster.As<IRaftCluster>()
            .SetupGet(x => x.Members)
            .Returns([member.Object]);
        var factory = CreateHttpClientFactory(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("SLDC/0")
            };
            response.Headers.TryAddWithoutValidation(SlimDataCommandProtocol.HeaderName, "SLDC/0");
            return response;
        });
        var compatibility = new SlimDataProtocolCompatibility(
            NullLogger<SlimDataProtocolCompatibility>.Instance);
        var worker = new SlimDataProtocolCompatibilityWorker(
            cluster.Object,
            factory,
            compatibility,
            NullLogger<SlimDataProtocolCompatibilityWorker>.Instance);

        await worker.CheckAsync(CancellationToken.None);

        Assert.False(compatibility.IsCompatible);
        Assert.Contains("Raft member protocol check failed", compatibility.Reason, StringComparison.Ordinal);
    }

    private static IHttpClientFactory CreateHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new TestHttpClientFactory(new StubHttpHandler(responder));

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
