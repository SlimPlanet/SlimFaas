using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SlimFaasMcpGateway.Audit;
using SlimFaasMcpGateway.Data;
using SlimFaasMcpGateway.Data.Entities;
using SlimFaasMcpGateway.Dto;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Services;

public interface IAuditService
{
    Task<AuditAppendResult> AppendAsync(string entityType, Guid entityId, string author, string entityJsonSnapshot, CancellationToken ct);
    Task<IReadOnlyList<AuditHistoryItemDto>> ListAsync(string entityType, Guid entityId, CancellationToken ct);
    Task<string> ReconstructJsonAsync(string entityType, Guid entityId, int index, CancellationToken ct);
    Task<AuditDiffDto> DiffAsync(string entityType, Guid entityId, int fromIndex, int toIndex, CancellationToken ct);
    Task<AuditTextDiffDto> TextDiffAsync(string entityType, Guid entityId, int fromIndex, int toIndex, CancellationToken ct);
}

public sealed record AuditAppendResult(int Index, long ModifiedAtUtc, string Author);

public sealed class AuditService : IAuditService
{
    private readonly GatewayDbContext _db;
    private readonly TimeProvider _time;

    public AuditService(GatewayDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<AuditAppendResult> AppendAsync(string entityType, Guid entityId, string author, string entityJsonSnapshot, CancellationToken ct)
    {
        author = string.IsNullOrWhiteSpace(author) ? "unknown" : author.Trim();

        var last = await _db.AuditRecords
            .Where(x => x.EntityType == entityType && x.EntityId == entityId)
            .OrderByDescending(x => x.Index)
            .FirstOrDefaultAsync(ct);

        var nowUnix = _time.GetUtcNow().ToUnixTimeSeconds();

        if (last is null)
        {
            var rec0 = new AuditRecord
            {
                Id = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = entityId,
                Index = 0,
                ModifiedAtUtc = nowUnix,
                Author = author,
                FullJsonSnapshot = entityJsonSnapshot,
                JsonPatch = null
            };
            _db.AuditRecords.Add(rec0);
            await _db.SaveChangesAsync(ct);
            return new AuditAppendResult(0, nowUnix, author);
        }

        // Reconstruct last snapshot JSON to compute diff vs new snapshot
        var lastJson = await ReconstructJsonAsync(entityType, entityId, last.Index, ct);

        var beforeNode = JsonNode.Parse(lastJson);
        var afterNode = JsonNode.Parse(entityJsonSnapshot);

        if (beforeNode is null || afterNode is null)
            throw new InvalidOperationException("Invalid JSON snapshots for audit.");

        var ops = JsonPatch.Create(beforeNode, afterNode);
        var patchJson = JsonPatch.Serialize(ops);

        var nextIndex = last.Index + 1;
        var rec = new AuditRecord
        {
            Id = Guid.NewGuid(),
            EntityType = entityType,
            EntityId = entityId,
            Index = nextIndex,
            ModifiedAtUtc = nowUnix,
            Author = author,
            FullJsonSnapshot = null,
            JsonPatch = patchJson
        };
        _db.AuditRecords.Add(rec);
        await _db.SaveChangesAsync(ct);

        return new AuditAppendResult(nextIndex, nowUnix, author);
    }

    public async Task<IReadOnlyList<AuditHistoryItemDto>> ListAsync(string entityType, Guid entityId, CancellationToken ct)
    {
        var list = await _db.AuditRecords
            .Where(x => x.EntityType == entityType && x.EntityId == entityId)
            .OrderBy(x => x.Index)
            .Select(x => new AuditHistoryItemDto(x.Index, x.ModifiedAtUtc, x.Author))
            .ToListAsync(ct);

        return list;
    }

    public async Task<string> ReconstructJsonAsync(string entityType, Guid entityId, int index, CancellationToken ct)
    {
        if (index < 0) throw new ApiException(400, "Audit index must be >= 0.");

        var all = await _db.AuditRecords
            .Where(x => x.EntityType == entityType && x.EntityId == entityId && x.Index <= index)
            .OrderBy(x => x.Index)
            .ToListAsync(ct);

        if (all.Count == 0)
            throw new ApiException(404, "Audit history not found.");

        var rootRec = all[0];
        if (rootRec.Index != 0 || string.IsNullOrWhiteSpace(rootRec.FullJsonSnapshot))
            throw new InvalidOperationException("Corrupted audit history: missing root snapshot.");

        var node = JsonNode.Parse(rootRec.FullJsonSnapshot);
        if (node is null) throw new InvalidOperationException("Invalid root JSON snapshot.");

        for (var i = 1; i < all.Count; i++)
        {
            var rec = all[i];
            if (string.IsNullOrWhiteSpace(rec.JsonPatch))
                throw new InvalidOperationException($"Corrupted audit patch at index {rec.Index}.");

            var ops = JsonPatch.Deserialize(rec.JsonPatch);
            node = JsonPatch.Apply(node, ops);
        }

        return node.ToJsonString(AppJsonOptions.Default);
    }

    public async Task<AuditDiffDto> DiffAsync(string entityType, Guid entityId, int fromIndex, int toIndex, CancellationToken ct)
    {
        if (fromIndex < 0 || toIndex < 0) throw new ApiException(400, "Audit indices must be >= 0.");

        var history = await _db.AuditRecords
            .Where(x => x.EntityType == entityType && x.EntityId == entityId && (x.Index == fromIndex || x.Index == toIndex))
            .ToListAsync(ct);

        var fromMeta = history.FirstOrDefault(x => x.Index == fromIndex);
        var toMeta = history.FirstOrDefault(x => x.Index == toIndex);

        if (fromMeta is null || toMeta is null)
            throw new ApiException(404, "One or both audit indices not found.");

        var fromJson = await ReconstructJsonAsync(entityType, entityId, fromIndex, ct);
        var toJson = await ReconstructJsonAsync(entityType, entityId, toIndex, ct);

        var fromNode = JsonNode.Parse(fromJson);
        var toNode = JsonNode.Parse(toJson);

        if (fromNode is null || toNode is null)
            throw new InvalidOperationException("Invalid reconstructed JSON for diff.");

        var ops = JsonPatch.Create(fromNode, toNode);

        return new AuditDiffDto(
            new AuditSideDto(fromIndex, fromMeta.ModifiedAtUtc, fromMeta.Author),
            new AuditSideDto(toIndex, toMeta.ModifiedAtUtc, toMeta.Author),
            ops
        );
    }

    public async Task<AuditTextDiffDto> TextDiffAsync(string entityType, Guid entityId, int fromIndex, int toIndex, CancellationToken ct)
    {
        if (fromIndex < 0 || toIndex < 0) throw new ApiException(400, "Audit indices must be >= 0.");

        var history = await _db.AuditRecords
            .Where(x => x.EntityType == entityType && x.EntityId == entityId && (x.Index == fromIndex || x.Index == toIndex))
            .ToListAsync(ct);

        var fromMeta = history.FirstOrDefault(x => x.Index == fromIndex);
        var toMeta = history.FirstOrDefault(x => x.Index == toIndex);

        if (fromMeta is null || toMeta is null)
            throw new ApiException(404, "One or both audit indices not found.");

        var fromJson = await ReconstructJsonAsync(entityType, entityId, fromIndex, ct);
        var toJson = await ReconstructJsonAsync(entityType, entityId, toIndex, ct);

        var fromNode = JsonNode.Parse(fromJson);
        var toNode = JsonNode.Parse(toJson);

        if (fromNode is null || toNode is null)
            throw new InvalidOperationException("Invalid reconstructed JSON for diff.");

        var unifiedDiff = JsonPatch.CreateTextDiff(fromNode, toNode);

        return new AuditTextDiffDto(
            new AuditSideDto(fromIndex, fromMeta.ModifiedAtUtc, fromMeta.Author),
            new AuditSideDto(toIndex, toMeta.ModifiedAtUtc, toMeta.Author),
            unifiedDiff
        );
    }
}
