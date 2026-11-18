using System.Collections.Immutable;
using MemoryPack;
using System.Security.Cryptography;
using SlimData.Commands;

namespace SlimData;

public enum FileAdmissionAction : byte { Begin, CommitQueue, CommitPublic, Retire, Forget }
public enum FileAdmissionPhase : byte { Pending, Active, Retired }

[MemoryPackable]
public sealed partial class FileReplicaProof
{
    public string FileId { get; set; } = "";
    public string Sha256Hex { get; set; } = "";
    public long Length { get; set; }
    public string NodeIdentity { get; set; } = "";
    public string StorageIdentity { get; set; } = "";
}

[MemoryPackable]
public sealed partial class FileAdmissionMutation
{
    public FileAdmissionAction Action { get; set; }
    public string FileId { get; set; } = "";
    public string Token { get; set; } = "";
    public long DeadlineUtcTicks { get; set; }
    public int RequiredCopies { get; set; } = 1;
    public string Sha256Hex { get; set; } = "";
    public long Length { get; set; }
    public string MetadataKey { get; set; } = "";
    public byte[] Metadata { get; set; } = [];
    public string QueueKey { get; set; } = "";
    public string ElementId { get; set; } = "";
    public byte[] Value { get; set; } = [];
    public int RetryTimeoutSeconds { get; set; }
    public int[] Retries { get; set; } = [];
    public int[] HttpStatusRetries { get; set; } = [];
    public string AliasKey { get; set; } = "";
    public byte[] AliasValue { get; set; } = [];
    public long? AliasExpireAtUtcTicks { get; set; }
    public FileReplicaProof[] Copies { get; set; } = [];
}

[MemoryPackable]
public sealed partial class FileAdmissionEntry
{
    public string FileId { get; set; } = "";
    public string Token { get; set; } = "";
    public long DeadlineUtcTicks { get; set; }
    public int RequiredCopies { get; set; }
    public FileAdmissionPhase Phase { get; set; }
    public string Sha256Hex { get; set; } = "";
    public long Length { get; set; }
    public string MetadataKey { get; set; } = "";
    public string QueueKey { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string AliasKey { get; set; } = "";
    public byte[] AliasValue { get; set; } = [];
    public long? AliasExpireAtUtcTicks { get; set; }
    public byte[] PublicationHash { get; set; } = [];
    public FileReplicaProof[] Copies { get; set; } = [];
}

[MemoryPackable]
public sealed partial class FileAdmissionResult
{
    public bool Applied { get; set; }
    public string? Error { get; set; }
    public FileAdmissionPhase? Phase { get; set; }
}

/// <summary>Durable pins and atomic publication for immutable physical file IDs.</summary>
public static class FileAdmissionStore
{
    public const string IndexKey = "\0slimdata:file-admissions";
    internal const string ClockKey = "\0slimdata:file-admission-clock";
    private const long MaximumAdmissionTicks = 10 * TimeSpan.TicksPerMinute;

    internal static bool IsReservedKey(string key) => key is IndexKey or ClockKey;

    public static FileAdmissionEntry? Find(SlimDataPayload payload, string fileId) =>
        payload.Hashsets.TryGetValue(IndexKey, out var entries) && entries.TryGetValue(fileId, out var value)
            ? MemoryPackPayload.Deserialize<FileAdmissionEntry>(value.Span)
            : null;

    public static bool IsReferenced(SlimDataPayload payload, FileAdmissionEntry entry, long nowTicks)
    {
        if (entry.Phase == FileAdmissionPhase.Pending)
            return entry.DeadlineUtcTicks > nowTicks;
        if (entry.Phase != FileAdmissionPhase.Active)
            return false;
        if (!string.IsNullOrEmpty(entry.QueueKey))
        {
            if (payload.Queues.TryGetValue(entry.QueueKey, out var queue))
                foreach (var item in queue)
                    if (item.Id == entry.ElementId) return true;
            foreach (string owner in DeadLetterKeys.GetPayloadOwnerIds(payload))
                if (owner == entry.ElementId) return true;
        }
        return !string.IsNullOrEmpty(entry.AliasKey) &&
               (!entry.AliasExpireAtUtcTicks.HasValue || entry.AliasExpireAtUtcTicks.Value > nowTicks) &&
               payload.KeyValues.TryGetValue(entry.AliasKey, out var alias) &&
               alias.Span.SequenceEqual(entry.AliasValue);
    }

