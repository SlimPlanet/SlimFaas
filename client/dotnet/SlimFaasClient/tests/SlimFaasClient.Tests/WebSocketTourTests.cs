using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SlimFaasClient.Tests;

public sealed class WebSocketTourTests
{
    [Fact]
    public void Node_urls_preserve_single_endpoint_support_and_deduplicate_multiple_connections()
    {
        Assert.Single(WebSocketTour.ParseUrls("ws://localhost:30021/ws"));
        var urls = WebSocketTour.ParseUrls("ws://localhost:30021/ws, wss://other/ws,ws://localhost:30021/ws");
        Assert.Equal(2, urls.Length);
        Assert.Equal("wss", urls[1].Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://localhost/ws")]
    [InlineData("file:///tmp/worker")]
    public void Invalid_websocket_url_lists_fail_explicitly(string value) =>
        Assert.Throws<ArgumentException>(() => WebSocketTour.ParseUrls(value));

    private sealed class ProofHandler : HttpMessageHandler
    {
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal Uri? Address { get; private set; }
        internal string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Address = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status);
        }
    }

    [Theory]
    [InlineData("async")]
    [InlineData("stream")]
    public async Task Proofs_record_the_actual_worker_payload_and_delivery_identity_before_success(string mode)
    {
        using var handler = new ProofHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:30020/") };
        var tour = new WebSocketTour(http, "worker-unique", input => input + 45);
        Assert.True(tour.Matches("tour/ws/worker-unique/" + mode, mode));
        Assert.True(tour.Matches("/tour/ws/worker-unique/" + mode, mode));
        Assert.False(tour.Matches("/tour/ws/another-worker/" + mode, mode));
        var receipt = tour.Compute(Encoding.UTF8.GetBytes("""{"input":10,"nonce":"unique"}"""), mode,
            "actual-element", mode == "stream" ? "orders-unique" : null, mode == "stream" ? "actual-message" : null);
        Assert.NotNull(receipt);
        await tour.WriteAsync(receipt, CancellationToken.None);
        Assert.Equal("http://localhost:30020/data/sets/worker-unique-" + mode + "?ttl=600000", handler.Address!.AbsoluteUri);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("worker-unique", json.RootElement.GetProperty("function").GetString());
        Assert.Equal("unique", json.RootElement.GetProperty("nonce").GetString());
        Assert.Equal(55, json.RootElement.GetProperty("result").GetInt32());
        Assert.Equal("actual-element", json.RootElement.GetProperty("elementId").GetString());
        if (mode == "stream") Assert.Equal("actual-message", json.RootElement.GetProperty("messageId").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"input":41,"nonce":"unique"}""")]
    [InlineData("""{"input":10,"nonce":""}""")]
    [InlineData("not JSON")]
    public void Invalid_payloads_do_not_create_success_proofs(string body)
    {
        using var http = new HttpClient();
        var tour = new WebSocketTour(http, "worker-unique", input => input);
        Assert.Null(tour.Compute(Encoding.UTF8.GetBytes(body), "async", "element"));
    }

    [Fact]
    public async Task Failed_proof_writes_cannot_acknowledge_success()
    {
        using var handler = new ProofHandler { Status = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var tour = new WebSocketTour(http, "worker-unique", input => input);
        var receipt = tour.Compute(Encoding.UTF8.GetBytes("""{"input":10,"nonce":"unique"}"""), "async", "element");
        Assert.NotNull(receipt);
        await Assert.ThrowsAsync<HttpRequestException>(() => tour.WriteAsync(receipt, CancellationToken.None));
    }
}
