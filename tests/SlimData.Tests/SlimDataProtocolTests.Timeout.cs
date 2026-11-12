using System.Net;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed partial class SlimDataProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protocol_probe_bounds_both_headers_and_body_from_a_silent_member(bool blockBody)
    {
        using var handler = new SilentProtocolHandler(blockBody);
        using var stopping = new CancellationTokenSource();
        Task<SlimDataProtocolProbeResult> pending = SlimDataProtocolClient.ProbeAsync(
            new TestHttpClientFactory(handler), new Uri("http://localhost:3263/"), stopping.Token);
        try
        {
            SlimDataProtocolProbeResult result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.IsUnavailable);
            Assert.False(result.IsCompatible);
            Assert.Contains("timed out", result.Reason, StringComparison.Ordinal);
            Assert.False(stopping.IsCancellationRequested);
        }
        finally
        {
            await stopping.CancelAsync();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Protocol_probe_propagates_caller_cancellation()
    {
        using var handler = new SilentProtocolHandler(blockBody: false);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SlimDataProtocolClient.ProbeAsync(
            new TestHttpClientFactory(handler), new Uri("http://localhost:3263/"), stopping.Token));
    }

    [Fact]
    public async Task Newly_elected_leader_checks_healthy_members_after_a_silent_follower()
    {
        var missing = new Mock<IRaftClusterMember>(MockBehavior.Strict);
        missing.SetupGet(member => member.EndPoint).Returns(new UriEndPoint(new Uri("http://localhost:3263/")));
        var healthy = new Mock<IRaftClusterMember>(MockBehavior.Strict);
        healthy.SetupGet(member => member.EndPoint).Returns(new UriEndPoint(new Uri("http://localhost:3264/")));
        var cluster = new Mock<IRaftHttpCluster>(MockBehavior.Strict);
        cluster.SetupGet(value => value.LeadershipToken).Returns(CancellationToken.None);
        cluster.SetupGet(value => value.LocalMemberAddress).Returns(new Uri("http://localhost:3262/"));
        cluster.As<IRaftCluster>().SetupGet(value => value.Members).Returns([missing.Object, healthy.Object]);
        var compatibility = new SlimDataProtocolCompatibility(NullLogger<SlimDataProtocolCompatibility>.Instance);
        using var handler = new SilentProtocolHandler(blockBody: false);
        using var stopping = new CancellationTokenSource();
        var worker = new SlimDataProtocolCompatibilityWorker(cluster.Object, new TestHttpClientFactory(handler),
            compatibility, NullLogger<SlimDataProtocolCompatibilityWorker>.Instance);
        Task pending = worker.CheckAsync(stopping.Token);
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(compatibility.IsCompatible);
            Assert.Contains("UnavailableMembers=1", compatibility.Reason, StringComparison.Ordinal);
            Assert.True(handler.HealthyMemberChecked);
        }
        finally
        {
            await stopping.CancelAsync();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class SilentProtocolHandler(bool blockBody) : HttpMessageHandler
    {
        internal bool HealthyMemberChecked { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Port == 3263)
            {
                if (!blockBody)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SilentProtocolStream()) };
            }
            HealthyMemberChecked = true;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SlimDataCommandProtocol.Current) };
            response.Headers.Add(SlimDataCommandProtocol.HeaderName, SlimDataCommandProtocol.Current);
            return response;
        }
    }

    private sealed class SilentProtocolStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
