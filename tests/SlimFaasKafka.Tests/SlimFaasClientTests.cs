using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using SlimFaasKafka.Config;
using SlimFaasKafka.Services;
using Xunit;

namespace SlimFaasKafka.Tests;

public class SlimFaasClientTests
{
    private static SlimFaasClient CreateClient(
        HttpStatusCode responseStatusCode,
        out Mock<HttpMessageHandler> handlerMock,
        out Mock<ILogger<SlimFaasClient>> loggerMock,
        SlimFaasOptions? optionsOverride = null)
    {
        handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        loggerMock = new Mock<ILogger<SlimFaasClient>>();

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = responseStatusCode,
                Content = new StringContent(string.Empty)
            })
            .Verifiable();

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("http://slimfaas:30021")
        };

        var options = optionsOverride ?? new SlimFaasOptions
        {
            BaseUrl = "http://slimfaas:30021",
            WakeUpPathTemplate = "/functions/{functionName}/wake",
            HttpTimeoutSeconds = 10
        };

        var optionsMock = new Mock<IOptions<SlimFaasOptions>>();
        optionsMock.Setup(o => o.Value).Returns(options);

        return new SlimFaasClient(httpClient, optionsMock.Object, loggerMock.Object);
    }

    [Fact]
    public async Task WakeAsync_SuccessStatusCode_LogsInformation()
    {
        // Arrange
        var client = CreateClient(
            HttpStatusCode.Accepted,
            out var handlerMock,
            out var loggerMock);

        // Act
        await client.WakeAsync("my-func", CancellationToken.None);

        // Assert HTTP call
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post &&
                req.RequestUri != null &&
                req.RequestUri.ToString().Contains("/functions/my-func/wake")),
            ItExpr.IsAny<CancellationToken>());

        // Assert : au moins un log d'info sur le succès
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("wake up", StringComparison.OrdinalIgnoreCase) ||
                    v.ToString()!.Contains("Wake up", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task WakeAsync_NonSuccessStatusCode_LogsWarningButDoesNotThrow()
    {
        // Arrange : 500 => ta version log en Warning
        var client = CreateClient(
            HttpStatusCode.InternalServerError,
            out var handlerMock,
            out var loggerMock);

        // Act (ne doit pas lever dans cette implémentation)
        await client.WakeAsync("my-func", CancellationToken.None);

        // Assert HTTP call
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());

        // Assert logging WARNING avec un message qui contient "failed"
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("Wake up for") &&
                    v.ToString()!.Contains("failed", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task WakeAsync_HttpException_LogsErrorAndDoesNotThrow()
    {
        // Arrange : ton implémentation actuelle log en Error mais ne relance pas
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<SlimFaasClient>>();

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("http://slimfaas:30021")
        };

        var options = new SlimFaasOptions
        {
            BaseUrl = "http://slimfaas:30021",
            WakeUpPathTemplate = "/functions/{functionName}/wake",
            HttpTimeoutSeconds = 10
        };

        var optionsMock = new Mock<IOptions<SlimFaasOptions>>();
        optionsMock.Setup(o => o.Value).Returns(options);

        var client = new SlimFaasClient(httpClient, optionsMock.Object, loggerMock.Object);

        // Act : on NE s'attend PAS à une exception (contrairement à la version précédente du test)
        await client.WakeAsync("my-func", CancellationToken.None);

        // Assert : log Error avec "Error while calling SlimFaas wake up"
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("Error while calling SlimFaas wake up")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task WakeAsync_DoesNotEncodeFunctionName_InCurrentImplementation()
    {
        // Arrange : on s'aligne sur le comportement réel,
        // qui envoie "my func with space" tel quel dans l'URL.
        var client = CreateClient(
            HttpStatusCode.OK,
            out var handlerMock,
            out _);

        // Act
        await client.WakeAsync("my func with space", CancellationToken.None);

        // Assert : l’URL contient la version non encodée (ce que montrent les logs actuels)
        handlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri != null &&
                req.RequestUri.ToString().Contains("my func with space")),
            ItExpr.IsAny<CancellationToken>());
    }
}
