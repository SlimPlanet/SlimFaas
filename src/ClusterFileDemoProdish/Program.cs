using System.Net;
using ClusterFileDemoProdish.Cluster;
using ClusterFileDemoProdish.Models;
using ClusterFileDemoProdish.Options;
using ClusterFileDemoProdish.Storage;
using ClusterFileDemoProdish.Workers;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Http.Features;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;

var builder = WebApplication.CreateBuilder(args);

// --- Déduire l'URL publique du node depuis ASPNETCORE_URLS (celle que ton run-local.sh pose) ---
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
           ?? builder.Configuration["ASPNETCORE_URLS"]
           ?? builder.Configuration["urls"]
           ?? "http://127.0.0.1:3262";

var firstUrl = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
var publicUri = new Uri(firstUrl);

// --- IMPORTANT : définir explicitement l’endpoint public pour DotNext ---
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    // Bind direct sur HttpClusterMemberConfiguration.PublicEndPoint (sinon DotNext peut ne pas “trouver” le local node)
    ["member-config:publicEndPoint"] = publicUri.ToString(),

    // Optionnel mais utile: aide la détection / redirections
    ["member-config:hostAddressHint"] = publicUri.Host,
    ["member-config:port"] = publicUri.Port.ToString()
});

// --- Si l’environnement réseau est bizarre, on force aussi la sélection du node local ---
builder.Services.AddSingleton<IClusterMemberLifetime>(new PortBasedLocalMemberLifetime(publicUri.Port));

// --- DotNext cluster ---
builder.JoinCluster("member-config");
// -------------------- Options --------------------
builder.Services.Configure<FileStorageOptions>(builder.Configuration.GetSection("fileStorage"));

// -------------------- SlimData-like KV + File repo --------------------
builder.Services.AddSingleton<SlimDataKvStore>();
builder.Services.AddSingleton<IKvStore>(sp => sp.GetRequiredService<SlimDataKvStore>());
builder.Services.AddSingleton<IFileRepository, FileRepository>();


// -------------------- Messaging channel --------------------
builder.Services.AddSingleton<ClusterMessagingChannel>();
builder.Services.AddSingleton<IClusterFileSync, ClusterFileSync>();
builder.Services.AddHostedService<ClusterMessagingRegistrationService>();

// -------------------- Workers --------------------
builder.Services.AddHostedService<ExpiredFileCleanupWorker>();
builder.Services.AddHostedService<StartupSyncWorker>();

var app = builder.Build();

app.UseConsensusProtocolHandler(); // must be before auth/other middleware

// For large binary payloads
app.Use(async (ctx, next) =>
{
    var feature = ctx.Features.Get<IHttpBodyControlFeature>();
    if (feature is not null)
        feature.AllowSynchronousIO = false;
    await next();
});

app.MapGet("/healthz", () => Results.Ok(new { ok = true, node = Environment.MachineName }));

// POST /data/file?key=element_id&ttl=8908980890
app.MapPost("/data/file", async (HttpContext ctx, IKvStore kv, IClusterFileSync sync, IFileRepository files, CancellationToken ct) =>
{
    var redirect = sync.TryRedirectToLeaderForWrite(ctx);
    if (redirect is not null)
        return redirect;

    var key = ctx.Request.Query["key"].ToString();
    var ttlRaw = ctx.Request.Query["ttl"].ToString();

    var id = string.IsNullOrWhiteSpace(key) ? Guid.NewGuid().ToString("N") : key.Trim();
    if (!IdValidator.IsSafeId(id))
        return Results.BadRequest("Invalid key. Allowed: [A-Za-z0-9._-], length 1..200");

    // overwrite si la meta existe déjà
    var overwrite = (await kv.GetAsync($"filemeta:{id}")) is not null;

    long? ttlMs = null;
    if (!string.IsNullOrWhiteSpace(ttlRaw))
    {
        if (!long.TryParse(ttlRaw, out var ttlParsed) || ttlParsed < 0)
            return Results.BadRequest("Invalid ttl. Expected a non-negative integer (milliseconds).");
        ttlMs = ttlParsed == 0 ? null : ttlParsed;
    }

    var contentType = ctx.Request.ContentType ?? "application/octet-stream";
    var fileName = TryGetFileName(ctx.Request.Headers["Content-Disposition"].ToString());

    var (meta, path) = await files.SaveAsync(id, ctx.Request.Body, contentType, fileName, ttlMs, ct);

    await sync.BroadcastMetaUpsertAsync(meta, ct);

    await using (var fs = new FileStream(path, new FileStreamOptions
                 {
                     Mode = FileMode.Open,
                     Access = FileAccess.Read,
                     Share = FileShare.Read,
                     Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                 }))
    {
        await sync.BroadcastFilePutAsync(id, fs, meta.ContentType, overwrite, ct);
    }

    return Results.Ok(meta.Id);
});

