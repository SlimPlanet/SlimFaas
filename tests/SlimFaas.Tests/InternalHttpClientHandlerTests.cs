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
}
