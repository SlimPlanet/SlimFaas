using System.Text;
using SlimFaas.Security;

namespace SlimFaas.Tests.Security;

/// <summary>
/// The vector below is shared with the .NET and Python client tests: the three
/// implementations must produce the same canonical string and signature.
/// </summary>
public class RequestSignatureTests
{
    internal static readonly byte[] SharedKey = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
    internal const string SharedCaller = "billing-api";
    internal const string SharedTimestamp = "1700000000";
    internal const string SharedNonce = "n0nce-123";
    internal const string SharedBodySha256 = "3ff6698e101869f36e088516c6c0ca6495c40c0abdae72f6e4d124610dace7b0";
    internal const string SharedPostSignature = "10+5lpxIbQ7lN9xIbfdF5o7/2h+R7I+4CFNuXX7VfTA=";
    internal const string SharedGetSignature = "I3pNGgh8gvq55dctrB99cJpJQoxQ1+rOVd7dR9lIWMc=";

    [Fact]
    public void SharedVector_Post_MatchesTheClients()
    {
        string canonical = RequestSignature.BuildCanonicalString(
            "post",
            "/function/fibonacci/compute",
            [new("b", "2"), new("a", "1"), new("a", "[0]")],
            SharedCaller,
            SharedTimestamp,
            SharedNonce,
            RequestSignature.Sha256Hex("{\"n\":10}"u8));

        Assert.Equal(
            "SLIMFAAS-HMAC-SHA256\nPOST\n/function/fibonacci/compute\na=%5B0%5D&a=1&b=2\nbilling-api\n1700000000\nn0nce-123\n" + SharedBodySha256,
            canonical);
        Assert.Equal(SharedPostSignature, RequestSignature.Sign(SharedKey, canonical));
    }

    [Fact]
    public void SharedVector_Get_MatchesTheClients()
    {
        string canonical = RequestSignature.BuildCanonicalString(
            "GET", "/function/fibonacci/health", [], SharedCaller, SharedTimestamp, SharedNonce,
            RequestSignature.EmptyBodySha256);

        Assert.Equal(SharedGetSignature, RequestSignature.Sign(SharedKey, canonical));
    }

    [Fact]
    public void EmptyBodyHash_IsTheSha256OfNothing()
        => Assert.Equal(RequestSignature.EmptyBodySha256, RequestSignature.Sha256Hex([]));

    [Fact]
    public void CanonicalQuery_EncodesAndSortsOrdinally()
        => Assert.Equal(
            "a=%5B0%5D&a=1&b=2&sp%20ace=x%20y&tilde=~",
            RequestSignature.CanonicalQuery(
                [new("tilde", "~"), new("b", "2"), new("sp ace", "x y"), new("a", "1"), new("a", "[0]")]));

    [Theory]
    [InlineData("billing-api", true)]
    [InlineData("a", true)]
    [InlineData("job-42", true)]
    [InlineData("", false)]
    [InlineData("-api", false)]
    [InlineData("api-", false)]
    [InlineData("Billing", false)]
    [InlineData("../etc", false)]
    [InlineData("api.next", false)]
    [InlineData("api_1", false)]
    public void IsValidCallerId(string callerId, bool expected)
        => Assert.Equal(expected, RequestSignature.IsValidCallerId(callerId));

    [Fact]
    public void IsValidCallerId_RejectsNamesLongerThan63()
    {
        Assert.True(RequestSignature.IsValidCallerId(new string('a', 63)));
        Assert.False(RequestSignature.IsValidCallerId(new string('a', 64)));
    }

    [Theory]
    [InlineData("n0nce-123", true)]
    [InlineData("qsL0Z_w8Q3pE4v5Kk9YtJg==", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("new\nline", false)]
    public void IsValidNonce(string nonce, bool expected)
        => Assert.Equal(expected, RequestSignature.IsValidNonce(nonce));

    [Theory]
    [InlineData("UNSIGNED-PAYLOAD", true)]
    [InlineData(RequestSignature.EmptyBodySha256, true)]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", false)]
    [InlineData("e3b0", false)]
    [InlineData("", false)]
    public void IsValidContentSha256(string value, bool expected)
        => Assert.Equal(expected, RequestSignature.IsValidContentSha256(value));
}
