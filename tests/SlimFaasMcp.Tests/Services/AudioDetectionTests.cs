using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SlimFaasMcp.Models;
using SlimFaasMcp.Services;
using Xunit;
using System.Collections.Generic;

public class AudioDetectionTests
{
    [Fact]
    public async Task ExecuteToolAsync_Detects_Audio_As_Binary()
    {
        var endpoint = new Endpoint
        {
            Name = "get_audio",
            Url  = "/audio",
            Verb = "GET",
            Summary = "Audio stream",
            ContentType = "application/json", // peu importe ici; on force via HTTP handler
            Parameters = new List<Parameter>()
        };

        var swagger = new StubSwaggerService(new List<Endpoint> { endpoint });
        var handler = new AudioProbeHandler();
        var factory = new FakeHttpClientFactory(new HttpClient(handler));

        var svc = new ToolProxyService(swagger, factory);

        // Pas d'arguments
        using var doc = JsonDocument.Parse("{}");

        var res = await svc.ExecuteToolAsync("https://example/openapi.json", "get_audio", doc.RootElement, "https://api.example");

        Assert.True(handler.WasCalled);
        Assert.True(res.IsBinary);
        Assert.Equal("audio/mpeg", res.MimeType);
        Assert.Equal("track.mp3", res.FileName);
        Assert.Equal(Encoding.UTF8.GetBytes("MP3DATA"), res.Bytes);
    }

    private sealed class StubSwaggerService : ISwaggerService
    {
        private readonly List<Endpoint> _eps;
        public StubSwaggerService(List<Endpoint> eps) => _eps = eps;
        public Task<JsonDocument> GetSwaggerAsync(string u, string? b = null, IDictionary<string,string>? h = null)
            => Task.FromResult(JsonDocument.Parse("""{"openapi":"3.0.0"}"""));
        public IEnumerable<Endpoint> ParseEndpoints(JsonDocument _) => _eps;
    }

    private sealed class AudioProbeHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("MP3DATA"));
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            resp.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                { FileName = "track.mp3" };
            return Task.FromResult(resp);
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public FakeHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
