using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests;

public sealed class SendClientDeliveryTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_function_messages_keep_the_current_http11_policy_when_client_defaults_prefer_http2(bool synchronous)
    {
        var requests = new List<(Version Version, HttpVersionPolicy Policy)>();
        using var http = new HttpClient(new HttpMessageHandlerStub((request, _) =>
        {
            requests.Add((request.Version, request.VersionPolicy));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        var sender = Sender(http, new RecordingLogger(), "http://function:8080");
        if (synchronous)
        {
            using var body = new MemoryStream([1]);
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Body = body;
            using HttpResponseMessage response = await sender.SendHttpRequestSync(
                context, "function", "/work", string.Empty, new SlimFaasSyncConfiguration());
            Assert.True(body.CanRead);
        }
        else
        {
            using HttpResponseMessage response = await sender.SendHttpRequestAsync(Request(), OneAttempt());
        }

        Assert.Equal((HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower), Assert.Single(requests));
    }

    [Fact]
    public async Task No_destination_is_proven_before_transport_and_does_not_send_or_retry()
    {
        int transmissions = 0;
        using var http = new HttpClient(new HttpMessageHandlerStub((_, _) =>
        {
            Interlocked.Increment(ref transmissions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var logger = new RecordingLogger();
        var sender = Sender(http, logger, "http://{pod_ip}:{pod_port}");
        var proxy = new Mock<IProxy>();
        proxy.Setup(value => value.GetNextIP()).Returns(string.Empty);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendHttpRequestAsync(
            Request(), OneAttempt(), proxy: proxy.Object, reservedPodIp: "deleted"));

        Assert.Equal("Not port or IP available", error.Message);
        Assert.Equal(0, transmissions);
        Dictionary<string, object?> failure = Assert.Single(logger.Entries, entry => Stage(entry) == "failed-before-transport");
        Assert.Equal("element", failure["ElementId"]);
        Assert.Equal(0, failure["TransportEntries"]);
        Assert.DoesNotContain(logger.Entries, entry => Stage(entry) == "enter-transport");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_response_lost_after_execution_is_distinct_from_a_real_http_500_without_an_extra_send(bool loseResponse)
    {
        int executions = 0;
        using var http = new HttpClient(new HttpMessageHandlerStub((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return loseResponse
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Executed; response lost"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));
        var logger = new RecordingLogger();
        var sender = Sender(http, logger, "http://function:8080");

        using HttpResponseMessage result = await sender.SendHttpRequestAsync(Request(), OneAttempt());

        // Retry.WrapRequestAction preserves its existing synthetic 500 on network
        // errors. That status alone cannot establish that the function replied.
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.Equal(1, executions);
        Dictionary<string, object?> entered = Assert.Single(logger.Entries, entry => Stage(entry) == "enter-transport");
        Assert.Equal("element", entered["ElementId"]);
        Assert.Equal(1, entered["TransportEntries"]);
        Assert.Equal("http://function:8080/work", entered["ActualUrl"]);
        Assert.Equal(loseResponse, logger.Entries.Any(entry => Stage(entry) == "transport-outcome-unknown"));
        Assert.Equal(!loseResponse, logger.Entries.Any(entry => Stage(entry) == "response-headers"));
        Assert.DoesNotContain(logger.Entries, entry => Stage(entry) == "failed-before-transport");
    }

    private static SendClient Sender(HttpClient http, ILogger<SendClient> logger, string url) => new(
        http, logger, Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions { BaseFunctionUrl = url }),
        Mock.Of<INamespaceProvider>(), new NetworkActivityTracker());

    private static CustomRequest Request() => new(
        [new CustomHeader(SlimQueuesWorker.SlimfaasElementId, ["element"])], [1], "function", "/work", "POST", string.Empty);

    private static SlimFaasDefaultConfiguration OneAttempt() => new() { TimeoutRetries = [], HttpStatusRetries = [] };

    private static string? Stage(Dictionary<string, object?> entry) => entry.GetValueOrDefault("Stage") as string;

    private sealed class RecordingLogger : ILogger<SendClient>
    {
        internal List<Dictionary<string, object?>> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Trace && state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }
}
