using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SlimFaasClient;

/// <summary>
/// Identity of a caller of Private SlimFaas functions: a <c>caller-id</c> and the shared
/// key that SlimFaas holds under <c>&lt;SecretsDirectory&gt;/&lt;caller-id&gt;</c>.
/// </summary>
public sealed class SlimFaasCallerCredentials
{
    public const int MinimumKeyBytes = 16;
    public const int MaxCallerIdLength = 63;

    /// <param name="callerId">1 to 63 characters among <c>a-z</c>, <c>0-9</c> and <c>-</c>, no leading or trailing dash.</param>
    /// <param name="key">The shared key, at least 16 bytes.</param>
    public SlimFaasCallerCredentials(string callerId, byte[] key)
    {
        if (!IsValidCallerId(callerId))
        {
            throw new ArgumentException("A caller id is 1 to 63 characters among a-z, 0-9 and '-', with no leading or trailing dash.", nameof(callerId));
        }

        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinimumKeyBytes)
        {
            throw new ArgumentException($"The key must be at least {MinimumKeyBytes} bytes.", nameof(key));
        }

        CallerId = callerId;
        Key = (byte[])key.Clone();
    }

    public string CallerId { get; }

    internal byte[] Key { get; }

    /// <summary>
    /// Reads the key from a file (for example a Kubernetes Secret mounted in the pod).
    /// Trailing whitespace is ignored, exactly as SlimFaas does on its side.
    /// </summary>
    public static SlimFaasCallerCredentials FromFile(string callerId, string keyFilePath)
    {
        ArgumentNullException.ThrowIfNull(keyFilePath);
        byte[] content = File.ReadAllBytes(keyFilePath);
        int length = content.Length;
        while (length > 0 && content[length - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t')
        {
            length--;
        }

        return new SlimFaasCallerCredentials(callerId, content.AsSpan(0, length).ToArray());
    }

    public static bool IsValidCallerId(string? callerId)
    {
        if (string.IsNullOrEmpty(callerId) || callerId.Length > MaxCallerIdLength || callerId[0] == '-' || callerId[^1] == '-')
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
}

/// <summary>
/// Signs HTTP requests sent to SlimFaas with HMAC-SHA256 (issue #408). SlimFaas accepts
/// the signature when <c>SlimFaas:CallerAuthentication:Mode</c> is <c>Hybrid</c> or
/// <c>Strict</c>; in <c>Legacy</c> mode the headers are ignored.
/// </summary>
/// <remarks>
/// Canonical string (lines joined with <c>\n</c>): <c>SLIMFAAS-HMAC-SHA256</c>, the upper-case
/// method, the decoded path, the canonical query (RFC 3986-encoded <c>key=value</c> pairs
/// sorted ordinally and joined with <c>&amp;</c>), the caller id, the Unix timestamp, the
/// nonce, and the lower-case hex SHA-256 of the body or <c>UNSIGNED-PAYLOAD</c>.
/// </remarks>
public static class SlimFaasRequestSigner
{
    public const string Algorithm = "SLIMFAAS-HMAC-SHA256";
    public const string CallerHeader = "X-SlimFaas-Caller";
    public const string TimestampHeader = "X-SlimFaas-Timestamp";
    public const string NonceHeader = "X-SlimFaas-Nonce";
    public const string ContentSha256Header = "X-SlimFaas-Content-Sha256";
    public const string SignatureHeader = "X-SlimFaas-Signature";

    /// <summary>
    /// Body marker for requests whose body is streamed or larger than the SlimFaas
    /// <c>MaxSignedBodyBytes</c> limit: the body is then not covered by the signature.
    /// </summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>
    /// Adds the five signature headers to <paramref name="request"/>. The request URI must be
    /// absolute. With <paramref name="signBody"/>, the content is buffered to hash it; otherwise
    /// the body is declared <see cref="UnsignedPayload"/> and left untouched.
    /// </summary>
    public static async Task SignAsync(
        HttpRequestMessage request,
        SlimFaasCallerCredentials credentials,
        bool signBody = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);
        if (request.RequestUri is null || !request.RequestUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The request URI must be absolute to be signed.", nameof(request));
        }

        string contentSha256;
        if (!signBody)
        {
            contentSha256 = UnsignedPayload;
        }
        else if (request.Content is null)
        {
            contentSha256 = Sha256Hex([]);
        }
        else
        {
            byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            contentSha256 = Sha256Hex(body);
        }

        Uri uri = request.RequestUri;
        IReadOnlyDictionary<string, string> headers = CreateHeaders(
            credentials,
            request.Method.Method,
            Uri.UnescapeDataString(uri.AbsolutePath),
            ParseQuery(uri.Query),
            contentSha256);

        foreach (KeyValuePair<string, string> header in headers)
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    /// <summary>
    /// Computes the signature headers for a request described by its parts. Use this when the
    /// HTTP client is not <see cref="System.Net.Http.HttpClient"/>.
    /// </summary>
    /// <param name="path">The decoded request path, for example <c>/function/billing/invoice</c>.</param>
    /// <param name="query">The decoded query parameters, in any order.</param>
    /// <param name="contentSha256">Lower-case hex SHA-256 of the body (see <see cref="Sha256Hex"/>) or <see cref="UnsignedPayload"/>.</param>
    /// <param name="timestamp">Signing time; now by default.</param>
    /// <param name="nonce">Unique value per request; random by default.</param>
    public static IReadOnlyDictionary<string, string> CreateHeaders(
        SlimFaasCallerCredentials credentials,
        string method,
        string path,
        IEnumerable<KeyValuePair<string, string>>? query,
        string contentSha256,
        DateTimeOffset? timestamp = null,
        string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(contentSha256);

        string ts = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string n = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string canonical = BuildCanonicalString(method, path, query ?? [], credentials.CallerId, ts, n, contentSha256);
        string signature = Convert.ToBase64String(HMACSHA256.HashData(credentials.Key, Encoding.UTF8.GetBytes(canonical)));

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CallerHeader] = credentials.CallerId,
            [TimestampHeader] = ts,
            [NonceHeader] = n,
            [ContentSha256Header] = contentSha256,
            [SignatureHeader] = signature,
        };
    }

    /// <summary>Lower-case hex SHA-256 of a body, as expected in <see cref="ContentSha256Header"/>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> body)
    {
#pragma warning disable CA1308 // the protocol requires lower-case hex
        return Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
#pragma warning restore CA1308
    }

    internal static string BuildCanonicalString(
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

    internal static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query)
    {
        List<string> pairs = [];
        foreach (KeyValuePair<string, string> pair in query)
        {
            pairs.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
        }

        pairs.Sort(StringComparer.Ordinal);
        return string.Join('&', pairs);
    }

    internal static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        List<KeyValuePair<string, string>> pairs = [];
        if (string.IsNullOrEmpty(query))
        {
            return pairs;
        }

        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            string key = eq < 0 ? part : part[..eq];
            string value = eq < 0 ? "" : part[(eq + 1)..];
            pairs.Add(new KeyValuePair<string, string>(
                Uri.UnescapeDataString(key.Replace('+', ' ')),
                Uri.UnescapeDataString(value.Replace('+', ' '))));
        }

        return pairs;
    }
}

