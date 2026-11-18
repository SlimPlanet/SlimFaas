using System.Net.Security;

namespace SlimFaas.Tests;

public sealed class InternalHttpClientHandlerTests
{
    [Fact]
    public void BatchConnectionsExpireAfterTwoMinutesAndBypassTheSystemProxy()
    {
        using var handler = InternalHttpClientHandler.CreateForSlimData(allowUnsecureSsl: false);

        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
        Assert.False(handler.UseProxy);
        Assert.True(handler.AllowAutoRedirect);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void BatchConnectionsPreserveTheOptInForUnsecureCertificates()
    {
        using var handler = InternalHttpClientHandler.CreateForSlimData(allowUnsecureSsl: true);

        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(handler.SslOptions.RemoteCertificateValidationCallback(
            new object(), null, null, SslPolicyErrors.RemoteCertificateChainErrors));
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
