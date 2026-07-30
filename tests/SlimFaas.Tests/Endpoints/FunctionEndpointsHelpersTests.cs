using System.Net;
using Microsoft.AspNetCore.Http;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Endpoints;

public class FunctionEndpointsHelpersTests
{
    [Fact(DisplayName = "Network activity caller resolves a running job from the remote IP")]
    public void ResolveNetworkActivityCaller_ResolvesJobFromRemoteIp()
    {
        const string runName = "daily-report-slimfaas-job-a1";
        var context = CreateContext("10.42.0.17");
        var jobService = CreateJobService(runName, "10.42.0.17");

        var caller = FunctionEndpointsHelpers.ResolveNetworkActivityCaller(context, jobService.Object);

        Assert.Equal("daily-report", caller.Actor);
        Assert.Equal(runName, caller.SourcePod);
    }

    [Fact(DisplayName = "Network activity caller normalizes IPv4-mapped addresses")]
    public void ResolveNetworkActivityCaller_ResolvesIpv4MappedJobIp()
    {
        const string runName = "daily-report-slimfaas-job-b2";
        var context = CreateContext("::ffff:10.42.0.18");
        var jobService = CreateJobService(runName, "10.42.0.18");

        var caller = FunctionEndpointsHelpers.ResolveNetworkActivityCaller(context, jobService.Object);

        Assert.Equal("daily-report", caller.Actor);
        Assert.Equal(runName, caller.SourcePod);
    }

    [Fact(DisplayName = "Network activity caller resolves a job from X-Forwarded-For")]
    public void ResolveNetworkActivityCaller_ResolvesJobFromForwardedFor()
    {
        const string runName = "daily-report-slimfaas-job-c3";
        var context = CreateContext("10.42.0.200");
        context.Request.Headers["X-Forwarded-For"] = "10.42.0.19, 10.42.0.201";
        var jobService = CreateJobService(runName, "10.42.0.19");

        var caller = FunctionEndpointsHelpers.ResolveNetworkActivityCaller(context, jobService.Object);

        Assert.Equal("daily-report", caller.Actor);
        Assert.Equal(runName, caller.SourcePod);
    }

    [Fact(DisplayName = "Network activity caller keeps unknown callers external")]
    public void ResolveNetworkActivityCaller_KeepsUnknownCallerExternal()
    {
        var context = CreateContext("10.42.0.99");
        var jobService = CreateJobService(
            "daily-report-slimfaas-job-d4",
            "10.42.0.20");

        var caller = FunctionEndpointsHelpers.ResolveNetworkActivityCaller(context, jobService.Object);

        Assert.Equal(NetworkActivityTracker.Actors.External, caller.Actor);
        Assert.Equal("10.42.0.99", caller.SourcePod);
    }

    private static DefaultHttpContext CreateContext(string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }

    private static Mock<IJobService> CreateJobService(string runName, string ip)
    {
        var jobService = new Mock<IJobService>();
        jobService.SetupGet(service => service.Jobs).Returns(
        [
            new KubernetesJob(
                runName,
                JobStatus.Running,
                [ip],
                [],
                "element-1",
                0,
                0)
        ]);
        return jobService;
    }
}
