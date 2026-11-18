using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using SlimData;
using SlimFaas.Kubernetes;
using SlimFaas.Security;

namespace SlimFaas.WebSocket;

public sealed class WebSocketPeerAuthorization(IWebSocketClusterDirectory directory, IReplicasService replicas,
    ILogger<WebSocketPeerAuthorization> logger)
{
    internal const string ContextHeader = "X-SlimFaas-WebSocket-Context";
    internal const string SignatureHeader = "X-SlimFaas-WebSocket-Signature";
    private readonly ConcurrentDictionary<string, long> _used = new(StringComparer.Ordinal);
    private long _nextCleanupTicks;

    internal static (string Context, string Signature) Sign(WebSocketPeerContext context, WebSocketDirectoryOwner owner)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(context, WebSocketPeerJsonContext.Default.WebSocketPeerContext);
        return (Convert.ToBase64String(bytes), Convert.ToBase64String(
            HMACSHA256.HashData(Convert.FromBase64String(owner.RelaySecret), bytes)));
    }

    internal WebSocketPeerContext? Authenticate(HttpContext context, string operation)
    {
        try
        {
            string encoded = context.Request.Headers[ContextHeader].ToString();
            string signature = context.Request.Headers[SignatureHeader].ToString();
            if (encoded.Length > 16 * 1024 || signature.Length != 44)
                return Denied("Invalid relay proof.");
            var bytes = Convert.FromBase64String(encoded);
            var proof = JsonSerializer.Deserialize(bytes, WebSocketPeerJsonContext.Default.WebSocketPeerContext);
            if (proof is null || proof.Owner != directory.Node || proof.Incarnation != directory.Incarnation ||
                proof.Operation != operation || !Guid.TryParseExact(proof.Nonce, "N", out _) ||
                proof.DeadlineTicks <= DateTime.UtcNow.Ticks || proof.DeadlineTicks > DateTime.MaxValue.Ticks)
                return Denied("Wrong relay owner, operation or deadline.");
            var sender = replicas.Deployments.SlimFaas.Pods.FirstOrDefault(pod => pod.Name == proof.Sender);
            if (sender is null || !RemoteAddress.IsAnyOf(context.Connection.RemoteIpAddress, [sender.Ip]))
                return Denied("Relay sender is not a SlimFaas peer.");
            var owner = directory.Owner(directory.Node);
            if (owner is null || owner.Incarnation != directory.Incarnation ||
                owner.ExpiresAtTicks <= DateTime.UtcNow.Ticks)
                return Denied("Relay owner lease is unavailable.");
            var expected = HMACSHA256.HashData(Convert.FromBase64String(owner.RelaySecret), bytes);
            if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(signature)))
                return Denied("Relay signature is invalid.");
            var configuration = directory.Connections.FirstOrDefault(connection =>
                connection.Node == directory.Node && connection.Incarnation == directory.Incarnation &&
                connection.Function == proof.Function && connection.Fingerprint == proof.Fingerprint);
            if (configuration is null)
                return Denied("Relay function registration is unavailable.");
            var config = JsonSerializer.Deserialize(configuration.Configuration, AppJsonContext.Default.WebSocketFunctionConfiguration)
                ?? throw new InvalidDataException("Missing relay function configuration.");
            WebSocketConnectionRegistry.ParseConfiguration(proof.Function, config);
            long maximumSeconds = Math.Max(300, config.ParsedConfiguration.DefaultAsync.HttpTimeout);
            if (proof.DeadlineTicks - DateTime.UtcNow.Ticks > (maximumSeconds + 30) * TimeSpan.TicksPerSecond)
                return Denied("Relay deadline exceeds the configured operation budget.");
            long now = DateTime.UtcNow.Ticks;
            long cleanup = Volatile.Read(ref _nextCleanupTicks);
            if (now >= cleanup && Interlocked.CompareExchange(ref _nextCleanupTicks,
                    now + 5 * TimeSpan.TicksPerSecond, cleanup) == cleanup)
                foreach (var (nonce, deadline) in _used)
                    if (deadline <= now) _used.TryRemove(nonce, out _);
            if (!_used.TryAdd(proof.Nonce, proof.DeadlineTicks))
                return Denied("Relay proof was already used.");
            return proof;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidDataException)
        {
            logger.LogRelayProofInvalid(exception);
            return null;
        }
    }

    private WebSocketPeerContext? Denied(string reason)
    {
        logger.LogRelayRejected(reason);
        return null;
    }

    internal static bool MatchesPayload(WebSocketPeerContext proof, ReadOnlySpan<byte> payload) =>
        string.Equals(proof.PayloadHash, Convert.ToHexString(SHA256.HashData(payload)), StringComparison.Ordinal);
}
