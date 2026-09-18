using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;
using KubernetesJob = SlimFaas.Kubernetes.Job;

namespace SlimFaas.Tests.Security;

/// <summary>
/// Legacy / Hybrid / Strict behaviour of the two classification points, driven by the
/// feature that <see cref="CallerAuthenticationMiddleware"/> records on the request.
/// </summary>
public class CallerClassificationTests
{
    private const string SlimFaasPodIp = "10.0.0.10";
    private const string TrustedPodIp = "10.0.0.1";
    private const string ExternalIp = "203.0.113.7";

    [Theory]
    [InlineData(TrustedPodIp, true)]
    [InlineData(ExternalIp, false)]
    public void Legacy_WithoutMiddleware_IgnoresSignedHeadersAndUsesTheAddress(string remoteIp, bool expected)
    {
        DefaultHttpContext context = Context(remoteIp);
        context.Request.Headers[RequestSignature.CallerHeader] = "billing-api";
        context.Request.Headers[RequestSignature.SignatureHeader] = "garbage";

        Assert.Equal(expected, Policy().IsInternalRequest(context));
        Assert.Equal(expected, FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(NullLogger.Instance, context, Replicas(), Jobs()));
    }

    [Theory]
    [InlineData(TrustedPodIp, true)]
    [InlineData(ExternalIp, false)]
    public void Legacy_WithMiddlewareFeature_UsesTheAddress(string remoteIp, bool expected)
    {
        DefaultHttpContext context = Context(remoteIp, CallerAuthenticationMode.Legacy, "billing-api");

        Assert.Equal(expected, Policy().IsInternalRequest(context));
        Assert.Equal(expected, FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(NullLogger.Instance, context, Replicas(), Jobs()));
    }

    [Theory]
    [InlineData(CallerAuthenticationMode.Hybrid)]
    [InlineData(CallerAuthenticationMode.Strict)]
    public void VerifiedCaller_IsInternalFromAnyAddress(CallerAuthenticationMode mode)
    {
        DefaultHttpContext context = Context(ExternalIp, mode, "billing-api");

        Assert.True(Policy().IsInternalRequest(context));
        Assert.True(FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(NullLogger.Instance, context, Replicas(), Jobs()));
    }

    [Fact]
    public void Hybrid_UnsignedTrustedAddress_IsInternal_AndWarnsOnce()
    {
        LogThrottle throttle = new(TimeSpan.FromMinutes(1));
        DefaultHttpContext context = Context(TrustedPodIp, CallerAuthenticationMode.Hybrid, null, throttle);
        context.Request.Path = "/function/private-api/compute";
        Mock<ILogger<DefaultFunctionAccessPolicy>> logger = new();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        Assert.True(Policy(logger.Object).IsInternalRequest(context));
        Assert.True(Policy(logger.Object).IsInternalRequest(Context(TrustedPodIp, CallerAuthenticationMode.Hybrid, null, throttle, "/function/private-api/compute")));

        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(TrustedPodIp, StringComparison.Ordinal)
                                              && state.ToString()!.Contains("/function/private-api/compute", StringComparison.Ordinal)),
            null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public void Hybrid_UnsignedExternalAddress_IsExternal()
    {
        DefaultHttpContext context = Context(ExternalIp, CallerAuthenticationMode.Hybrid, null);

        Assert.False(Policy().IsInternalRequest(context));
        Assert.False(FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(NullLogger.Instance, context, Replicas(), Jobs()));
    }

    [Fact]
    public void Strict_UnsignedTrustedAddress_IsExternal()
    {
        DefaultHttpContext context = Context(TrustedPodIp, CallerAuthenticationMode.Strict, null);

        Assert.False(Policy().IsInternalRequest(context));
        Assert.False(FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(NullLogger.Instance, context, Replicas(), Jobs()));
    }

    [Fact]
    public void Strict_UnsignedSlimFaasPeer_StaysInternalForPeerEndpoints()
    {
        DefaultHttpContext context = Context(SlimFaasPodIp, CallerAuthenticationMode.Strict, null);

        Assert.True(FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(NullLogger.Instance, context, Replicas(), Jobs()));
        Assert.False(Policy().IsInternalRequest(context));
    }