    internal static void Validate(FileAdmissionMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!Enum.IsDefined(mutation.Action) || !Identifier(mutation.FileId) ||
            !Guid.TryParseExact(mutation.Token, "N", out _))
            throw new InvalidDataException("Invalid file admission identity or action.");
        if (mutation.Action == FileAdmissionAction.Begin)
        {
            if (mutation.DeadlineUtcTicks <= 0 || mutation.DeadlineUtcTicks > DateTime.MaxValue.Ticks ||
                mutation.RequiredCopies is < 1 or > 2)
                throw new InvalidDataException("Invalid file admission deadline or copy policy.");
            return;
        }
        if (mutation.Action is FileAdmissionAction.Retire or FileAdmissionAction.Forget) return;
        if (!Identifier(mutation.MetadataKey) || mutation.Metadata.Length == 0 ||
            mutation.Metadata.Length > SlimDataCommandCodec.MaxValueBytes ||
            !ValidHash(mutation.Sha256Hex) || mutation.Length < 0 || mutation.Copies.Length is < 1 or > 2)
            throw new InvalidDataException("Invalid file admission metadata or copy proofs.");
        foreach (var proof in mutation.Copies)
            if (proof is null || !Identifier(proof.NodeIdentity) ||
                !Guid.TryParseExact(proof.StorageIdentity, "N", out var volume) || volume == Guid.Empty ||
                proof.FileId != mutation.FileId || proof.Length != mutation.Length ||
                !string.Equals(proof.Sha256Hex, mutation.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A persisted-copy proof does not match the file admission.");
        if (mutation.Action == FileAdmissionAction.CommitQueue)
        {
            if (!mutation.QueueKey.StartsWith("Queue:", StringComparison.Ordinal) ||
                !Identifier(mutation.QueueKey) || !Identifier(mutation.ElementId) ||
                mutation.Value.Length > SlimDataCommandCodec.MaxValueBytes || mutation.RetryTimeoutSeconds <= 0 ||
                mutation.Retries.Length > SlimDataCommandCodec.MaxCollectionCount ||
                mutation.HttpStatusRetries.Length > SlimDataCommandCodec.MaxCollectionCount)
                throw new InvalidDataException("Invalid file admission queue publication.");
        }
        else if (!Identifier(mutation.AliasKey) || mutation.AliasKey == mutation.MetadataKey ||
                 mutation.AliasValue.Length == 0 || mutation.AliasValue.Length > SlimDataCommandCodec.MaxValueBytes ||
                 mutation.AliasExpireAtUtcTicks <= 0 || mutation.AliasExpireAtUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException("Invalid file admission public alias.");
        }
    }

    internal static FileAdmissionResult Apply(FileAdmissionMutation mutation, SlimDataState state, long nowTicks)
    {
        Validate(mutation);
        if (nowTicks <= 0 || nowTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Invalid file admission leader timestamp.");
        var snapshot = state.Capture();
        var staged = new SlimDataState(snapshot.Hashsets, snapshot.KeyValues, snapshot.Queues);
        long floor = 0;
        if (staged.KeyValues.TryGetValue(ClockKey, out var clock))
        {
            if (clock.Length != sizeof(long)) throw new InvalidDataException("Invalid persisted file admission clock.");
            floor = BitConverter.ToInt64(clock.Span);
            if (floor < 0 || floor > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Invalid persisted file admission clock.");
        }
        long now = Math.Max(floor, nowTicks);
        staged.KeyValues = staged.KeyValues.SetItem(ClockKey, BitConverter.GetBytes(now));
        var result = ApplyCore(mutation, staged, now);
        // A reader can observe the pending pin or the published metadata, queue and
        // active reference together. No partially published snapshot is exposed.
        state.Replace(staged.Capture());
        return result;
    }

    private static FileAdmissionResult ApplyCore(FileAdmissionMutation mutation, SlimDataState state, long now)
    {
        var entries = state.Hashsets.GetValueOrDefault(IndexKey) ??
                      ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
        var entry = entries.TryGetValue(mutation.FileId, out var bytes)
            ? MemoryPackPayload.Deserialize<FileAdmissionEntry>(bytes.Span)!
            : null;
        if (mutation.Action == FileAdmissionAction.Begin)
        {
            if (mutation.DeadlineUtcTicks <= now || mutation.DeadlineUtcTicks - now > MaximumAdmissionTicks)
                return Reject("The admission deadline has expired or exceeds ten minutes.", entry);
            if (entry is not null)
                return entry.Token == mutation.Token && entry.Phase == FileAdmissionPhase.Pending &&
                       entry.DeadlineUtcTicks == mutation.DeadlineUtcTicks && entry.RequiredCopies == mutation.RequiredCopies
                    ? Accept(entry)
                    : Reject("The physical file ID already has an admission.", entry);
            entry = new()
            {
                FileId = mutation.FileId, Token = mutation.Token, DeadlineUtcTicks = mutation.DeadlineUtcTicks,
                RequiredCopies = mutation.RequiredCopies, Phase = FileAdmissionPhase.Pending
            };
        }
        else
        {
            // A delayed copy can arrive after the retired pin was collected.
            // Its original deadline, carried in the local file tags, is fenced
            // by the same persisted clock that rejects a delayed Begin.
            if (entry is null && mutation.Action == FileAdmissionAction.Retire &&
                mutation.DeadlineUtcTicks > 0 && mutation.DeadlineUtcTicks <= now)
                return new() { Applied = true, Phase = FileAdmissionPhase.Retired };
            if (entry is null || entry.Token != mutation.Token)
                return Reject("The admission is absent or its token differs.", entry);
            if (mutation.Action == FileAdmissionAction.Forget)
            {
                if (entry.Phase != FileAdmissionPhase.Retired || entry.DeadlineUtcTicks > now)
                    return Reject("Only an expired retired admission can be forgotten.", entry);
                state.Hashsets = state.Hashsets.SetItem(IndexKey, entries.Remove(entry.FileId));
                return Accept(entry);
            }
            if (mutation.Action == FileAdmissionAction.Retire)
            {
                if (IsReferenced(state.CapturePayload(), entry, now))
                    return Reject("The file still has a live admission or owner.", entry);
                entry.Phase = FileAdmissionPhase.Retired;
                if (!string.IsNullOrEmpty(entry.AliasKey) && entry.AliasExpireAtUtcTicks <= now &&
                    state.KeyValues.TryGetValue(entry.AliasKey, out var alias) && alias.Span.SequenceEqual(entry.AliasValue))
                    state.KeyValues = state.KeyValues.Remove(entry.AliasKey).Remove(SlimDataInterpreter.TtlKey(entry.AliasKey));
                if (!string.IsNullOrEmpty(entry.MetadataKey))
                    state.KeyValues = state.KeyValues.Remove(entry.MetadataKey).Remove(SlimDataInterpreter.TtlKey(entry.MetadataKey));
            }
            else
            {
                if (entry.Phase == FileAdmissionPhase.Active)
                    return SamePublication(entry, mutation) ? Accept(entry) : Reject("The admission was published differently.", entry);
                if (entry.Phase != FileAdmissionPhase.Pending || entry.DeadlineUtcTicks <= now)
                    return Reject("The admission is no longer pending.", entry);
                if (!EnoughCopies(mutation.Copies, entry.RequiredCopies))
                    return Reject("Distinct persisted node and volume copies are required.", entry);
                if (state.KeyValues.ContainsKey(mutation.MetadataKey))
                    return Reject("The physical file metadata already exists.", entry);
                if (mutation.Action == FileAdmissionAction.CommitQueue)
                {
                    var queue = state.Queues.GetValueOrDefault(mutation.QueueKey, []);
                    foreach (var item in queue)
                        if (item.Id == mutation.ElementId) return Reject("The queue element already exists.", entry);
                    var element = new QueueElement(mutation.Value, mutation.ElementId, now, mutation.RetryTimeoutSeconds,
                        mutation.Retries.ToImmutableArray(), [], mutation.HttpStatusRetries.ToImmutableHashSet());
                    state.Queues = state.Queues.SetItem(mutation.QueueKey, queue.Add(element));
                    entry.QueueKey = mutation.QueueKey;
                    entry.ElementId = mutation.ElementId;
                }
                else
                {
                    if (mutation.AliasExpireAtUtcTicks <= now)
                        return Reject("The public file has already expired.", entry);
                    state.KeyValues = state.KeyValues.SetItem(mutation.AliasKey, mutation.AliasValue);
                    string ttlKey = SlimDataInterpreter.TtlKey(mutation.AliasKey);
                    state.KeyValues = mutation.AliasExpireAtUtcTicks.HasValue
                        ? state.KeyValues.SetItem(ttlKey, BitConverter.GetBytes(mutation.AliasExpireAtUtcTicks.Value))
                        : state.KeyValues.Remove(ttlKey);
                    entry.AliasKey = mutation.AliasKey;
                    entry.AliasValue = mutation.AliasValue;
                    entry.AliasExpireAtUtcTicks = mutation.AliasExpireAtUtcTicks;
                }
                entry.Phase = FileAdmissionPhase.Active;
                entry.Sha256Hex = mutation.Sha256Hex;
                entry.Length = mutation.Length;
                entry.MetadataKey = mutation.MetadataKey;
                entry.PublicationHash = SHA256.HashData(MemoryPackSerializer.Serialize(mutation));
                entry.Copies = mutation.Copies;
                state.KeyValues = state.KeyValues.SetItem(mutation.MetadataKey, mutation.Metadata);
            }
        }
        state.Hashsets = state.Hashsets.SetItem(IndexKey, entries.SetItem(entry.FileId, MemoryPackSerializer.Serialize(entry)));
        return Accept(entry);
    }

    private static bool EnoughCopies(FileReplicaProof[] copies, int required) => copies.Length >= required &&
        (required == 1 || (copies[0].NodeIdentity != copies[1].NodeIdentity &&
                          !string.Equals(copies[0].StorageIdentity, copies[1].StorageIdentity, StringComparison.OrdinalIgnoreCase)));

    private static bool SamePublication(FileAdmissionEntry entry, FileAdmissionMutation mutation) =>
        entry.PublicationHash.AsSpan().SequenceEqual(SHA256.HashData(MemoryPackSerializer.Serialize(mutation))) &&
        entry.MetadataKey == mutation.MetadataKey && entry.Length == mutation.Length &&
        string.Equals(entry.Sha256Hex, mutation.Sha256Hex, StringComparison.OrdinalIgnoreCase) &&
        (mutation.Action == FileAdmissionAction.CommitQueue
            ? entry.QueueKey == mutation.QueueKey && entry.ElementId == mutation.ElementId
            : entry.AliasKey == mutation.AliasKey && entry.AliasValue.AsSpan().SequenceEqual(mutation.AliasValue) &&
              entry.AliasExpireAtUtcTicks == mutation.AliasExpireAtUtcTicks);

    private static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && value[0] != '\0';
    private static bool ValidHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static FileAdmissionResult Accept(FileAdmissionEntry entry) => new() { Applied = true, Phase = entry.Phase };
    private static FileAdmissionResult Reject(string error, FileAdmissionEntry? entry) => new() { Error = error, Phase = entry?.Phase };
}
