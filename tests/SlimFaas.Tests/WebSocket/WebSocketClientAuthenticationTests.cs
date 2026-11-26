using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.WebSocket;

public sealed class WebSocketClientAuthenticationTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("Bearer anything", "")]
    [InlineData(null, "   ")]
    [InlineData("Bearer s3cret", "s3cret")]
    [InlineData("bearer s3cret", "s3cret")]
    [InlineData("Bearer s3cret", "s3cret\n")]
    public void Accepts_any_client_without_a_secret_or_a_matching_bearer_token(string? authorization, string secret)
    {
        Assert.True(WebSocketEndpoints.IsClientAuthorized(authorization, secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("s3cret")]
    [InlineData("Basic s3cret")]
    [InlineData("Bearer s3cre")]
    [InlineData("Bearer s3cret2")]
    [InlineData("Bearer S3CRET")]
    [InlineData("Bearer s3cret, Bearer s3cret")]
    public void Rejects_a_missing_or_wrong_bearer_token_when_a_secret_is_configured(string? authorization)
    {
        Assert.False(WebSocketEndpoints.IsClientAuthorized(authorization, "s3cret"));
    }

    [Fact]
    public async Task Handshake_without_the_secret_is_rejected_before_the_upgrade()
    {
        await using var server = await Server.StartAsync("s3cret");
        using var http = new HttpClient();

        using var missing = await http.GetAsync(server.HttpUri);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("Bearer", Assert.Single(missing.Headers.WwwAuthenticate).Scheme);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer wrong");
        var exception = await Assert.ThrowsAsync<WebSocketException>(() =>
            socket.ConnectAsync(server.WebSocketUri, CancellationToken.None));
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_with_the_secret_opens_the_websocket()
    {
        await using var server = await Server.StartAsync("s3cret");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer s3cret");
        await socket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, server.HttpUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret");
        using var plain = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
    }

    [Fact]
    public async Task Handshake_without_a_configured_secret_stays_open_to_every_client()
    {
        await using var server = await Server.StartAsync("");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Server(WebApplication app, Uri httpUri)
        {
            _app = app;
            HttpUri = httpUri;
            WebSocketUri = new UriBuilder(httpUri) { Scheme = "ws" }.Uri;
        }

        public Uri HttpUri { get; }

        public Uri WebSocketUri { get; }

        public static async Task<Server> StartAsync(string secret)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
                new SlimFaasOptions { WebSocketPort = 0, WebSocketSecret = secret }));
            builder.Services.AddSingleton(Mock.Of<IReplicasService>());
            builder.Services.AddSingleton<WebSocketConnectionRegistry>();
            var app = builder.Build();
            app.UseWebSockets();
            app.MapWebSocketEndpoints();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            return new Server(app, new Uri(new Uri(address), "/ws"));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
