// DataSetRoutes.cs

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using DotNext;
using MemoryPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimData.Expiration;

namespace SlimFaas;

public static class DataFileRoutes
{
    /// <summary>
    /// Redirection automatique vers le leader (écritures).
    /// Appelle ceci dans Program.cs : app.UseDataSetLeaderRedirect();
    /// </summary>
    public static WebApplication UseDataSetLeaderRedirect(this WebApplication app)
    {
        // Writes -> leader (DotNext HTTP consensus protocol handler)
        app.UseConsensusProtocolHandler()
           .RedirectToLeader("/data/file")
           .RedirectToLeader("/data/file/"); // couvre /data/set/{id} si RedirectToLeader fait un match par prefix

        return app;
    }

    /// <summary>
    /// Appelle ceci dans Program.cs : app.MapDataSetRoutes();
    /// </summary>
    public static IEndpointRouteBuilder MapDataSetRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/data/files")
            .AddEndpointFilter<DataVisibilityEndpointFilter>();

        group.MapPost("", DataFileHandlers.PostAsync);
        group.MapGet("/{elementId}", DataFileHandlers.GetAsync);
        group.MapDelete("/{elementId}", DataFileHandlers.DeleteAsync);
        group.MapGet("", DataFileHandlers.ListFilesAsync);

