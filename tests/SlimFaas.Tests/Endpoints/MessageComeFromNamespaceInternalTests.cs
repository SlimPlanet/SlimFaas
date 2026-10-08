using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Endpoints;

public class MessageComeFromNamespaceInternalTests
{
    private const string SlimFaasPodIp = "10.0.0.10";
    private const string TrustedPodIp = "10.0.0.1";
    private const string UntrustedPodIp = "10.0.0.2";
    private const string JobIp = "10.42.0.17";

    [Theory]
    [InlineData(SlimFaasPodIp, true)]
    [InlineData("::ffff:10.0.0.10", true)]
    [InlineData(TrustedPodIp, true)]
    [InlineData(JobIp, true)]
    [InlineData(UntrustedPodIp, false)]
    [InlineData("110.0.0.10", false)]
    [InlineData("10.0.0.100", false)]
    [InlineData("203.0.113.7", false)]
    public void ComparesTheConnectionAddressExactly(string remoteIp, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        Assert.Equal(expected, FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(
            NullLogger.Instance, context, Replicas(), Jobs()));
    }

    [Theory]
    [InlineData(SlimFaasPodIp)]
    [InlineData(TrustedPodIp)]
    [InlineData(JobIp)]
    public void IgnoresForgedXForwardedFor(string forwardedFor)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        Assert.False(FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(
            NullLogger.Instance, context, Replicas(), Jobs()));
    }

    [Fact]
    public void WithoutConnectionAddress_IsExternal()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-For"] = SlimFaasPodIp;

        Assert.False(FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(
            NullLogger.Instance, context, Replicas(), Jobs()));
    }

    private static IReplicasService Replicas()
    {
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(r => r.Deployments).Returns(new DeploymentsInformations(
            new List<DeploymentInformation>
            {
                new("trusted-api", "default",
                    [new PodInformation("trusted-api-0", true, true, TrustedPodIp, "trusted-api")],
                    new SlimFaasConfiguration(), 1),
                new("untrusted-api", "default",
                    [new PodInformation("untrusted-api-0", true, true, UntrustedPodIp, "untrusted-api")],
                    new SlimFaasConfiguration(), 1, Trust: FunctionTrust.Untrusted)
            },
            new SlimFaasDeploymentInformation(1,
                [new PodInformation("slimfaas-0", true, true, SlimFaasPodIp, "slimfaas")]),
            new List<PodInformation>()));
        return replicas.Object;
    }

    private static IJobService Jobs()
    {
        var jobs = new Mock<IJobService>();
        jobs.SetupGet(j => j.Jobs).Returns(new List<KubernetesJob>
        {
            new("daily-report-slimfaas-job-1", JobStatus.Running, [JobIp], [], "element-1", 0, 0)
        });
        return jobs.Object;
    }
}