/// <summary>
/// <see cref="DelegatingHandler"/> that signs every outgoing request with
/// <see cref="SlimFaasRequestSigner"/>. Wrap the primary handler of the
/// <see cref="HttpClient"/> that talks to SlimFaas.
/// </summary>
/// <example>
/// <code>
/// var credentials = SlimFaasCallerCredentials.FromFile("billing-api", "/var/run/slimfaas/caller-key");
/// using var http = new HttpClient(new SlimFaasSigningHandler(credentials) { InnerHandler = new HttpClientHandler() })
/// {
///     BaseAddress = new Uri("http://slimfaas:5000")
/// };
/// var response = await http.PostAsJsonAsync("/function/billing/invoice", invoice);
/// </code>
/// </example>
public sealed class SlimFaasSigningHandler : DelegatingHandler
{
    private readonly SlimFaasCallerCredentials _credentials;
    private readonly bool _signBody;

    /// <param name="credentials">The caller identity.</param>
    /// <param name="signBody">
    /// <c>true</c> (default) buffers and hashes each request body. Set <c>false</c> for streamed
    /// or very large bodies; they are then sent as <see cref="SlimFaasRequestSigner.UnsignedPayload"/>.
    /// </param>
    public SlimFaasSigningHandler(SlimFaasCallerCredentials credentials, bool signBody = true)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
        _signBody = signBody;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await SlimFaasRequestSigner.SignAsync(request, _credentials, _signBody, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
