namespace SlimFaas.Security;

/// <summary>
/// How SlimFaas decides that a caller is internal (allowed to reach Private functions,
/// private event subscribers, peer endpoints and job mutations).
/// </summary>
public enum CallerAuthenticationMode
{
    /// <summary>
    /// Connection address only (Trusted function pods, job pods, SlimFaas members).
    /// Signed headers are ignored. Default.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// A valid signature makes the request internal. An unsigned request falls back to
    /// the address rule and, when accepted, is logged as a rate-limited warning so that
    /// unsigned callers can be inventoried before switching to <see cref="Strict"/>.
    /// </summary>
    Hybrid = 1,

    /// <summary>
    /// A valid signature is the only way to be internal. The address rule is ignored,
    /// except for SlimFaas member pods (peer traffic, see issue #409).
    /// </summary>
    Strict = 2,
}

/// <summary>
/// Options of the shared-secret request signing (<c>SlimFaas:CallerAuthentication</c>).
/// </summary>
public sealed class CallerAuthenticationOptions
{
    public const int MinimumKeyBytes = 16;

    /// <summary>Authentication mode. <see cref="CallerAuthenticationMode.Legacy"/> by default.</summary>
    public CallerAuthenticationMode Mode { get; set; } = CallerAuthenticationMode.Legacy;

    /// <summary>
    /// Directory holding one key file per caller: <c>&lt;SecretsDirectory&gt;/&lt;caller-id&gt;</c>,
    /// plus an optional <c>&lt;caller-id&gt;.next</c> accepted during rotation. Typically a
    /// Kubernetes Secret mounted as a volume. Files are re-read when they change.
    /// Required when <see cref="Mode"/> is not <see cref="CallerAuthenticationMode.Legacy"/>.
    /// </summary>
    public string SecretsDirectory { get; set; } = "";

    /// <summary>Maximum distance, in seconds, between the signed timestamp and the SlimFaas clock.</summary>
    public int ClockSkewSeconds { get; set; } = 300;

    /// <summary>
    /// Largest request body, in bytes, that SlimFaas buffers to verify a body hash. A signed
    /// request whose body is larger must declare <c>UNSIGNED-PAYLOAD</c>.
    /// </summary>
    public long MaxSignedBodyBytes { get; set; } = 4L * 1024L * 1024L;

    /// <summary>
    /// Maximum number of nonces remembered per caller. Nonces expire after
    /// 2 × <see cref="ClockSkewSeconds"/>; when the cache of a caller is full and holds no
    /// expired entry, further requests of that caller are rejected until entries expire.
    /// </summary>
    public int NonceCacheMaxEntriesPerCaller { get; set; } = 100_000;

    /// <summary>Interval, in seconds, between two checks of a key file for changes.</summary>
    public int KeyRefreshSeconds { get; set; } = 10;

    /// <summary>Interval, in seconds, between two identical warnings (unsigned caller, rejected request).</summary>
    public int WarningIntervalSeconds { get; set; } = 60;
}
