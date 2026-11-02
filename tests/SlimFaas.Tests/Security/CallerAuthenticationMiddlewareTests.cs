using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.Tests.Endpoints;

namespace SlimFaas.Tests.Security;

public class CallerAuthenticationMiddlewareTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] s_key = RequestSignatureTests.SharedKey;
    private static readonly byte[] s_nextKey = Encoding.ASCII.GetBytes("next-key-next-key-next-key-next!");

    [Fact]
    public async Task ValidSignature_IsAccepted_AndExposesTheCallerIdentity()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Post, "/function/fibonacci/compute?b=2&a=1&a=%5B0%5D", "{\"n\":10}");

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("billing-api|{\"n\":10}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SharedVector_SignedByTheClients_IsAccepted()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = new(HttpMethod.Post, "http://localhost:5000/function/fibonacci/compute?b=2&a=1&a=%5B0%5D")
        {
            Content = new StringContent("{\"n\":10}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(RequestSignature.CallerHeader, RequestSignatureTests.SharedCaller);
        request.Headers.Add(RequestSignature.TimestampHeader, RequestSignatureTests.SharedTimestamp);
        request.Headers.Add(RequestSignature.NonceHeader, RequestSignatureTests.SharedNonce);
        request.Headers.Add(RequestSignature.ContentSha256Header, RequestSignatureTests.SharedBodySha256);
        request.Headers.Add(RequestSignature.SignatureHeader, RequestSignatureTests.SharedPostSignature);

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnsignedRequest_PassesThroughAnonymously()
    {
        using IHost host = await StartHostAsync();

        HttpResponseMessage response = await host.GetTestClient().GetAsync("http://localhost:5000/function/fibonacci/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("anonymous|", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WrongKey_IsRejected()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health", key: Encoding.ASCII.GetBytes("wrong-key-wrong-key-wrong-key-00"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task UnknownCaller_IsRejected()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health", callerId: "nobody");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("../billing-api")]
    [InlineData("Billing-Api")]
    [InlineData("billing api")]
    public async Task InvalidCallerId_IsRejected(string callerId)
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health", callerId: callerId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task TamperedPath_IsRejected()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health", signedPath: "/function/fibonacci/other");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task TamperedQuery_IsRejected()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health?n=11", signedQuery: [new("n", "10")]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task TamperedBody_IsRejected()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Post, "/function/fibonacci/compute", "{\"n\":10}", signedBody: "{\"n\":99}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task TamperedMethod_IsRejected()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Delete, "/function/fibonacci/compute", signedMethod: "GET");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public async Task TimestampOutsideTheWindow_IsRejected(int offsetSeconds)
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health", timestamp: s_now.AddSeconds(offsetSeconds));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public async Task TimestampAtTheEdgeOfTheWindow_IsAccepted(int offsetSeconds)
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health", timestamp: s_now.AddSeconds(offsetSeconds));

        Assert.Equal(HttpStatusCode.OK, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task ReplayedNonce_IsRejected_ButAnotherNonceIsAccepted()
    {
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed(HttpMethod.Get, "/function/fibonacci/health", nonce: "once"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Signed(HttpMethod.Get, "/function/fibonacci/health", nonce: "once"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed(HttpMethod.Get, "/function/fibonacci/health", nonce: "twice"))).StatusCode);
    }

    [Fact]
    public async Task ReplayedNonce_IsNotRecordedWhenTheSignatureIsInvalid()
    {
        using IHost host = await StartHostAsync();
        HttpClient client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Signed(HttpMethod.Get, "/function/fibonacci/health", nonce: "once", key: s_nextKey))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed(HttpMethod.Get, "/function/fibonacci/health", nonce: "once"))).StatusCode);
    }

    [Fact]
    public async Task MissingHeader_IsRejected()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health");
        request.Headers.Remove(RequestSignature.SignatureHeader);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task UnsignedPayload_SkipsTheBodyCheck_AndLeavesTheBodyUntouched()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Post, "/function/fibonacci/compute", new string('x', 100), unsignedPayload: true);

        HttpResponseMessage response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("billing-api|" + new string('x', 100), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HashedBodyLargerThanTheCap_IsRejected()
    {
        using IHost host = await StartHostAsync(maxSignedBodyBytes: 32);
        HttpRequestMessage request = Signed(HttpMethod.Post, "/function/fibonacci/compute", new string('x', 33));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task NextKey_IsAcceptedDuringRotation()
    {
        using IHost host = await StartHostAsync();
        HttpRequestMessage request = Signed(HttpMethod.Get, "/function/fibonacci/health", key: s_nextKey, callerId: "rotating-api");

        Assert.Equal(HttpStatusCode.OK, (await host.GetTestClient().SendAsync(request)).StatusCode);
    }

    private static HttpRequestMessage Signed(
        HttpMethod method,
        string pathAndQuery,
        string? body = null,
        byte[]? key = null,
        string callerId = "billing-api",
        string? signedPath = null,
        List<KeyValuePair<string, string>>? signedQuery = null,
        string? signedBody = null,
        string? signedMethod = null,
        DateTimeOffset? timestamp = null,
        string nonce = "n0nce-123",
        bool unsignedPayload = false)
    {
        Uri uri = new("http://localhost:5000" + pathAndQuery);
        HttpRequestMessage request = new(method, uri);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        List<KeyValuePair<string, string>> query = signedQuery ?? ParseQuery(uri.Query);
        string contentSha256 = unsignedPayload
            ? RequestSignature.UnsignedPayload
            : RequestSignature.Sha256Hex(Encoding.UTF8.GetBytes(signedBody ?? body ?? ""));
        string ts = (timestamp ?? s_now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string canonical = RequestSignature.BuildCanonicalString(
            signedMethod ?? method.Method, signedPath ?? Uri.UnescapeDataString(uri.AbsolutePath), query, callerId, ts, nonce, contentSha256);

        request.Headers.Add(RequestSignature.CallerHeader, callerId);
        request.Headers.Add(RequestSignature.TimestampHeader, ts);
        request.Headers.Add(RequestSignature.NonceHeader, nonce);
        request.Headers.Add(RequestSignature.ContentSha256Header, contentSha256);
        request.Headers.Add(RequestSignature.SignatureHeader, RequestSignature.Sign(key ?? s_key, canonical));
        return request;
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        List<KeyValuePair<string, string>> pairs = [];
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            string k = eq < 0 ? part : part[..eq];
            string v = eq < 0 ? "" : part[(eq + 1)..];
            pairs.Add(new(Uri.UnescapeDataString(k.Replace('+', ' ')), Uri.UnescapeDataString(v.Replace('+', ' '))));
        }

        return pairs;
    }

    private static async Task<IHost> StartHostAsync(long maxSignedBodyBytes = 4L * 1024L * 1024L)
    {
        SlimFaasOptions options = new()
        {
            CallerAuthentication = new CallerAuthenticationOptions
            {
                Mode = CallerAuthenticationMode.Strict,
                SecretsDirectory = "unused",
                MaxSignedBodyBytes = maxSignedBodyBytes,
            }
        };
        DictionaryKeyStore keys = new()
        {
            ["billing-api"] = [s_key],
            ["rotating-api"] = [s_key, s_nextKey],
        };

        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
                        services.AddSingleton<ICallerKeyStore>(keys);
                        services.AddSingleton(new NonceCache(TimeSpan.FromMinutes(10), 100));
                        services.AddSingleton(new LogThrottle(TimeSpan.FromMinutes(1)));
                        services.AddSingleton<TimeProvider>(new FixedTimeProvider(s_now));
                    })
                    .Configure(app =>
                    {
                        TestRemoteIp.Use(app);
                        app.UseCallerAuthentication();
                        app.Run(async context =>
                        {
                            using StreamReader reader = new(context.Request.Body);
                            string body = await reader.ReadToEndAsync();
                            string caller = context.GetCallerIdentity()?.CallerId ?? "anonymous";
                            await context.Response.WriteAsync(caller + "|" + body);
                        });
                    });
            })
            .StartAsync();
    }

    internal sealed class DictionaryKeyStore : Dictionary<string, IReadOnlyList<byte[]>>, ICallerKeyStore
    {
        public IReadOnlyList<byte[]> GetKeys(string callerId)
            => TryGetValue(callerId, out IReadOnlyList<byte[]>? keys) ? keys : [];
    }

    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
