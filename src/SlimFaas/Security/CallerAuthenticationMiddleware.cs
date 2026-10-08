using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SlimFaas.Options;

namespace SlimFaas.Security;

/// <summary>
/// Verifies the <c>X-SlimFaas-*</c> signature headers (see <see cref="RequestSignature"/>)
/// and records the proven <see cref="CallerIdentity"/> on the request. Registered only when
/// <c>SlimFaas:CallerAuthentication:Mode</c> is <c>Hybrid</c> or <c>Strict</c>.
/// </summary>
/// <remarks>
/// A request without the caller header is left untouched: whether it may reach a private
/// target is decided later by <see cref="CallerClassification"/>. A request that carries
/// the caller header but fails verification is answered <c>401</c> immediately.
/// </remarks>
internal sealed class CallerAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CallerAuthenticationMiddleware> _logger;
    private readonly CallerAuthenticationOptions _options;
    private readonly ICallerKeyStore _keyStore;
    private readonly NonceCache _nonces;
    private readonly LogThrottle _throttle;
    private readonly TimeProvider _timeProvider;

    public CallerAuthenticationMiddleware(
        RequestDelegate next,
        ILogger<CallerAuthenticationMiddleware> logger,
        IOptions<SlimFaasOptions> options,
        ICallerKeyStore keyStore,
        NonceCache nonces,
        LogThrottle throttle,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _logger = logger;
        _options = options.Value.CallerAuthentication;
        _keyStore = keyStore;
        _nonces = nonces;
        _throttle = throttle;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        string? callerId = request.Headers[RequestSignature.CallerHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(callerId))
        {
            context.Features.Set(new CallerAuthenticationFeature(_options.Mode, null));
            await _next(context);
            return;
        }

        string? failure = await VerifyAsync(context, callerId);
        if (failure is not null)
        {
            string remote = context.Connection.RemoteIpAddress?.ToString() ?? "";
            if (_throttle.ShouldLog(failure + "|" + callerId + "|" + remote))
            {
                _logger.LogSignedRequestRejected(callerId, remote, request.Method, request.Path.Value ?? "", failure);
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Features.Set(new CallerAuthenticationFeature(_options.Mode, new CallerIdentity(callerId)));
        await _next(context);
    }

    /// <summary>Returns <c>null</c> when the request is authentic, otherwise a short reason.</summary>
    private async Task<string?> VerifyAsync(HttpContext context, string callerId)
    {
        HttpRequest request = context.Request;
        if (!RequestSignature.IsValidCallerId(callerId))
        {
            return "invalid caller id";
        }

        string? timestamp = request.Headers[RequestSignature.TimestampHeader].FirstOrDefault();
        string? nonce = request.Headers[RequestSignature.NonceHeader].FirstOrDefault();
        string? contentSha256 = request.Headers[RequestSignature.ContentSha256Header].FirstOrDefault();
        string? signature = request.Headers[RequestSignature.SignatureHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(nonce)
            || string.IsNullOrEmpty(contentSha256) || string.IsNullOrEmpty(signature))
        {
            return "missing signature header";
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long unixSeconds))
        {
            return "invalid timestamp";
        }

        long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - unixSeconds) > _options.ClockSkewSeconds)
        {
            return "timestamp outside the accepted window";
        }

        if (!RequestSignature.IsValidNonce(nonce))
        {
            return "invalid nonce";
        }

        if (!RequestSignature.IsValidContentSha256(contentSha256))
        {
            return "invalid content hash";
        }

        byte[] providedSignature;
        try
        {
            providedSignature = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return "invalid signature encoding";
        }

        IReadOnlyList<byte[]> keys = _keyStore.GetKeys(callerId);
        if (keys.Count == 0)
        {
            return "unknown caller";
        }

        if (!string.Equals(contentSha256, RequestSignature.UnsignedPayload, StringComparison.Ordinal))
        {
            string? bodyFailure = await VerifyBodyAsync(request, contentSha256, context.RequestAborted);
            if (bodyFailure is not null)
            {
                return bodyFailure;
            }
        }

        string canonical = RequestSignature.BuildCanonicalString(
            request.Method,
            request.Path.Value ?? "/",
            QueryPairs(request),
            callerId,
            timestamp,
            nonce,
            contentSha256);
        byte[] canonicalBytes = Encoding.UTF8.GetBytes(canonical);

        bool valid = false;
        foreach (byte[] key in keys)
        {
            byte[] expected = HMACSHA256.HashData(key, canonicalBytes);
            if (CryptographicOperations.FixedTimeEquals(expected, providedSignature))
            {
                valid = true;
                break;
            }
        }

        if (!valid)
        {
            return "signature mismatch";
        }

        // Recorded only after the signature verified, so unauthenticated traffic cannot fill the cache.
        return _nonces.TryAdd(callerId, nonce) ? null : "nonce already used";
    }

    private async Task<string?> VerifyBodyAsync(HttpRequest request, string expectedSha256, CancellationToken ct)
    {
        long limit = _options.MaxSignedBodyBytes;
        if (request.ContentLength is > 0 && request.ContentLength > limit)
        {
            return "body larger than MaxSignedBodyBytes";
        }

        MemoryStream buffer = new();
        await using (buffer.ConfigureAwait(false))
        {
            byte[] chunk = new byte[16 * 1024];
            while (true)
            {
                int read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > limit)
                {
                    return "body larger than MaxSignedBodyBytes";
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            string actual = RequestSignature.Sha256Hex(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                return "body hash mismatch";
            }

            // The endpoint reads the body again: replace the consumed stream with the buffered copy.
            request.Body = new MemoryStream(buffer.ToArray(), writable: false);
            return null;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> QueryPairs(HttpRequest request)
    {
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in request.Query)
        {
            foreach (string? value in pair.Value)
            {
                yield return new KeyValuePair<string, string>(pair.Key, value ?? "");
            }
        }
    }
}

public static class CallerAuthenticationApplicationBuilderExtensions
{
    /// <summary>Registers the caller-authentication services for the configured mode.</summary>
    public static IServiceCollection AddCallerAuthentication(this IServiceCollection services, CallerAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<ICallerKeyStore>(_ =>
            new FileCallerKeyStore(options.SecretsDirectory, TimeSpan.FromSeconds(options.KeyRefreshSeconds)));
        services.AddSingleton(_ =>
            new NonceCache(TimeSpan.FromSeconds(2L * options.ClockSkewSeconds), options.NonceCacheMaxEntriesPerCaller));
        services.AddSingleton(_ => new LogThrottle(TimeSpan.FromSeconds(options.WarningIntervalSeconds)));
        return services;
    }

    /// <summary>
    /// Adds the signature verification to the pipeline. Must run after the forwarded-headers
    /// middleware and before any endpoint that classifies callers.
    /// </summary>
    public static IApplicationBuilder UseCallerAuthentication(this IApplicationBuilder app)
        => app.UseMiddleware<CallerAuthenticationMiddleware>();
}
