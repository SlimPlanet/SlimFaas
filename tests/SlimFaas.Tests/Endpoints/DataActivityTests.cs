using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DotNext;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Options;
using SlimFaas.Security;

namespace SlimFaas.Tests.Endpoints;

public sealed class DataActivityTests
{
    [Theory]
    [InlineData("GET", "/data/sets", "sets", "list", 200)]
    [InlineData("GET", "/data/sets/key", "sets", "read", 404)]
    [InlineData("POST", "/data/sets/key", "sets", "write", 200)]
    [InlineData("POST", "/data/sets/key/incr", "sets", "incr", 200)]
    [InlineData("POST", "/data/sets/key/incrby", "sets", "incrby", 409)]
    [InlineData("POST", "/data/sets/key/incrbyfloat", "sets", "incrbyfloat", 200)]
    [InlineData("POST", "/data/sets/key/decr", "sets", "decr", 200)]
    [InlineData("POST", "/data/sets/key/decrby", "sets", "decrby", 400)]
    [InlineData("DELETE", "/data/sets/key", "sets", "delete", 204)]
    [InlineData("POST", "/data/files", "files", "write", 200)]
    [InlineData("GET", "/data/files", "files", "list", 200)]
    [InlineData("GET", "/data/files/key", "files", "read", 503)]
    [InlineData("DELETE", "/data/files/key", "files", "delete", 204)]
    public async Task CallsKeepOperationOutcomeAndCausalIdentity(string method, string path, string kind, string operation, int status)
    {
        var tracker = new NetworkActivityTracker();
        var context = new DefaultHttpContext();
        context.Request.Method = method; context.Request.Path = path;
        if (operation != "list" && !(kind == "files" && operation == "write")) context.Request.RouteValues["id"] = "key";
        var filter = new DataActivityEndpointFilter(tracker, new Mock<IJobService>().Object,
            new MemoryReplicasService(), Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()));
        var invocation = new DefaultEndpointFilterInvocationContext(context);
        await filter.InvokeAsync(invocation, _ =>
        {
            var ingress = Assert.Single(tracker.GetRecent());
            Assert.Equal("data_request", ingress.Type);
            return ValueTask.FromResult<object?>(Results.StatusCode(status));
        });
        var events = tracker.GetRecent();
        Assert.Equal(2, events.Count);
        Assert.Equal(kind, events[1].DataKind); Assert.Equal(operation, events[1].DataOperation);
        Assert.Equal(status, events[1].DataStatusCode); Assert.Equal(events[0].Id, events[1].CorrelationId);
        Assert.Equal("external", events[0].Source);
        var peer = new NetworkActivityTracker();
        Assert.Equal(2, peer.IngestRemote(events)); Assert.Equal(0, peer.IngestRemote(events));
        var json = JsonSerializer.Serialize(events.ToList(), StatusStreamSerializerContext.Default.ListNetworkActivityEvent);
        Assert.DoesNotContain("/data/", json); Assert.DoesNotContain("key", json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedLocalCallStartsAtExactFunctionReplicaOrJobExecution(bool job)
    {
        const string token = "test-token";
        string name = job ? "report-slimfaas-job-run1" : "fibonacci-replica-1";
        var context = new DefaultHttpContext(); context.Request.Method = "GET"; context.Request.Path = "/data/sets";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers[job ? LocalWorkloadGateway.JobHeaderName : LocalWorkloadGateway.PodHeaderName] = name;
        context.Request.Headers[job ? LocalWorkloadGateway.SignatureHeaderName : LocalWorkloadGateway.PodSignatureHeaderName] =
            job ? LocalWorkloadGateway.CreateSignature(name, token) : LocalWorkloadGateway.CreatePodSignature(name, token);
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(service => service.Deployments).Returns(new DeploymentsInformations(
            [new DeploymentInformation("fibonacci", "default", [new(name, true, true, "127.0.0.1", "fibonacci")], new SlimFaasConfiguration(), Replicas: 1)],
            new SlimFaasDeploymentInformation(1, []), []));
        var tracker = new NetworkActivityTracker();
        var options = new SlimFaasOptions(); options.Process.Token = token;
        var filter = new DataActivityEndpointFilter(tracker, new Mock<IJobService>().Object, replicas.Object,
            Microsoft.Extensions.Options.Options.Create(options));
        await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(context), _ => ValueTask.FromResult<object?>(Results.Ok()));
        Assert.All(tracker.GetRecent(), activity =>
        {
            Assert.Equal(job ? "report" : "fibonacci", activity.Source);
            Assert.Equal(name, activity.SourcePod);
            Assert.Equal(name, StatusStreamPrivacy.ForBrowser(activity).SourcePod);
        });
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void MetadataAndActivityFollowTheSameAccessPolicy(bool expose, bool isPublic, bool isInternal, bool expected)
    {
        var access = new Mock<IFunctionAccessPolicy>(); access.Setup(item => item.IsInternalRequest(It.IsAny<HttpContext>())).Returns(isInternal);
        var state = new Mock<ISupplier<SlimDataPayload>>();
        state.Setup(item => item.Invoke()).Returns(new SlimDataPayload { KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty });
        var services = new ServiceCollection();
        services.Configure<SlimFaasOptions>(options => options.ExposeDataMetadata = expose);
        services.Configure<DataOptions>(options => options.DefaultVisibility = isPublic ? FunctionVisibility.Public : FunctionVisibility.Private);
        services.AddSingleton(access.Object); services.AddSingleton(state.Object); services.AddSingleton<DataStatusSnapshotCache>();
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        var activity = new NetworkActivityEvent("data-1", "data_request", "external", "slimfaas", null, 0, "node", DataKind: "sets");
        var payload = new StatusStreamPayload([], [], [], [activity]);
        var projected = DataObservation.Project(context, payload);
        Assert.Equal(expected, DataObservation.Visible(context, activity));
        Assert.Equal(expected, projected.DataSummary is not null);
        Assert.Equal(expected ? 1 : 0, projected.RecentActivity.Count);
        context.Request.QueryString = new QueryString("?activity=false");
        Assert.Null(DataObservation.Project(context, payload).DataSummary);
        if (!expected) state.Verify(item => item.Invoke(), Times.Never);
    }
}
