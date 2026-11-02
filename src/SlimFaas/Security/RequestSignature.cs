using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SlimFaas.Security;

/// <summary>
/// Canonical request and HMAC-SHA256 signature shared by SlimFaas and its clients.
/// </summary>
/// <remarks>
/// Canonical string (lines joined with <c>\n</c>, no trailing newline):
/// <code>
/// SLIMFAAS-HMAC-SHA256
/// &lt;HTTP method, upper case&gt;
/// &lt;decoded path&gt;
/// &lt;canonical query: RFC 3986-encoded key=value pairs sorted ordinally, joined with &amp;&gt;
/// &lt;caller-id&gt;
/// &lt;timestamp, Unix seconds&gt;
/// &lt;nonce&gt;
/// &lt;lower-case hex SHA-256 of the body, or UNSIGNED-PAYLOAD&gt;
/// </code>
/// Signature = base64(HMAC-SHA256(key, canonical string)).
/// </remarks>
internal static class RequestSignature
{
    public const string Algorithm = "SLIMFAAS-HMAC-SHA256";
    public const string CallerHeader = "X-SlimFaas-Caller";
    public const string TimestampHeader = "X-SlimFaas-Timestamp";
    public const string NonceHeader = "X-SlimFaas-Nonce";
    public const string ContentSha256Header = "X-SlimFaas-Content-Sha256";
    public const string SignatureHeader = "X-SlimFaas-Signature";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>Lower-case hex SHA-256 of an empty body.</summary>
    public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public const int MaxCallerIdLength = 63;
    public const int MaxNonceLength = 128;

    public static string BuildCanonicalString(
        string method,
        string path,
        IEnumerable<KeyValuePair<string, string>> query,
        string callerId,
        string timestamp,
        string nonce,
        string contentSha256)
    {
        StringBuilder builder = new(256);
        builder.Append(Algorithm).Append('\n')
            .Append(method.ToUpperInvariant()).Append('\n')
            .Append(path).Append('\n')
            .Append(CanonicalQuery(query)).Append('\n')
            .Append(callerId).Append('\n')
            .Append(timestamp).Append('\n')
            .Append(nonce).Append('\n')
            .Append(contentSha256);
        return builder.ToString();
    }

    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query)
    {
        List<string> pairs = [];
        foreach (KeyValuePair<string, string> pair in query)
        {
            pairs.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
        }

        pairs.Sort(StringComparer.Ordinal);
        return string.Join('&', pairs);
    }

    public static string Sign(byte[] key, string canonicalString)
        => Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonicalString)));

    public static string Sha256Hex(ReadOnlySpan<byte> body)
        => Convert.ToHexStringLower(SHA256.HashData(body));

    public static string UnixSecondsNow()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A caller id is a DNS-label-like name (<c>[a-z0-9]</c> and <c>-</c>, 1 to 63 characters,
    /// no leading or trailing dash) so that it can be used as a file name without traversal.
    /// </summary>
    public static bool IsValidCallerId(string? callerId)
    {
        if (string.IsNullOrEmpty(callerId) || callerId.Length > MaxCallerIdLength)
        {
            return false;
        }

        if (callerId[0] == '-' || callerId[^1] == '-')
        {
            return false;
        }

        foreach (char c in callerId)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > MaxNonceLength)
        {
            return false;
        }

        foreach (char c in nonce)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '-' or '_' or '.' or '=';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidContentSha256(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (string.Equals(value, UnsignedPayload, StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool ok = c is (>= 'a' and <= 'f') or (>= '0' and <= '9');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
