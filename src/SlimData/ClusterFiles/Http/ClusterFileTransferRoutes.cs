using System.Net.Mime;

namespace SlimData.ClusterFiles.Http;

public static class ClusterFileTransferRoutes
{
    public static IEndpointRouteBuilder MapClusterFileTransferRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/cluster/files");

        group.MapMethods("/{id}", new[] { "HEAD" }, HeadAsync);
        group.MapGet("/{id}", GetAsync);

        return endpoints;
    }

    private static async Task<IResult> HeadAsync(
        HttpContext ctx,
        string id,
        string? sha,
        IFileRepository repo,
        ILoggerFactory lf,
        CancellationToken ct)
    {
        var log = lf.CreateLogger("ClusterFilesTransfer");

        if (!IdValidator.IsSafeId(id))
            return Results.BadRequest("Invalid id.");

        if (string.IsNullOrWhiteSpace(sha))
            return Results.BadRequest("sha is required.");

        var meta = await repo.TryGetMetadataAsync(id, ct).ConfigureAwait(false);
        if (meta is null)
            return Results.NotFound();

        if (!meta.Sha256Hex.Equals(sha, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        // HEAD: headers only
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.ContentType = meta.ContentType ?? MediaTypeNames.Application.Octet;
        ctx.Response.ContentLength = meta.Length;
        ctx.Response.Headers.ETag = $"\"{meta.Sha256Hex}\"";
        if (meta.ExpireAtUtcTicks is { } exp && exp > 0)
            ctx.Response.Headers["X-SlimFaas-ExpireAtUtcTicks"] = exp.ToString();

        log.LogInformation("HEAD ok. Id={Id} Len={Len}", id, meta.Length);
        return Results.Ok();
    }

    private static async Task<IResult> GetAsync(
        HttpContext ctx,
        string id,
        string? sha,
        IFileRepository repo,
        ILoggerFactory lf,
        CancellationToken ct)
    {
        var log = lf.CreateLogger("ClusterFilesTransfer");

        if (!IdValidator.IsSafeId(id))
            return Results.BadRequest("Invalid id.");

        if (string.IsNullOrWhiteSpace(sha))
            return Results.BadRequest("sha is required.");

        var meta = await repo.TryGetMetadataAsync(id, ct).ConfigureAwait(false);
        if (meta is null)
            return Results.NotFound();

        if (!meta.Sha256Hex.Equals(sha, StringComparison.OrdinalIgnoreCase))
            return Results.NotFound();

        var stream = await repo.OpenReadAsync(id, ct).ConfigureAwait(false);

        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.Headers.ETag = $"\"{meta.Sha256Hex}\"";
        if (meta.ExpireAtUtcTicks is { } exp && exp > 0)
            ctx.Response.Headers["X-SlimFaas-ExpireAtUtcTicks"] = exp.ToString();

        log.LogInformation("GET streaming (range enabled). Id={Id} Len={Len}", id, meta.Length);

        return Results.File(
            fileStream: stream,
            contentType: string.IsNullOrWhiteSpace(meta.ContentType) ? MediaTypeNames.Application.Octet : meta.ContentType,
            fileDownloadName: null,
            lastModified: null,
            entityTag: null,
            enableRangeProcessing: true);
    }

    // Reprend ton IdValidator sinon
    private static class IdValidator
    {
        private static readonly System.Text.RegularExpressions.Regex Rx =
            new("^[A-Za-z0-9._-]{1,200}$", System.Text.RegularExpressions.RegexOptions.Compiled);
        public static bool IsSafeId(string id) => Rx.IsMatch(id);
    }
}
