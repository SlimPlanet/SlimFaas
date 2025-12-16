using ClusterFileDemoProdish.Models;
using ClusterFileDemoProdish.Storage;
using DotNext.Net.Cluster.Messaging;
using System.Net;
using System.Text;
using System.Text.Json;
using DotNext.IO;

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
        await _kv.SetAsync(Keys.Meta(meta.Id), Encoding.UTF8.GetBytes(json), timeToLiveMilliseconds: null);
        await _bus.SendBroadcastSignalAsync(new TextMessage(json, MessageNames.MetaUpsert), requiresConfirmation: false);
    }

    public async Task BroadcastMetaDeleteAsync(string id, CancellationToken ct)
    {
        await _kv.DeleteAsync(Keys.Meta(id));
        await _files.DeleteAsync(id, ct);
        await _bus.SendBroadcastSignalAsync(new TextMessage(id, MessageNames.MetaDelete), requiresConfirmation: false);
    }

    public async Task BroadcastFilePutAsync(string id, Stream fileStream, string contentType, bool overwrite, CancellationToken ct)
    {
        var name = MessageNames.FilePutPrefix + id;
        await _bus.SendBroadcastSignalAsync(
            new StreamMessage(fileStream, true, name, new System.Net.Mime.ContentType(contentType)),
            requiresConfirmation: false
        );
    }

    public async Task<bool> PullFileIfMissingAsync(string id, CancellationToken ct)
    {
        var (exists, _) = await _files.TryGetAsync(id, ct);
        if (exists) return true;

        // Candidates: leader first, then other members.
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
                            if (response is TextMessage txt && txt.Name.EndsWith(".error", StringComparison.Ordinal))
                                return false;

                            if (response is not IDataTransferObject dto)
                                return false;

                            await _files.WriteFromDataTransferObjectAsync(id, dto, chunkSize: 64 * 1024, token);
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

        // Heuristic good for local dev: each node uses a different port.
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
        // ISubscriber is also a cluster member. EndPoint is typically IPEndPoint in dev.
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
