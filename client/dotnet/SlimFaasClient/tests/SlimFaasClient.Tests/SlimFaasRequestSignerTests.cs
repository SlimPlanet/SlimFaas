using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace SlimFaasClient.Tests;

/// <summary>
/// The vector shared with the SlimFaas server tests (<c>RequestSignatureTests</c>) and the
/// Python client tests: all three must produce the same signature.
/// </summary>
public class SlimFaasRequestSignerTests
{
    private static readonly byte[] s_key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
    private static readonly DateTimeOffset s_timestamp = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private const string Nonce = "n0nce-123";
    private const string BodySha256 = "3ff6698e101869f36e088516c6c0ca6495c40c0abdae72f6e4d124610dace7b0";
    private const string PostSignature = "10+5lpxIbQ7lN9xIbfdF5o7/2h+R7I+4CFNuXX7VfTA=";
    private const string GetSignature = "I3pNGgh8gvq55dctrB99cJpJQoxQ1+rOVd7dR9lIWMc=";

    private static SlimFaasCallerCredentials Credentials() => new("billing-api", s_key);

    [Fact]
    public void CreateHeaders_MatchesTheSharedPostVector()
    {
        var headers = SlimFaasRequestSigner.CreateHeaders(
            Credentials(), "post", "/function/fibonacci/compute",
            [new("b", "2"), new("a", "1"), new("a", "[0]")],
            SlimFaasRequestSigner.Sha256Hex("{\"n\":10}"u8), s_timestamp, Nonce);

        headers[SlimFaasRequestSigner.CallerHeader].Should().Be("billing-api");
        headers[SlimFaasRequestSigner.TimestampHeader].Should().Be("1700000000");
        headers[SlimFaasRequestSigner.NonceHeader].Should().Be(Nonce);
        headers[SlimFaasRequestSigner.ContentSha256Header].Should().Be(BodySha256);
        headers[SlimFaasRequestSigner.SignatureHeader].Should().Be(PostSignature);
    }

    [Fact]
    public async Task SignAsync_MatchesTheSharedPostVector_AndKeepsTheBodyReadable()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "http://slimfaas:5000/function/fibonacci/compute?b=2&a=1&a=%5B0%5D")
        {
            Content = new StringContent("{\"n\":10}", Encoding.UTF8, "application/json")
        };

        await SlimFaasRequestSigner.SignAsync(request, Credentials());
        // Timestamp and nonce are generated: recompute with the fixed ones to compare the signature.
        var expected = SlimFaasRequestSigner.CreateHeaders(Credentials(), "POST", "/function/fibonacci/compute",
            SlimFaasRequestSigner.ParseQuery("?b=2&a=1&a=%5B0%5D"), BodySha256, s_timestamp, Nonce);

        request.Headers.GetValues(SlimFaasRequestSigner.ContentSha256Header).Single().Should().Be(BodySha256);
        expected[SlimFaasRequestSigner.SignatureHeader].Should().Be(PostSignature);
        (await request.Content!.ReadAsStringAsync()).Should().Be("{\"n\":10}");
        request.Headers.Contains(SlimFaasRequestSigner.SignatureHeader).Should().BeTrue();
        request.Headers.Contains(SlimFaasRequestSigner.NonceHeader).Should().BeTrue();
        request.Headers.Contains(SlimFaasRequestSigner.TimestampHeader).Should().BeTrue();
    }

    [Fact]
    public void CreateHeaders_MatchesTheSharedGetVector()
    {
        var headers = SlimFaasRequestSigner.CreateHeaders(
            Credentials(), "GET", "/function/fibonacci/health", null,
            SlimFaasRequestSigner.Sha256Hex([]), s_timestamp, Nonce);

        headers[SlimFaasRequestSigner.ContentSha256Header].Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        headers[SlimFaasRequestSigner.SignatureHeader].Should().Be(GetSignature);
    }

    [Fact]
    public async Task SignAsync_WithoutBodySigning_DeclaresAnUnsignedPayload()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "http://slimfaas:5000/function/upload")
        {
            Content = new ByteArrayContent(new byte[1024])
        };

        await SlimFaasRequestSigner.SignAsync(request, Credentials(), signBody: false);

        request.Headers.GetValues(SlimFaasRequestSigner.ContentSha256Header).Single().Should().Be(SlimFaasRequestSigner.UnsignedPayload);
    }

    [Fact]
    public async Task SignAsync_RejectsRelativeUris()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/function/x", UriKind.Relative));

        Func<Task> act = () => SlimFaasRequestSigner.SignAsync(request, Credentials());

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SigningHandler_SignsEveryRequest_WithTheResolvedBaseAddress()
    {
        HttpRequestMessage? seen = null;
        using SlimFaasSigningHandler handler = new(Credentials())
        {
            InnerHandler = new CapturingHandler(r => seen = r)
        };
        using HttpClient client = new(handler) { BaseAddress = new Uri("http://slimfaas:5000") };

        HttpResponseMessage response = await client.GetAsync("/function/fibonacci/health?n=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seen.Should().NotBeNull();
        seen!.Headers.GetValues(SlimFaasRequestSigner.CallerHeader).Single().Should().Be("billing-api");
        seen.Headers.GetValues(SlimFaasRequestSigner.ContentSha256Header).Single().Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        seen.Headers.Contains(SlimFaasRequestSigner.SignatureHeader).Should().BeTrue();
    }

    [Fact]
    public void CanonicalQuery_EncodesRfc3986_AndSortsOrdinally()
        => SlimFaasRequestSigner.CanonicalQuery([new("tilde", "~"), new("b", "2"), new("sp ace", "x y"), new("a", "1"), new("a", "[0]")])
            .Should().Be("a=%5B0%5D&a=1&b=2&sp%20ace=x%20y&tilde=~");

    [Fact]
    public void ParseQuery_DecodesPlusAndPercentEncoding()
    {
        List<KeyValuePair<string, string>> pairs = SlimFaasRequestSigner.ParseQuery("?a=x+y&b=%5B0%5D&c");

        pairs.Select(p => p.Key + "=" + p.Value).Should().Equal("a=x y", "b=[0]", "c=");
    }

    [Fact]
    public void Credentials_FromFile_StripsTrailingWhitespace()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "0123456789abcdef0123456789abcdef\r\n");

            SlimFaasCallerCredentials credentials = SlimFaasCallerCredentials.FromFile("billing-api", path);

            credentials.Key.Should().Equal(s_key);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-api")]
    [InlineData("Billing")]
    [InlineData("api.next")]
    public void Credentials_RejectInvalidCallerIds(string callerId)
    {
        Action act = () => _ = new SlimFaasCallerCredentials(callerId, s_key);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Credentials_RejectShortKeys()
    {
        Action act = () => _ = new SlimFaasCallerCredentials("billing-api", Encoding.ASCII.GetBytes("short"));

        act.Should().Throw<ArgumentException>();
    }

    private sealed class CapturingHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