// GET /data/file/{element_id}
app.MapGet("/data/file/{id}", async (string id, HttpContext ctx, IKvStore kv, IFileRepository files, IClusterFileSync sync, CancellationToken ct) =>
{
    if (!IdValidator.IsSafeId(id))
        return Results.BadRequest("Invalid id.");

    var metaBytes = await kv.GetAsync($"filemeta:{id}");
    if (metaBytes is null)
        return Results.NotFound();

    FileMeta? meta;
    try
    {
        meta = JsonSerializer.Deserialize<FileMeta>(Encoding.UTF8.GetString(metaBytes));
    }
    catch
    {
        return Results.Problem("Corrupted metadata", statusCode: 500);
    }

    if (meta is null || meta.IsExpired)
        return Results.NotFound();

    var (exists, path) = await files.TryGetAsync(id, ct);
    // Si le fichier est là mais "pas le bon" (overwrite raté), on force un pull
    if (exists)
    {
        var len = new FileInfo(path).Length;
        if (len != meta.Size)
            exists = false;
    }

    if (!exists)
    {
        var ok = await sync.PullFileIfMissingAsync(id, ct);
        if (!ok)
            return Results.NotFound();

        (exists, path) = await files.TryGetAsync(id, ct);
        if (!exists)
            return Results.NotFound();
    }

    if (!string.IsNullOrWhiteSpace(meta.FileName))
        ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{meta.FileName}\"";

    return Results.File(path, contentType: meta.ContentType, enableRangeProcessing: true);

});

// DELETE /data/file/{element_id}
app.MapDelete("/data/file/{id}", async (string id, HttpContext ctx, IClusterFileSync sync, CancellationToken ct) =>
{
    var redirect = sync.TryRedirectToLeaderForWrite(ctx);
    if (redirect is not null)
        return redirect;

    if (!IdValidator.IsSafeId(id))
        return Results.BadRequest("Invalid id.");

    await sync.BroadcastMetaDeleteAsync(id, ct);
    return Results.NoContent();
});

app.Run();

static string? TryGetFileName(string contentDisposition)
{
    if (string.IsNullOrWhiteSpace(contentDisposition)) return null;

    var parts = contentDisposition.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var p in parts)
    {
        if (p.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            return p.Substring("filename=".Length).Trim().Trim('"');
    }

    return null;
}

static class IdValidator
{
    private static readonly System.Text.RegularExpressions.Regex Rx = new("^[A-Za-z0-9._-]{1,200}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static bool IsSafeId(string id) => Rx.IsMatch(id);
}

internal sealed class PortBasedLocalMemberLifetime : IClusterMemberLifetime
{
    private readonly int _port;

    public PortBasedLocalMemberLifetime(int port) => _port = port;

    public Func<IRaftClusterMember, CancellationToken, ValueTask<bool>>? LocalMemberSelector
        => (member, _) =>
        {
            var ep = member.EndPoint;
            return new ValueTask<bool>(TryGetPort(ep, out var p) && p == _port);
        };

    // Required by the interface in your DotNext version
    public void OnStart(IRaftCluster cluster, IDictionary<string, string> tags)
    {
        // no-op
    }

    public void OnStop(IRaftCluster cluster)
    {
        // no-op
    }

    private static bool TryGetPort(EndPoint? ep, out int port)
    {
        port = 0;
        if (ep is null) return false;

        if (ep is IPEndPoint ip)
        {
            port = ip.Port;
            return true;
        }

        if (ep is DnsEndPoint dns)
        {
            port = dns.Port;
            return true;
        }

        // Fallback: parse "http://127.0.0.1:3262" etc.
        var s = ep.ToString();
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            port = uri.Port;
            return true;
        }

        return false;
    }
}
