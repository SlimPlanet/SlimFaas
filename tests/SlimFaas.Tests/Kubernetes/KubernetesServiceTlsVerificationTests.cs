using k8s;
using Microsoft.Extensions.Logging;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public class KubernetesServiceTlsVerificationTests
{
    [Fact]
    public void ByDefault_TheApiServerCertificateIsVerified()
    {
        var config = new KubernetesClientConfiguration { Host = "https://kubernetes.default.svc" };
        var logger = new RecordingLogger();

        KubernetesService.ApplyTlsVerification(config, skipTlsVerify: false, logger);

        Assert.False(config.SkipTlsVerify);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void WhenExplicitlyRequested_VerificationIsSkippedAndAWarningIsLogged()
    {
        var config = new KubernetesClientConfiguration { Host = "https://kubernetes.default.svc" };
        var logger = new RecordingLogger();

        KubernetesService.ApplyTlsVerification(config, skipTlsVerify: true, logger);

        Assert.True(config.SkipTlsVerify);
        var entry = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("KubernetesSkipTlsVerify", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKubeconfigThatSkipsVerificationIsLeftUntouched()
    {
        var config = new KubernetesClientConfiguration { Host = "https://localhost:6443", SkipTlsVerify = true };
        var logger = new RecordingLogger();

        KubernetesService.ApplyTlsVerification(config, skipTlsVerify: false, logger);

        Assert.True(config.SkipTlsVerify);
        Assert.Empty(logger.Messages);
    }

    private sealed class RecordingLogger : ILogger<KubernetesService>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }
}
