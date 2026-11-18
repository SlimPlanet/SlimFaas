using System.Globalization;
using DotNext.Net.Cluster.Messaging;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Options;

namespace SlimFaas;

public sealed record PreparedFileAdmission(string FileId, string Token, long DeadlineUtcTicks,
    DataSetMetadata Metadata, FileReplicaProof[] Copies);

public interface IFileAdmissionService
{
    bool Enabled { get; }
    Task<PreparedFileAdmission> PrepareAsync(string fileId, Stream content, string contentType, string fileName,
        long lengthReservation, IDictionary<string, string>? tags, CancellationToken ct);
    Task CommitQueueAsync(PreparedFileAdmission admission, string function, string elementId, byte[] value,
        RetryInformation retry, CancellationToken ct);
    Task CommitPublicAsync(PreparedFileAdmission admission, string publicId, long? ttlMilliseconds, CancellationToken ct);
}

public sealed class FileAdmissionService(IDatabaseService database, IClusterFileSync sync, IFileRepository repository,
    IMessageBus bus, IOptions<ClusterFileOptions> options) : IFileAdmissionService
{
    internal static readonly object ContextKey = new();
    public bool Enabled => options.Value.RequiredDurableCopies > 0;

    public async Task<PreparedFileAdmission> PrepareAsync(string fileId, Stream content, string contentType, string fileName,
        long lengthReservation, IDictionary<string, string>? tags, CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Durable file admission is disabled.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.AdmissionTimeoutSeconds));
        string token = Guid.NewGuid().ToString("N");
        long deadline = DateTime.UtcNow.AddSeconds(options.Value.AdmissionTimeoutSeconds).Ticks;
        try
        {
            await MutateAsync(new()
            {
                Action = FileAdmissionAction.Begin, FileId = fileId, Token = token,
                DeadlineUtcTicks = deadline, RequiredCopies = options.Value.RequiredDurableCopies
            }, timeout.Token).ConfigureAwait(false);
            var markedTags = tags is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new(tags, StringComparer.Ordinal);
            markedTags[FileAdmissionTags.Token] = token;
            markedTags[FileAdmissionTags.Deadline] = deadline.ToString(CultureInfo.InvariantCulture);
            markedTags[DataFileKeys.StorageIdTag] = fileId;
            FilePutResult put = await sync.BroadcastFilePutAsync(fileId, content, contentType, lengthReservation,
                overwrite: false, ttl: null, timeout.Token, markedTags).ConfigureAwait(false);
            if (repository is not IDurableFileRepository durable)
                throw new IOException("The file repository cannot confirm persistence.");
            string localNode = bus.Members.Single(member => !member.IsRemote).Id.ToString();
            FileReplicaProof[] copies;
            if (options.Value.RequiredDurableCopies == 2)
            {
                if (sync is not IDurableClusterFileSync durableSync)
                    throw new IOException("The file synchronization service cannot confirm remote persistence.");
                // BroadcastFilePut has released its transfer budget before this
                // call: a receiver can pull without two senders holding budgets.
                FileCopyReceipt remote = await durableSync.ConfirmRemoteCopyAsync(fileId, put.Sha256Hex, put.Length, timeout.Token)
                    .ConfigureAwait(false);
                if (!remote.Persisted || remote.Error is not null || remote.Id != fileId || remote.Length != put.Length ||
                    remote.AdmissionToken != token ||
                    !remote.Sha256Hex.Equals(put.Sha256Hex, StringComparison.OrdinalIgnoreCase) ||
                    remote.NodeIdentity == localNode ||
                    string.Equals(remote.StorageIdentity, durable.StorageIdentity, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The remote persisted-copy receipt does not match the admission.");
                copies = [Proof(fileId, put, localNode, durable.StorageIdentity), Proof(fileId, put, remote.NodeIdentity, remote.StorageIdentity)];
            }
            else
            {
                FilePersistenceReceipt local = await durable.PersistVersionAsync(fileId, put.Sha256Hex, put.Length, timeout.Token)
                    .ConfigureAwait(false);
                if (local.Id != fileId || local.Length != put.Length ||
                    !local.Sha256Hex.Equals(put.Sha256Hex, StringComparison.OrdinalIgnoreCase) || local.StorageIdentity != durable.StorageIdentity)
                    throw new IOException("The local persisted-copy receipt does not match the admission.");
                copies = [Proof(fileId, put, localNode, local.StorageIdentity)];
            }
            var result = new PreparedFileAdmission(fileId, token, deadline,
                new(put.Sha256Hex, put.Length, put.ContentType, fileName, markedTags), copies);
            CheckMembers(result);
            return result;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SlimDataUnavailableException("Durable file admission timed out before publication.", ex);
        }
        catch (IOException ex)
        {
            throw new SlimDataUnavailableException("Durable file copies could not be confirmed before publication.", ex);
        }
    }

    public async Task CommitQueueAsync(PreparedFileAdmission admission, string function, string elementId, byte[] value,
        RetryInformation retry, CancellationToken ct)
    {
        CheckMembers(admission);
        var mutation = Publication(admission, FileAdmissionAction.CommitQueue);
        mutation.QueueKey = "Queue:" + function;
        mutation.ElementId = elementId;
        mutation.Value = value;
        mutation.RetryTimeoutSeconds = retry.RetryTimeoutSeconds;
        mutation.Retries = retry.Retries.ToArray();
        mutation.HttpStatusRetries = retry.HttpStatusRetries.ToArray();
        await MutateAsync(mutation, ct).ConfigureAwait(false);
    }

    public async Task CommitPublicAsync(PreparedFileAdmission admission, string publicId, long? ttlMilliseconds, CancellationToken ct)
    {
        CheckMembers(admission);
        var mutation = Publication(admission, FileAdmissionAction.CommitPublic);
        mutation.AliasKey = DataFileKeys.MetaKey(publicId);
        mutation.AliasValue = mutation.Metadata;
        if (ttlMilliseconds.HasValue)
        {
            if (ttlMilliseconds.Value <= 0) throw new ArgumentOutOfRangeException(nameof(ttlMilliseconds));
            long now = DateTime.UtcNow.Ticks;
            long remainingMilliseconds = (DateTime.MaxValue.Ticks - now) / TimeSpan.TicksPerMillisecond;
            mutation.AliasExpireAtUtcTicks = ttlMilliseconds.Value >= remainingMilliseconds
                ? DateTime.MaxValue.Ticks : now + ttlMilliseconds.Value * TimeSpan.TicksPerMillisecond;
        }
        await MutateAsync(mutation, ct).ConfigureAwait(false);
    }

    private async Task MutateAsync(FileAdmissionMutation mutation, CancellationToken ct)
    {
        FileAdmissionResult result = await database.ExecuteFileAdmissionAsync(mutation).WaitAsync(ct).ConfigureAwait(false);
        if (!result.Applied) throw new SlimDataUnavailableException(result.Error ?? "File admission was not committed.");
    }

    private void CheckMembers(PreparedFileAdmission admission)
    {
        foreach (var copy in admission.Copies)
        {
            if (!Guid.TryParseExact(copy.StorageIdentity, "N", out var volume) || volume == Guid.Empty ||
                !bus.Members.Any(member => member.Id.ToString() == copy.NodeIdentity))
                throw new SlimDataUnavailableException("The persisted file copy is no longer a known cluster member.");
        }
    }

    private static FileAdmissionMutation Publication(PreparedFileAdmission admission, FileAdmissionAction action) => new()
    {
        Action = action, FileId = admission.FileId, Token = admission.Token, Sha256Hex = admission.Metadata.Sha256Hex,
        Length = admission.Metadata.Length, MetadataKey = DataFileKeys.MetaKey(admission.FileId),
        Metadata = MemoryPackSerializer.Serialize(admission.Metadata), Copies = admission.Copies
    };

    private static FileReplicaProof Proof(string id, FilePutResult put, string node, string storage) => new()
    {
        FileId = id, Sha256Hex = put.Sha256Hex, Length = put.Length, NodeIdentity = node, StorageIdentity = storage
    };
}
