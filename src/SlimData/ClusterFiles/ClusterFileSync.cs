using System.Net;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Messaging;
using SlimData.ClusterFiles.Http;

namespace SlimData.ClusterFiles;

public sealed class ClusterFileSync : IClusterFileSync, IAsyncDisposable
{
    private readonly IMessageBus _bus;
    private readonly IFileRepository _repo;
    private readonly ILogger<ClusterFileSync> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ClusterFileSyncChannel _channel;
    private readonly KeyedAsyncLock _idLock = new(KeyedAsyncLock.MegaBytes(256));

    public ClusterFileSync(IMessageBus bus, IFileRepository repo, ClusterFileAnnounceQueue announceQueue, ILogger<ClusterFileSync> logger,
        IHttpClientFactory httpFactory)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpFactory = httpFactory;

        _channel = new ClusterFileSyncChannel(_repo, announceQueue, _idLock, logger);
        _bus.AddListener(_channel);
    }

    public ValueTask DisposeAsync()
    {
        _bus.RemoveListener(_channel);
        return ValueTask.CompletedTask;
    }

    public async Task<FilePutResult> BroadcastFilePutAsync(
        string id,
        Stream content,
        string contentType,
        long contentLengthBytes,
        bool overwrite,
        long? ttl,
        CancellationToken ct)
    {
        MemoryDump.Dump("BeforeUpload");
        await using var _ = await _idLock.AcquireAsync(id, contentLengthBytes, ct);
        
        long? expireAtUtcTicks = null;
        if (ttl.HasValue && ttl.Value > 0)
             expireAtUtcTicks = DateTime.UtcNow.AddMilliseconds(ttl.Value).Ticks;
        // 1) Sauvegarde locale + SHA256
        var put = await _repo.SaveAsync(id, content, contentType, overwrite, expireAtUtcTicks, ct).ConfigureAwait(false);

        // 2) Broadcast "announce-only" (pas de stream envoyé)
        var idEnc = Base64UrlCodec.Encode(id);
        var announceName = FileSyncProtocol.BuildAnnounceName(idEnc, put.Sha256Hex, put.Length, put.ContentType, overwrite);

        foreach (var member in _bus.Members)
        {
            if (!member.IsRemote) continue;

            try
            {
                var msg = new TextMessage("", announceName);
                await member.SendSignalAsync(msg, requiresConfirmation: true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNotImplemented(ex))
            {
                // Nœud pas prêt / pas de listener enregistré => on ignore (best effort)
                _logger.LogWarning("FileSync announce skipped (remote returned 501 Not Implemented). Node={Node}", SafeNode(member));
            }
            catch (Exception ex)
            {
                // Best effort: on ne casse pas l'upload, on log et on continue.
                _logger.LogWarning(ex, "FileSync announce failed. Node={Node}", SafeNode(member));
            }
        }
        MemoryDump.Dump("AfterUpload");
        return put;
    }

    private const int RangeChunkSizeBytes = 10 * 1024 * 1024;       // 10 MiB
    private static readonly TimeSpan PerChunkTimeout = TimeSpan.FromMinutes(2);

    public async Task<FilePullResult> PullFileIfMissingAsync(string id, string sha256Hex, CancellationToken ct)
    {
        // Déjà présent localement
        if (await _repo.ExistsAsync(id, sha256Hex, ct).ConfigureAwait(false))
            return new FilePullResult(await _repo.OpenReadAsync(id, ct).ConfigureAwait(false));

        // Itérer sur tous les nœuds (remotes) pour trouver celui qui a le fichier
        var candidates = _bus.Members.Where(m => m.IsRemote).ToArray();
        if (candidates.Length == 0)
            return new FilePullResult(null);

        var http = _httpFactory.CreateClient("ClusterFilesTransfer");

        foreach (var member in candidates)
        {

            var baseUri = RemoveLastPathSegment(SafeNode(member));
            // /cluster/files/{id}?sha=...
            var fileUri = new Uri($"{baseUri}/cluster/files/{Uri.EscapeDataString(id)}?sha={Uri.EscapeDataString(sha256Hex)}");
            _logger.LogInformation("GET {Node}", fileUri);
            HttpResponseMessage? headResp = null;
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, fileUri);
                headResp = await HttpRedirect.SendWithRedirectAsync(http, headReq, ct).ConfigureAwait(false);
                _logger.LogInformation("GET {FileUri} {StatusCode}", fileUri, headResp.StatusCode);
                if (headResp.StatusCode == HttpStatusCode.NotFound)
                {
                    continue;
                }

                if (!headResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("HEAD failed on node {Node}. Status={Status}", fileUri, (int)headResp.StatusCode);
                    continue;
                }

                var length = headResp.Content.Headers.ContentLength;
                if (length is null || length <= 0)
                {
                    _logger.LogWarning("HEAD ok but no Content-Length from node {Node}", fileUri);
                    continue;
                }

                var contentType = headResp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

                long? expireAtUtcTicks = null;
                if (headResp.Headers.TryGetValues("X-SlimFaas-ExpireAtUtcTicks", out var vals))
                {
                    var s = vals.FirstOrDefault();
                    if (long.TryParse(s, out var v) && v > 0) expireAtUtcTicks = v;
                }

                // URL finale (si redirect 307/308 etc.)
                var finalUri = headResp.RequestMessage?.RequestUri ?? fileUri;

                // Lock + budget global (comme avant)
                await using var _ = await _idLock.AcquireAsync(id, length.Value, ct).ConfigureAwait(false);

                // Re-check après lock
                if (await _repo.ExistsAsync(id, sha256Hex, ct).ConfigureAwait(false))
                    return new FilePullResult(await _repo.OpenReadAsync(id, ct).ConfigureAwait(false));

                // Téléchargement range -> SaveAsync (streaming)
                try
                {
                    await using var rangeStream = new HttpRangeReadStream(
                        http,
                        finalUri,
                        length.Value,
                        RangeChunkSizeBytes,
                        PerChunkTimeout);

                    var put = await _repo.SaveAsync(
                        id: id,
                        content: rangeStream,
                        contentType: contentType,
                        overwrite: true,
                        expireAtUtcTicks: expireAtUtcTicks,
                        ct: ct).ConfigureAwait(false);

                    // Vérif intégrité
                    if (!put.Sha256Hex.Equals(sha256Hex, StringComparison.OrdinalIgnoreCase) || put.Length != length.Value)
                    {
                        _logger.LogWarning(
                            "Cluster pull integrity mismatch from {Node}. Id={Id} ExpectedSha={Sha} ActualSha={ActSha} ExpectedLen={Len} ActualLen={ActLen}",
                            SafeNode(member), id, sha256Hex, put.Sha256Hex, length.Value, put.Length);

                        await _repo.DeleteAsync(id, ct).ConfigureAwait(false);
                        continue; // essaie un autre nœud
                    }

                    // OK
                    return new FilePullResult(await _repo.OpenReadAsync(id, ct).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Range pull failed from node {Node}. Id={Id}", SafeNode(member), id);
                    // essaie le suivant
                    continue;
                }
            }
            finally
            {
                headResp?.Dispose();
            }
        }

        return new FilePullResult(null);
    }


    private static bool IsNotImplemented(Exception ex)
    {
        // Cas “standard” : HttpRequestException avec StatusCode (public) -> AOT OK
        if (ex is HttpRequestException hre && hre.StatusCode == HttpStatusCode.NotImplemented)
            return true;

        // Sinon (cas DotNext interne), fallback AOT-friendly sur le message
        // (c’est exactement ce que tu as dans tes logs)
        var msg = ex.Message;
        return msg.Contains("501", StringComparison.Ordinal) ||
               msg.Contains("Not Implemented", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeNode(IClusterMember member)
        => member.EndPoint?.ToString() ?? member.Id.ToString();
    
    public static string RemoveLastPathSegment(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }
        if (url.EndsWith('/'))
        {
            url = url.Substring(0, url.Length - 1);
        }

        return url;
    }
}
