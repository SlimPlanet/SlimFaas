using System.Net;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

public sealed class ClusterFileSync : IClusterFileSync, IAsyncDisposable
{
    private readonly IMessageBus _bus;
    private readonly IFileRepository _repo;
    private readonly ILogger<ClusterFileSync> _logger;
    private readonly ClusterFileSyncChannel _channel;

    public ClusterFileSync(IMessageBus bus, IFileRepository repo, ClusterFileAnnounceQueue announceQueue, ILogger<ClusterFileSync> logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _channel = new ClusterFileSyncChannel(_repo, announceQueue);
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
        bool overwrite,
        CancellationToken ct)
    {
        // 1) Sauvegarde locale + SHA256
        var put = await _repo.SaveAsync(id, content, contentType, overwrite, ct).ConfigureAwait(false);

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

        return put;
    }

    public async Task<FilePullResult> PullFileIfMissingAsync(string id, string sha256Hex, CancellationToken ct)
    {
        // déjà présent localement
        if (await _repo.ExistsAsync(id, sha256Hex, ct).ConfigureAwait(false))
        {
            var local = await _repo.OpenReadAsync(id, ct).ConfigureAwait(false);
            return new FilePullResult(local);
        }

        var idEnc = Base64UrlCodec.Encode(id);
        var requestName = FileSyncProtocol.BuildFetchName(idEnc, sha256Hex);
        var request = new TextMessage("", requestName);

        foreach (var member in _bus.Members)
        {
            if (!member.IsRemote) continue;

            try
            {
                var ok = await member.SendMessageAsync(
                    request,
                    async (response, token) =>
                    {
                        // not found
                        if (string.Equals(response.Name, FileSyncProtocol.FetchNotFound, StringComparison.Ordinal))
                            return false;

                        if (!FileSyncProtocol.TryParseFetchOkName(response.Name, out var rid, out var rsha, out var rlen))
                            return false;

                        if (!string.Equals(rid, idEnc, StringComparison.Ordinal) ||
                            !string.Equals(rsha, sha256Hex, StringComparison.OrdinalIgnoreCase))
                            return false;

                        var ct2 = response.Type?.MediaType ?? "application/octet-stream";

                        await _repo.SaveFromTransferObjectAsync(
                            id,
                            response,
                            ct2,
                            overwrite: true,
                            expectedSha256Hex: sha256Hex,
                            expectedLength: rlen,
                            token).ConfigureAwait(false);

                        return true;
                    },
                    ct).ConfigureAwait(false);

                if (ok)
                {
                    var local = await _repo.OpenReadAsync(id, ct).ConfigureAwait(false);
                    return new FilePullResult(local);
                }
            }
            catch (Exception ex) when (IsNotImplemented(ex))
            {
                _logger.LogDebug("FileSync fetch skipped (remote returned 501 Not Implemented). Node={Node}", SafeNode(member));
            }
            catch (Exception ex)
            {
                // membre down/timeout/etc => on tente le suivant
                _logger.LogDebug(ex, "FileSync fetch failed. Node={Node}", SafeNode(member));
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
}
