using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;
using SlimFaas.WebSocket;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Security;

public class DefaultFunctionAccessPolicyTests
{
    private const string TrustedPodIp = "10.0.0.1";
    private const string UntrustedPodIp = "10.0.0.2";
    private const string JobIp = "10.42.0.17";

    [Theory]
    [InlineData(TrustedPodIp, true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData(JobIp, true)]
    [InlineData(UntrustedPodIp, false)]
    [InlineData("110.0.0.10", false)]
    [InlineData("10.0.0.10", false)]
    [InlineData("10.0.0.100", false)]
    [InlineData("203.0.113.7", false)]
    public void IsInternalRequest_ComparesTheConnectionAddressExactly(string remoteIp, bool expected)
    {
        var policy = CreatePolicy();
        var context = CreateContext(remoteIp);

        Assert.Equal(expected, policy.IsInternalRequest(context));
    }

    [Theory]
    [InlineData(TrustedPodIp)]
    [InlineData(JobIp)]
    [InlineData("10.0.0.1, 203.0.113.7")]
    public void IsInternalRequest_IgnoresForgedXForwardedFor(string forwardedFor)
    {
        var policy = CreatePolicy();
        var context = CreateContext("203.0.113.7");
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        Assert.False(policy.IsInternalRequest(context));
    }

    [Fact]
    public void IsInternalRequest_WithoutConnectionAddress_IsExternalEvenWithXForwardedFor()
    {
        var policy = CreatePolicy();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = TrustedPodIp;

        Assert.False(policy.IsInternalRequest(context));
    }

    [Fact]
    public void IsInternalRequest_IsCachedPerRequest()
    {
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(r => r.Deployments).Returns(Deployments());
        var policy = CreatePolicy(replicas);
        var context = CreateContext(TrustedPodIp);

        Assert.True(policy.IsInternalRequest(context));
        Assert.True(policy.IsInternalRequest(context));

        replicas.VerifyGet(r => r.Deployments, Times.Once);
    }

    [Fact]
    public void CanAccessFunction_PrivateFunction_RequiresAnInternalConnection()
    {
        var policy = CreatePolicy();
        var function = Deployments().Functions.Single(f => f.Deployment == "private-api");

        Assert.True(policy.CanAccessFunction(CreateContext(TrustedPodIp), function, "/compute"));
        Assert.False(policy.CanAccessFunction(CreateContext("203.0.113.7"), function, "/compute"));
    }

    private static DefaultFunctionAccessPolicy CreatePolicy(Mock<IReplicasService>? replicas = null)
    {
        if (replicas is null)
        {
            replicas = new Mock<IReplicasService>();
            replicas.SetupGet(r => r.Deployments).Returns(Deployments());
        }

        var jobs = new Mock<IJobService>();
        jobs.SetupGet(j => j.Jobs).Returns(new List<KubernetesJob>
        {
            new("daily-report-slimfaas-job-1", JobStatus.Running, [JobIp], [], "element-1", 0, 0)
        });

        var webSockets = new Mock<IWebSocketFunctionRepository>();
        webSockets.Setup(w => w.GetVirtualDeployments()).Returns(new List<DeploymentInformation>());

        return new DefaultFunctionAccessPolicy(replicas.Object, jobs.Object, webSockets.Object,
            NullLogger<DefaultFunctionAccessPolicy>.Instance);
    }

    private static DeploymentsInformations Deployments()
        => new(
            new List<DeploymentInformation>
            {
                new("trusted-api", "default",
                    [new PodInformation("trusted-api-0", true, true, TrustedPodIp, "trusted-api")],
                    new SlimFaasConfiguration(), 1),
                new("untrusted-api", "default",
                    [new PodInformation("untrusted-api-0", true, true, UntrustedPodIp, "untrusted-api")],
                    new SlimFaasConfiguration(), 1, Trust: FunctionTrust.Untrusted),
                new("private-api", "default",
                    [new PodInformation("private-api-0", true, true, "10.0.0.3", "private-api")],
                    new SlimFaasConfiguration(), 1, Visibility: FunctionVisibility.Private)
            },
            new SlimFaasDeploymentInformation(1, []),
            new List<PodInformation>());

    private static DefaultHttpContext CreateContext(string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }
}
