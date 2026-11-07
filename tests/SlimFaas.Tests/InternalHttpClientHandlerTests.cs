using System.Net.Security;

namespace SlimFaas.Tests;

public sealed class InternalHttpClientHandlerTests
{
    [Fact]
    public void CreateAlwaysBypassesTheSystemProxy()
    {
        using var handler = InternalHttpClientHandler.Create(allowUnsecureSsl: false);

        Assert.False(handler.UseProxy);
        Assert.True(handler.AllowAutoRedirect);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void CreateCanAllowUnsecureCertificates()
    {
        using var handler = InternalHttpClientHandler.Create(allowUnsecureSsl: true);

        Assert.Same(
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void FunctionConnectionsExpireAfterFiveMinutesWithoutChangingOtherHandlerSettings()
    {
        using var handler = InternalHttpClientHandler.CreateForFunctions(allowUnsecureSsl: false);

        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.True(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void FunctionConnectionsPreserveTheOptInForUnsecureCertificates()
    {
        using var handler = InternalHttpClientHandler.CreateForFunctions(allowUnsecureSsl: true);

        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(handler.SslOptions.RemoteCertificateValidationCallback(
            new object(), null, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
