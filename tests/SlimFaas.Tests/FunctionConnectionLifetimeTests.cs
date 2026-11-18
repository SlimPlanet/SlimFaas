using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace SlimFaas.Tests;

public sealed class FunctionConnectionLifetimeTests
{
    [Theory(Timeout = 20_000)]
    [InlineData("legacy")]
    [InlineData("functions")]
    [InlineData("batch")]
    [InlineData("peers")]
    public async Task An_established_connection_retires_only_after_its_active_response_finishes(string policy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = deadline.Token;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http1));
        await using WebApplication app = builder.Build();
        app.MapGet("/connection", (HttpContext context) => Results.Text(context.Connection.Id));
        app.MapGet("/stream", async (HttpContext context) =>
        {
            context.Response.Headers["X-Connection-Id"] = context.Connection.Id;
            context.Response.ContentLength = 2;
            await context.Response.WriteAsync("A", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("B", context.RequestAborted);
        });
        await app.StartAsync(token);
        try
        {
            TimeSpan lifetime = TimeSpan.FromMilliseconds(100);
            HttpMessageHandler handler;
            bool recycle = policy != "legacy";
            if (recycle)
            {
                SocketsHttpHandler pooled = policy switch
                {
                    "batch" => InternalHttpClientHandler.CreateForSlimData(allowUnsecureSsl: false),
                    "peers" => InternalHttpClientHandler.CreateForClusterPeers(allowUnsecureSsl: false),
                    _ => InternalHttpClientHandler.CreateForFunctions(allowUnsecureSsl: false)
                };
                Assert.Equal(TimeSpan.FromMinutes(policy == "functions" ? 5 : 2), pooled.PooledConnectionLifetime);
                // Shorten only the test clock; production retains the two/five-minute policies.
                pooled.PooledConnectionLifetime = lifetime;
                handler = pooled;
            }
            else
            {
                // Reproduce the handler used before PR467, with its unbounded connection age.
                handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true };
            }
            using var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()) };
            string originalConnection;
            using (HttpResponseMessage response = await client.GetAsync("/stream", HttpCompletionOption.ResponseHeadersRead, token))
            {
                Assert.Equal(HttpVersion.Version11, response.Version);
                originalConnection = response.Headers.GetValues("X-Connection-Id").Single();
                await using Stream body = await response.Content.ReadAsStreamAsync(token);
                byte[] first = new byte[1];
                Assert.Equal(1, await body.ReadAsync(first, token));
                Assert.Equal((byte)'A', first[0]);
                byte[] second = new byte[1];
                Task<int> pendingBody = body.ReadAsync(second, token).AsTask();
                // The response remains deliberately active beyond the configured pool age.
                await Task.Delay(lifetime + lifetime, token);
                Assert.False(pendingBody.IsCompleted);
                release.SetResult();
                Assert.Equal(1, await pendingBody);
                Assert.Equal((byte)'B', second[0]);
            }
            string nextConnection = await client.GetStringAsync("/connection", token);
            Assert.Equal(!recycle, originalConnection == nextConnection);
        }
        finally
        {
            release.TrySetResult();
            await app.StopAsync(token);
        }
    }
}