    [Fact]
    public void Strict_PrivateFunction_RequiresASignature()
    {
        DefaultFunctionAccessPolicy policy = Policy();
        DeploymentInformation function = Deployments().Functions.Single(f => f.Deployment == "private-api");

        Assert.False(policy.CanAccessFunction(Context(TrustedPodIp, CallerAuthenticationMode.Strict, null), function, "/compute"));
        Assert.True(policy.CanAccessFunction(Context(ExternalIp, CallerAuthenticationMode.Strict, "billing-api"), function, "/compute"));
    }

    [Fact]
    public void GetCallerIdentity_ReturnsTheVerifiedCaller()
    {
        Assert.Equal(new CallerIdentity("billing-api"), Context(ExternalIp, CallerAuthenticationMode.Strict, "billing-api").GetCallerIdentity());
        Assert.Null(Context(ExternalIp, CallerAuthenticationMode.Strict, null).GetCallerIdentity());
        Assert.Null(Context(ExternalIp).GetCallerIdentity());
    }

    [Theory]
    [InlineData("Hybrid", "", "SecretsDirectory")]
    [InlineData("Strict", "", "SecretsDirectory")]
    [InlineData("Paranoid", "/run/secrets", "Mode")]
    public void SlimFaasOptions_ValidationRejectsInvalidCallerAuthentication(string mode, string directory, string expectedInMessage)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SlimFaas:CallerAuthentication:Mode"] = mode,
            ["SlimFaas:CallerAuthentication:SecretsDirectory"] = directory,
        }).Build();
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSlimFaasOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Exception exception = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<IOptions<SlimFaasOptions>>().Value);
        Assert.Contains(expectedInMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SlimFaasOptions_DefaultsToLegacy()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSlimFaasOptions(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        CallerAuthenticationOptions auth = provider.GetRequiredService<IOptions<SlimFaasOptions>>().Value.CallerAuthentication;

        Assert.Equal(CallerAuthenticationMode.Legacy, auth.Mode);
        Assert.Equal(300, auth.ClockSkewSeconds);
        Assert.Equal(4L * 1024L * 1024L, auth.MaxSignedBodyBytes);
    }

    private static DefaultHttpContext Context(
        string remoteIp,
        CallerAuthenticationMode? mode = null,
        string? verifiedCaller = null,
        LogThrottle? throttle = null,
        string path = "/function/private-api")
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Path = path;
        if (mode is not null)
        {
            context.Features.Set(new CallerAuthenticationFeature(mode.Value,
                verifiedCaller is null ? null : new CallerIdentity(verifiedCaller)));
        }

        ServiceCollection services = new();
        services.AddSingleton(throttle ?? new LogThrottle(TimeSpan.Zero));
        context.RequestServices = services.BuildServiceProvider();
        return context;
    }

    private static DefaultFunctionAccessPolicy Policy(ILogger<DefaultFunctionAccessPolicy>? logger = null)
    {
        Mock<IWebSocketFunctionRepository> webSockets = new();
        webSockets.Setup(w => w.GetVirtualDeployments()).Returns(new List<DeploymentInformation>());
        return new DefaultFunctionAccessPolicy(Replicas(), Jobs(), webSockets.Object,
            logger ?? NullLogger<DefaultFunctionAccessPolicy>.Instance);
    }

    private static IReplicasService Replicas()
    {
        Mock<IReplicasService> replicas = new();
        replicas.SetupGet(r => r.Deployments).Returns(Deployments());
        return replicas.Object;
    }

    private static DeploymentsInformations Deployments()
        => new(
            new List<DeploymentInformation>
            {
                new("trusted-api", "default",
                    [new PodInformation("trusted-api-0", true, true, TrustedPodIp, "trusted-api")],
                    new SlimFaasConfiguration(), 1),
                new("private-api", "default",
                    [new PodInformation("private-api-0", true, true, "10.0.0.3", "private-api")],
                    new SlimFaasConfiguration(), 1, Visibility: FunctionVisibility.Private)
            },
            new SlimFaasDeploymentInformation(1,
                [new PodInformation("slimfaas-0", true, true, SlimFaasPodIp, "slimfaas")]),
            new List<PodInformation>());

    private static IJobService Jobs()
    {
        Mock<IJobService> jobs = new();
        jobs.SetupGet(j => j.Jobs).Returns(new List<KubernetesJob>());
        return jobs.Object;
    }
}
