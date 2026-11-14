using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Workers;

public sealed class WebSocketPeerAuthorizationTests
{
    private static (WebSocketPeerAuthorization Authorization, WebSocketDirectoryOwner Owner,
        WebSocketPeerContext Proof, Mock<IWebSocketClusterDirectory> Directory) Setup()
    {
        var owner = new WebSocketDirectoryOwner
        {
            Node = "owner", Incarnation = Guid.NewGuid().ToString("N"),
            RelaySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ExpiresAtTicks = DateTime.UtcNow.AddSeconds(30).Ticks
        };
        var directory = new Mock<IWebSocketClusterDirectory>();
        directory.SetupGet(service => service.Node).Returns(owner.Node);
        directory.SetupGet(service => service.Incarnation).Returns(owner.Incarnation);
        directory.Setup(service => service.Owner(owner.Node)).Returns(owner);
        directory.SetupGet(service => service.Connections).Returns(
        [
            new WebSocketDirectoryConnection
            {
                Id = Guid.NewGuid().ToString("N"), Node = owner.Node, Incarnation = owner.Incarnation,
                Function = "remote", Fingerprint = new string('A', 64), Configuration = "{}", Ready = true
            }
        ]);
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(service => service.Deployments).Returns(new DeploymentsInformations([],
            new SlimFaasDeploymentInformation(2,
            [
                new PodInformation("ingress", true, true, "127.0.0.1", "slimfaas"),
                new PodInformation("owner", true, true, "127.0.0.1", "slimfaas")
            ]), []));
        var proof = new WebSocketPeerContext("ingress", owner.Node, owner.Incarnation, "sync", "remote",
            new string('A', 64), Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddSeconds(120).Ticks,
            Convert.ToHexString(SHA256.HashData("payload"u8)), null, "request-in");
        return (new WebSocketPeerAuthorization(directory.Object, replicas.Object,
            NullLogger<WebSocketPeerAuthorization>.Instance), owner, proof, directory);
    }

    private static DefaultHttpContext Context(WebSocketPeerContext proof, WebSocketDirectoryOwner owner)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
        var signed = WebSocketPeerAuthorization.Sign(proof, owner);
        context.Request.Headers[WebSocketPeerAuthorization.ContextHeader] = signed.Context;
        context.Request.Headers[WebSocketPeerAuthorization.SignatureHeader] = signed.Signature;
        return context;
    }

    [Fact]
    public void Authenticated_same_ip_peer_is_accepted_only_once()
    {
        var (authorization, owner, proof, _) = Setup();
        var context = Context(proof, owner);
        Assert.Equal(proof, authorization.Authenticate(context, "sync"));
        Assert.Null(authorization.Authenticate(context, "sync"));
        Assert.True(WebSocketPeerAuthorization.MatchesPayload(proof, "payload"u8));
        Assert.False(WebSocketPeerAuthorization.MatchesPayload(proof, "changed"u8));
    }

    [Fact]
    public void Sharing_the_owner_ip_does_not_authorize_forged_or_missing_proofs()
    {
        var (authorization, owner, proof, _) = Setup();
        var context = Context(proof, owner);
        context.Request.Headers[WebSocketPeerAuthorization.SignatureHeader] = Convert.ToBase64String(new byte[32]);
        Assert.Null(authorization.Authenticate(context, "sync"));
        context.Request.Headers.Clear();
        Assert.Null(authorization.Authenticate(context, "sync"));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("incarnation")]
    [InlineData("sender")]
    [InlineData("fingerprint")]
    [InlineData("deadline")]
    [InlineData("operation")]
    public void Valid_signature_does_not_bypass_owner_membership_configuration_or_operation(string invalid)
    {
        var (authorization, owner, proof, _) = Setup();
        proof = invalid switch
        {
            "owner" => proof with { Owner = "another" },
            "incarnation" => proof with { Incarnation = Guid.NewGuid().ToString("N") },
            "sender" => proof with { Sender = "untrusted-job" },
            "fingerprint" => proof with { Fingerprint = new string('B', 64) },
            "deadline" => proof with { DeadlineTicks = DateTime.UtcNow.AddSeconds(-1).Ticks },
            "operation" => proof with { Operation = "event" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        Assert.Null(authorization.Authenticate(Context(proof, owner), "sync"));
    }

    [Fact]
    public void Node_without_local_sockets_discovers_remote_configuration_and_unique_replicas()
    {
        var (_, _, _, directory) = Setup();
        var registry = new WebSocketConnectionRegistry(NullLogger<WebSocketConnectionRegistry>.Instance);
        var repository = new WebSocketFunctionRepository(registry, directory.Object);
        var deployment = Assert.Single(repository.GetVirtualDeployments());
        Assert.Equal("remote", deployment.Deployment);
        Assert.Equal(PodType.WebSocket, deployment.PodType);
        Assert.Equal(1, deployment.Replicas);
        Assert.Equal(Assert.Single(directory.Object.Connections).Id, Assert.Single(deployment.Pods).Ip);
        Assert.Empty(registry.GetRegisteredFunctionNames());
        Assert.Empty(registry.GetConnections("remote"));
    }

    [Fact]
    public void Hop_headers_and_connection_nominated_headers_are_not_delivered_to_clients()
    {
        var headers = WebSocketPeerHeaders.Filter(new(StringComparer.OrdinalIgnoreCase)
        {
            ["Connection"] = ["X-Private-Hop, keep-alive"], ["X-Private-Hop"] = ["hidden"],
            ["Transfer-Encoding"] = ["chunked"], [WebSocketPeerAuthorization.SignatureHeader] = ["hidden"],
            ["Content-Type"] = ["application/octet-stream"], ["Set-Cookie"] = ["first=1", "second=2"]
        });
        Assert.Equal(2, headers.Count);
        Assert.Equal("application/octet-stream", Assert.Single(headers["Content-Type"]));
        Assert.Equal(2, headers["Set-Cookie"].Length);
    }
}
