using System.Net;
using System.Net.Sockets;
using System.Text;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalJobGatewayTests
{
    [Fact]
    public async Task Gateway_AttributesAndForwardsHttpRequest()
    {
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        try
        {
            await using var gateway = new LocalJobGateway(
                upstreamPort,
                "report-slimfaas-job-run1",
                "test-token");
            gateway.Start();
            Task<string> receivedRequest = ReceiveRequestAsync(upstream);
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            using HttpResponseMessage response = await client.GetAsync(
                $"http://127.0.0.1:{gateway.Port}/function/worker");
            string request = await receivedRequest.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "X-SlimFaas-Job: report-slimfaas-job-run1\r\n",
                request,
                StringComparison.Ordinal);
        }
        finally
        {
            upstream.Stop();
        }
    }

    [Fact]
    public void AddJobIdentity_ReplacesCallerHeaderAndClosesRegularHttpConnection()
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "POST /function/worker HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "X-SlimFaas-Job: spoofed\r\n" +
            "Connection: keep-alive\r\n" +
            "Content-Length: 4\r\n\r\n" +
            "body");

        byte[] result = LocalJobGateway.AddJobIdentity(
            request,
            "report-slimfaas-job-run1",
            LocalJobGateway.CreateSignature(
                "report-slimfaas-job-run1",
                "test-token"));
        string text = Encoding.ASCII.GetString(result);

        Assert.Contains(
            "X-SlimFaas-Job: report-slimfaas-job-run1\r\n",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("spoofed", text, StringComparison.Ordinal);
        Assert.Contains(
            "X-SlimFaas-Job-Signature: ",
            text,
            StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\nbody", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJobIdentity_PreservesWebSocketUpgrade()
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "GET /ws HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Connection: Upgrade\r\n" +
            "Upgrade: websocket\r\n\r\n");

        string result = Encoding.ASCII.GetString(
            LocalJobGateway.AddJobIdentity(
                request,
                "report-slimfaas-job-run1",
                LocalJobGateway.CreateSignature(
                    "report-slimfaas-job-run1",
                    "test-token")));

        Assert.Contains("Connection: Upgrade\r\n", result, StringComparison.Ordinal);
        Assert.Contains("Upgrade: websocket\r\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection: close\r\n", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "http://127.0.0.1:5020/function/api",
        "http://127.0.0.1:5199/function/api")]
    [InlineData(
        "HTTP://LOCALHOST:5020/function/api",
        "http://127.0.0.1:5199/function/api")]
    [InlineData(
        "ws://[::1]:5020/ws",
        "ws://127.0.0.1:5199/ws")]
    [InlineData(
        "https://service.example:5020/function/api",
        "https://service.example:5020/function/api")]
    public void RewriteEntrypointUrls_RoutesLocalHttpAndWebSocketUrlsThroughJobGateway(
        string value,
        string expected)
    {
        string result = LocalJobManager.RewriteEntrypointUrls(value, 5020, 5199);

        Assert.Equal(expected, result);
    }

    private static async Task<string> ReceiveRequestAsync(TcpListener listener)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        NetworkStream stream = client.GetStream();
        using var request = new MemoryStream();
        byte[] buffer = new byte[1024];
        string text;
        do
        {
            int read = await stream.ReadAsync(buffer);
            if (read == 0)
                break;
            request.Write(buffer, 0, read);
            text = Encoding.ASCII.GetString(request.ToArray());
        } while (!text.Contains("\r\n\r\n", StringComparison.Ordinal));

        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Length: 2\r\n" +
            "Connection: close\r\n\r\n" +
            "OK");
        await stream.WriteAsync(response);
        return Encoding.ASCII.GetString(request.ToArray());
    }
}