        return endpoints;
    }

    // ------------------------------------------------------------
    // Handlers (testables unitairement)
    // ------------------------------------------------------------
    public static class DataFileHandlers
    {
                // Doit matcher SlimDataInterpreter
        private const string TimeToLiveSuffix = "${slimfaas-timetolive}$";

        private const string MetaPrefix = "data:file:";
        private const string MetaSuffix = ":meta";

        public static IResult ListFilesAsync(ISupplier<SlimDataPayload> state)
        {
            var payload = state.Invoke();

            var keyValues = payload.KeyValues
                ?? ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;

            var list = new List<DataFileEntry>(capacity: 128);

            foreach (var kv in keyValues)
            {
                var metaKey = kv.Key;

                if (!metaKey.StartsWith(MetaPrefix, StringComparison.Ordinal) ||
                    !metaKey.EndsWith(MetaSuffix, StringComparison.Ordinal))
                    continue;

                var id = metaKey.Substring(
                    MetaPrefix.Length,
                    metaKey.Length - MetaPrefix.Length - MetaSuffix.Length);

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var ttlKey = metaKey + TimeToLiveSuffix;

                long expireAtUtcTicks = -1;
                if(keyValues.TryGetValue(ttlKey, out var ttlBytes))
                    SlimDataExpirationCleaner.TryReadInt64(ttlBytes, out expireAtUtcTicks);

                list.Add(new DataFileEntry(
                    Id: id,
                    ExpireAtUtcTicks: expireAtUtcTicks
                    ));
            }


            return Results.Ok(list);
        }


        // POST /data/file?id=...&ttl=...
        public static async Task<IResult> PostAsync(
            HttpContext context,
            [FromQuery] string? id,
            [FromQuery] long? ttl, // milliseconds
            IClusterFileSync fileSync,
            IDatabaseService db,
            CancellationToken ct)
        {
            var elementId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;

            if (!IdValidator.IsSafeId(elementId))
                return Results.BadRequest("Invalid id.");

            // Snippet demandé
            var contentType = context.Request.ContentType ?? "application/octet-stream";
            var fileName = TryGetFileName(context.Request.Headers["Content-Disposition"].ToString());

            Stream contentStream;
            string? actualContentType = null;
            string? actualFileName = null;

            // “Vrai upload” : multipart/form-data -> IFormFile
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(ct);
                var file = form.Files.FirstOrDefault();
                if (file is null)
                    return Results.BadRequest("No file found in multipart form-data.");

                actualFileName = string.IsNullOrWhiteSpace(file.FileName) ? null : file.FileName;
                actualContentType = string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType;

                contentStream = file.OpenReadStream();
            }
            else
            {
                // fallback : raw body (application/octet-stream)
                contentStream = context.Request.Body;
            }

            var finalContentType = actualContentType ?? contentType ?? "application/octet-stream";
            var finalFileName = actualFileName ?? fileName ?? elementId;

            // Persiste localement + calcule sha/len + announce-only cluster
            var put = await fileSync.BroadcastFilePutAsync(
                id: elementId,
                content: contentStream,
                contentType: finalContentType,
                overwrite: true,
                ct: ct);

            // Metadata RAFT (SlimData)
            var meta = new DataSetMetadata(
                Sha256Hex: put.Sha256Hex,
                Length: put.Length,
                ContentType: put.ContentType,
                FileName: finalFileName);

            var metaKey = MetaKey(elementId);
            var metaBytes = MemoryPackSerializer.Serialize(meta);

            await db.SetAsync(metaKey, metaBytes, ttl);

            // byte[] => element_id (ici: return element_id)
            return Results.Text(elementId);
        }

        // GET /data/file/{elementId}
        public static async Task<IResult> GetAsync(
            string elementId,
            IClusterFileSync fileSync,
            IDatabaseService db,
            CancellationToken ct)
        {

            if (!IdValidator.IsSafeId(elementId))
                return Results.BadRequest("Invalid id.");

            var metaKey = MetaKey(elementId);
            var metaBytes = await db.GetAsync(metaKey);
            if (metaBytes is null || metaBytes.Length == 0)
                return Results.NotFound();

            DataSetMetadata? meta;
            try
            {
                meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(metaBytes);
            }
            catch
            {
                return Results.Problem("Corrupted metadata", statusCode: 500);
            }

            var pulled = await fileSync.PullFileIfMissingAsync(elementId, meta?.Sha256Hex ?? "", ct);
            if (pulled.Stream is null)
                return Results.NotFound();

            return Results.File(
                fileStream: pulled.Stream,
                contentType: string.IsNullOrWhiteSpace(meta?.ContentType) ? "application/octet-stream" : meta.ContentType,
                fileDownloadName: string.IsNullOrWhiteSpace(meta?.FileName) ? elementId : meta.FileName);
        }

        // DELETE /data/set/{elementId}
        public static async Task<IResult> DeleteAsync(
            string elementId,
            IDatabaseService db,
            CancellationToken ct)
        {
            if (!IdValidator.IsSafeId(elementId))
                return Results.BadRequest("Invalid id.");
            await db.DeleteAsync(MetaKey(elementId));
            return Results.NoContent();
        }

        private static string MetaKey(string elementId) => $"data:file:{elementId}:meta";

        private static string? TryGetFileName(string? contentDisposition)
        {
            if (string.IsNullOrWhiteSpace(contentDisposition))
                return null;

            if (!ContentDispositionHeaderValue.TryParse(contentDisposition, out var cd))
                return null;

            var fn = cd.FileNameStar.HasValue ? cd.FileNameStar.Value
                : cd.FileName.HasValue ? cd.FileName.Value
                : null;

            return string.IsNullOrWhiteSpace(fn) ? null : fn.Trim('"');
        }
    }
}

// ------------------------------------------------------------
// MemoryPack metadata
// ------------------------------------------------------------
[MemoryPackable]
public partial record DataSetMetadata(
    string Sha256Hex,
    long Length,
    string ContentType,
    string FileName);

public sealed record DataFileEntry(
    string Id,
    long ExpireAtUtcTicks
);


static class IdValidator
{
    private static readonly System.Text.RegularExpressions.Regex Rx = new("^[A-Za-z0-9._-]{1,200}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static bool IsSafeId(string id) => Rx.IsMatch(id);
}

[JsonSerializable(typeof(List<DataFileEntry>))]
internal partial class DataFileRoutesJsonContext : JsonSerializerContext
{
}
