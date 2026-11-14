using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Security;

public class DefaultFunctionAccessPolicyTests
{
    private const string TrustedPodIp = "10.0.0.1";
    private const string UntrustedPodIp = "10.0.0.2";
    private const string JobIp = "10.42.0.17";
    private const string BatchImportIp = "10.0.0.5";
    private const string SharedHostIp = "10.0.0.6";
    private const string SlimFaasPodIp = "10.0.0.7";
    private const string ProcessToken = "process-token";

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

    [Theory]
    [InlineData(TrustedPodIp, "trusted-api")]
    [InlineData("::ffff:10.0.0.1", "trusted-api")]
    [InlineData(UntrustedPodIp, "untrusted-api")]
    [InlineData(JobIp, "daily-report")]
    [InlineData(SharedHostIp, null)]
    [InlineData(SlimFaasPodIp, null)]
    [InlineData("203.0.113.7", null)]
    [InlineData("127.0.0.1", null)]
    public void ResolveCallerIdentity_NamesTheSingleWorkloadOwningTheConnectionAddress(string remoteIp, string? expected)
    {
        var policy = CreatePolicy();

        Assert.Equal(expected, policy.ResolveCallerIdentity(CreateContext(remoteIp)));
    }

    [Fact]
    public void ResolveCallerIdentity_IgnoresForgedXForwardedFor()
    {
        var policy = CreatePolicy();
        var context = CreateContext("203.0.113.7");
        context.Request.Headers["X-Forwarded-For"] = TrustedPodIp;

        Assert.Null(policy.ResolveCallerIdentity(context));
    }

    [Fact]
    public void ResolveCallerIdentity_UsesTheSignedIdentityOfALocalFunctionPod()
    {
        var policy = CreatePolicy();
        var context = CreateContext("127.0.0.1");
        context.Request.Headers[LocalWorkloadGateway.PodHeaderName] = "trusted-api-0";
        context.Request.Headers[LocalWorkloadGateway.PodSignatureHeaderName] =
            LocalWorkloadGateway.CreatePodSignature("trusted-api-0", ProcessToken);

        Assert.Equal("trusted-api", policy.ResolveCallerIdentity(context));
    }

