using ClusterFileDemoProdish.Models;
using ClusterFileDemoProdish.Storage;
using DotNext.Net.Cluster.Messaging;
using DotNext.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ClusterFileDemoProdish.Cluster;

public interface IClusterFileSync
{
    Task BroadcastMetaUpsertAsync(FileMeta meta, CancellationToken ct);
    Task BroadcastFilePutAsync(string id, Stream content, string contentType, bool overwrite, CancellationToken ct);
    Task BroadcastMetaDeleteAsync(string id, CancellationToken ct);

    Task<bool> PullFileIfMissingAsync(string id, CancellationToken ct);
    Task<List<FileMeta>> PullMetaDumpFromLeaderAsync(CancellationToken ct);

    IResult? TryRedirectToLeaderForWrite(HttpContext ctx);
}

public sealed class ClusterFileSync : IClusterFileSync
{
    // Prefix local pour éviter de dépendre d’une constante externe
    private const string FileDeletePrefix = "file.del:";

    private readonly IMessageBus _bus;
    private readonly IKvStore _kv;
    private readonly IFileRepository _files;
    private readonly ILogger<ClusterFileSync> _logger;

    public ClusterFileSync(IMessageBus bus, IKvStore kv, IFileRepository files, ILogger<ClusterFileSync> logger)
    {
        _bus = bus;
        _kv = kv;
        _files = files;
        _logger = logger;
    }

    public async Task BroadcastMetaUpsertAsync(FileMeta meta, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(meta);

        // Stocke localement (source de vérité côté leader)
        await _kv.SetAsync(Keys.Meta(meta.Id), Encoding.UTF8.GetBytes(json), timeToLiveMilliseconds: null);

        // Diffuse aux autres nodes
        await _bus.SendBroadcastSignalAsync(
            new TextMessage(json, MessageNames.MetaUpsert),
            requiresConfirmation: false
        );
    }

    public async Task BroadcastMetaDeleteAsync(string id, CancellationToken ct)
    {
        // Delete local meta + fichier
        await _kv.DeleteAsync(Keys.Meta(id));
        await _files.DeleteAsync(id, ct);

        // Diffuse suppression meta
        await _bus.SendBroadcastSignalAsync(
            new TextMessage(id, MessageNames.MetaDelete),
            requiresConfirmation: false
        );

        // Optionnel: supprime aussi le fichier sur les autres nodes
        await _bus.SendBroadcastSignalAsync(
            new TextMessage("", FileDeletePrefix + id),
            requiresConfirmation: false
        );
    }

    public async Task BroadcastFilePutAsync(string id, Stream fileStream, string contentType, bool overwrite, CancellationToken ct)
    {
        // IMPORTANT overwrite:
        // - on supprime d’abord l’ancien fichier sur les nodes (file.del)
        // - ainsi, si un node rate le put, il se retrouve "meta présente + fichier manquant" => pull ok
        if (overwrite)
        {
            await _bus.SendBroadcastSignalAsync(
                new TextMessage("", FileDeletePrefix + id),
                requiresConfirmation: false
            );
        }

        // Ensure stream is at beginning (FileStream est seekable)
        if (fileStream.CanSeek)
            fileStream.Position = 0;

        var name = MessageNames.FilePutPrefix + id;

        await _bus.SendBroadcastSignalAsync(
            new StreamMessage(fileStream, leaveOpen: true, name, new System.Net.Mime.ContentType(contentType)),
            requiresConfirmation: false
        );
    }

    public async Task<bool> PullFileIfMissingAsync(string id, CancellationToken ct)
    {
        var (exists, _) = await _files.TryGetAsync(id, ct);
        if (exists) return true;

        // Candidats: leader d'abord, puis autres membres
        var candidates = new List<ISubscriber>();

        if (_bus.Leader is { } leader)
            candidates.Add(leader);

        foreach (var m in _bus.Members)
        {
            if (!candidates.Contains(m))
                candidates.Add(m);
        }

        foreach (var member in candidates)
        {
            try
            {
                var ok = await member.SendMessageAsync(
                    new TextMessage(id, MessageNames.FileGetRequest),
                    async (response, token) =>
                    {
                        try
                        {
                            // Erreur renvoyée en texte
                            if (response is TextMessage txt && txt.Name.EndsWith(".error", StringComparison.Ordinal))
                                return false;

                            // Réponse attendue: DataTransferObject (StreamMessage implémente IDataTransferObject)
                            if (response is not IDataTransferObject dto)
                                return false;

                            await _files.WriteFromDataTransferObjectAsync(
                                id,
                                dto,
                                chunkSize: 64 * 1024,
                                token
                            );

                            return true;
                        }
                        finally
                        {
                            (response as IDisposableMessage)?.Dispose();
                        }
                    },
                    ct
                );

                if (ok) return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pull from member failed");
            }
        }

        return false;
    }

    public async Task<List<FileMeta>> PullMetaDumpFromLeaderAsync(CancellationToken ct)
    {
        if (_bus.Leader is null)
            return new();

        try
        {
            var json = await _bus.Leader.SendMessageAsync(
                new TextMessage("", MessageNames.MetaDumpRequest),
                async (response, token) =>
                {
                    try { return await response.ReadAsTextAsync(token); }
                    finally { (response as IDisposableMessage)?.Dispose(); }
                },
                ct
            );

            var metas = JsonSerializer.Deserialize<List<FileMeta>>(json) ?? new();
            return metas.Where(m => !m.IsExpired).ToList();
        }
        catch
        {
            return new();
        }
    }

    public IResult? TryRedirectToLeaderForWrite(HttpContext ctx)
    {
        if (_bus.Leader is null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var leaderEndpoint = TryGetLeaderEndpoint(_bus.Leader);
        if (leaderEndpoint is null)
            return null;

        // Si on est déjà sur le leader (heuristique par port)
        if (ctx.Request.Host.Port is int p && p == leaderEndpoint.Port)
            return null;

        var uri = new UriBuilder
        {
            Scheme = ctx.Request.Scheme,
            Host = leaderEndpoint.Address.ToString(),
            Port = leaderEndpoint.Port,
            Path = ctx.Request.Path,
            Query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : ""
        }.Uri;

        return Results.Redirect(uri.ToString(), permanent: false, preserveMethod: true);
    }

    private static IPEndPoint? TryGetLeaderEndpoint(ISubscriber leader)
    {
        // ISubscriber est aussi un membre de cluster. EndPoint est souvent IPEndPoint en dev.
        var epProp = leader.GetType().GetProperty("EndPoint");
        if (epProp?.GetValue(leader) is EndPoint ep)
        {
            return ep switch
            {
                IPEndPoint ip => ip,
                DnsEndPoint dns => new IPEndPoint(IPAddress.Loopback, dns.Port),
                _ => null
            };
        }

        return null;
    }

    private static class Keys
    {
        public const string MetaPrefix = "filemeta:";
        public static string Meta(string id) => $"{MetaPrefix}{id}";
    }
}