    [Fact]
    public void ResolveCallerIdentity_UsesTheSignedIdentityOfALocalJob()
    {
        var policy = CreatePolicy();
        var context = CreateContext("127.0.0.1");
        context.Request.Headers[LocalWorkloadGateway.JobHeaderName] = "nightly-slimfaas-job-7";
        context.Request.Headers[LocalWorkloadGateway.SignatureHeaderName] =
            LocalWorkloadGateway.CreateSignature("nightly-slimfaas-job-7", ProcessToken);

        Assert.Equal("nightly", policy.ResolveCallerIdentity(context));
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    public void ResolveCallerIdentity_RejectsALocalIdentityWithoutAValidSignature(string signingToken)
    {
        var policy = CreatePolicy();
        var context = CreateContext("127.0.0.1");
        context.Request.Headers[LocalWorkloadGateway.PodHeaderName] = "trusted-api-0";
        context.Request.Headers[LocalWorkloadGateway.PodSignatureHeaderName] =
            signingToken.Length == 0 ? "" : LocalWorkloadGateway.CreatePodSignature("trusted-api-0", signingToken);

        Assert.Null(policy.ResolveCallerIdentity(context));
    }

    [Fact]
    public void ResolveCallerIdentity_IgnoresLocalIdentitiesWhenNoProcessTokenIsConfigured()
    {
        var policy = CreatePolicy(processToken: "");
        var context = CreateContext("127.0.0.1");
        context.Request.Headers[LocalWorkloadGateway.PodHeaderName] = "trusted-api-0";
        context.Request.Headers[LocalWorkloadGateway.PodSignatureHeaderName] =
            LocalWorkloadGateway.CreatePodSignature("trusted-api-0", "");

        Assert.Null(policy.ResolveCallerIdentity(context));
    }

    [Theory]
    [InlineData(TrustedPodIp, true)]
    [InlineData(BatchImportIp, true)]
    [InlineData(JobIp, true)]
    [InlineData(UntrustedPodIp, false)]
    [InlineData(SharedHostIp, false)]
    [InlineData("203.0.113.7", false)]
    public void IsAllowedCaller_MatchesTheCallerAgainstTheList(string remoteIp, bool expected)
    {
        var policy = CreatePolicy();
        var function = Function(["trusted-api", "batch-*", "daily-report"]);

        Assert.Equal(expected, policy.IsAllowedCaller(CreateContext(remoteIp), function));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsAllowedCaller_WithoutAList_AcceptsEveryCaller(bool emptyList)
    {
        var policy = CreatePolicy();
        var function = Function(emptyList ? [] : null);

        Assert.True(policy.IsAllowedCaller(CreateContext("203.0.113.7"), function));
        Assert.True(policy.IsAllowedCaller(CreateContext(SharedHostIp), function));
    }

    [Theory]
    [InlineData("frontend", "frontend", true)]
    [InlineData("frontend", "frontend-v2", false)]
    [InlineData("frontend", "Frontend", false)]
    [InlineData("batch-*", "batch-import", true)]
    [InlineData("batch-*", "batch-", true)]
    [InlineData("batch-*", "batch", false)]
    [InlineData("*-worker", "image-worker", true)]
    [InlineData("*-worker", "image-worker-2", false)]
    [InlineData("api-*-v*", "api-orders-v2", true)]
    [InlineData("api-*-v*", "api-orders", false)]
    [InlineData("a*b*c", "aXbYbZc", true)]
    [InlineData("*", "anything", true)]
    [InlineData("**", "anything", true)]
    public void MatchesCallerPattern_SupportsWildcards(string pattern, string caller, bool expected)
    {
        Assert.Equal(expected, DefaultFunctionAccessPolicy.MatchesCallerPattern(pattern, caller));
    }

    [Theory]
    [InlineData(TrustedPodIp, new[] { "public-subscriber", "private-subscriber", "restricted-subscriber" })]
    [InlineData(JobIp, new[] { "public-subscriber", "private-subscriber" })]
    [InlineData("203.0.113.7", new[] { "public-subscriber" })]
    public void GetAllowedSubscribers_HonoursTheAllowedCallersOfPrivateSubscriptions(string remoteIp, string[] expected)
    {
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(r => r.Deployments).Returns(new DeploymentsInformations(
            new List<DeploymentInformation>
            {
                Deployments().Functions.Single(f => f.Deployment == "trusted-api"),
                Subscriber("public-subscriber", FunctionVisibility.Public, ["nobody"]),
                Subscriber("private-subscriber", FunctionVisibility.Private, null),
                Subscriber("restricted-subscriber", FunctionVisibility.Private, ["trusted-*"])
            },
            new SlimFaasDeploymentInformation(1, []),
            new List<PodInformation>()));
        var policy = CreatePolicy(replicas);

        var subscribers = policy.GetAllowedSubscribers(CreateContext(remoteIp), "invoice-paid");

        Assert.Equal(expected, subscribers.Select(s => s.Deployment));
    }

    private static DeploymentInformation Subscriber(string name, FunctionVisibility visibility, IList<string>? allowedCallers)
        => new(name, "default", [], new SlimFaasConfiguration(), 1,
            SubscribeEvents: [new SubscribeEvent("invoice-paid", visibility)],
            AllowedCallers: allowedCallers);

    private static DeploymentInformation Function(IList<string>? allowedCallers)
        => new("payments", "default", [], new SlimFaasConfiguration(), 1,
            Visibility: FunctionVisibility.Private, AllowedCallers: allowedCallers);

    private static DefaultFunctionAccessPolicy CreatePolicy(Mock<IReplicasService>? replicas = null,
        string processToken = ProcessToken)
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

        var options = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            Process = new ProcessOrchestratorOptions { Token = processToken }
        });

        return new DefaultFunctionAccessPolicy(replicas.Object, jobs.Object, webSockets.Object, options,
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
                    new SlimFaasConfiguration(), 1, Visibility: FunctionVisibility.Private),
                new("batch-import", "default",
                    [new PodInformation("batch-import-0", true, true, BatchImportIp, "batch-import")],
                    new SlimFaasConfiguration(), 1),
                new("host-network-a", "default",
                    [new PodInformation("host-network-a-0", true, true, SharedHostIp, "host-network-a")],
                    new SlimFaasConfiguration(), 1),
                new("host-network-b", "default",
                    [new PodInformation("host-network-b-0", true, true, SharedHostIp, "host-network-b")],
                    new SlimFaasConfiguration(), 1),
                new("slimfaas-host-network", "default",
                    [new PodInformation("slimfaas-host-network-0", true, true, SlimFaasPodIp, "slimfaas-host-network")],
                    new SlimFaasConfiguration(), 1)
            },
            new SlimFaasDeploymentInformation(1, [new PodInformation("slimfaas-0", true, true, SlimFaasPodIp, "slimfaas")]),
            new List<PodInformation>());

    private static DefaultHttpContext CreateContext(string remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return context;
    }
}
