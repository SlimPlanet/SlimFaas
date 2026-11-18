using System.Collections.Immutable;
using MemoryPack;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class FileAdmissionTests
{
    private static readonly long Now = new DateTime(2026, 11, 15, 18, 0, 0, DateTimeKind.Utc).Ticks;
    private const string FileId = "physical-unique-file";
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static SlimDataState Create() => new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
    private static FileAdmissionMutation Begin(int copies = 2) => new()
    {
        Action = FileAdmissionAction.Begin, FileId = FileId, Token = Token,
        DeadlineUtcTicks = Now + 120 * TimeSpan.TicksPerSecond, RequiredCopies = copies
    };
    private static FileReplicaProof Proof(string node, string volume) => new()
    {
        FileId = FileId, Sha256Hex = Hash, Length = 42, NodeIdentity = node, StorageIdentity = volume
    };
    private static FileAdmissionMutation Commit(bool publicFile = false) => new()
    {
        Action = publicFile ? FileAdmissionAction.CommitPublic : FileAdmissionAction.CommitQueue,
        FileId = FileId, Token = Token, Sha256Hex = Hash, Length = 42,
        MetadataKey = "data:file:physical-unique-file:meta", Metadata = [1, 2, 3],
        QueueKey = "Queue:billing", ElementId = "delivery", Value = [4, 5, 6],
        RetryTimeoutSeconds = 30, Retries = [1, 2], HttpStatusRetries = [500, 503],
        AliasKey = "data:file:public:meta", AliasValue = [7, 8, 9],
        Copies = [Proof("node-a", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Proof("node-b", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    };
    private static FileAdmissionMutation Maintenance(FileAdmissionAction action) => new()
    {
        Action = action, FileId = FileId, Token = Token
    };
    private static FileAdmissionEntry Entry(SlimDataState state) => FileAdmissionStore.Find(state.CapturePayload(), FileId)!;
    private static FileAdmissionResult Apply(SlimDataState state, FileAdmissionMutation mutation, long? now = null) =>
        FileAdmissionStore.Apply(mutation, state, now ?? Now);

    [Fact]
    public void Pending_pin_is_durable_before_any_metadata_or_queue_publication()
    {
        var state = Create();
        Assert.True(Apply(state, Begin()).Applied);
        Assert.True(FileAdmissionStore.IsReferenced(state.CapturePayload(), Entry(state), Now));
        Assert.False(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
        Assert.Empty(state.Queues);
        Assert.False(state.KeyValues.ContainsKey(Commit().MetadataKey));
        var restored = new SlimDataState(state.Hashsets, state.KeyValues, state.Queues);
        Assert.True(FileAdmissionStore.IsReferenced(restored.CapturePayload(), Entry(restored), Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_publishes_metadata_and_owner_in_one_snapshot_and_is_idempotent(bool publicFile)
    {
        var state = Create();
        Apply(state, Begin());
        var pendingSnapshot = state.Capture();
        var commit = Commit(publicFile);
        Assert.True(Apply(state, commit).Applied);
        Assert.Equal(FileAdmissionPhase.Active, Entry(state).Phase);
        Assert.Equal(commit.Metadata, state.KeyValues[commit.MetadataKey].ToArray());
        Assert.False(pendingSnapshot.KeyValues.ContainsKey(commit.MetadataKey));
        Assert.Empty(pendingSnapshot.Queues);
        Assert.True(FileAdmissionStore.IsReferenced(state.CapturePayload(), Entry(state), Now));
        Assert.True(Apply(state, commit).Applied);
        if (publicFile) Assert.Equal(commit.AliasValue, state.KeyValues[commit.AliasKey].ToArray());
        else Assert.Equal(commit.Value, Assert.Single(state.Queues[commit.QueueKey]).Value.ToArray());
        Assert.False(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("volume")]
    [InlineData("volume-case")]
    [InlineData("missing")]
    public void Missing_or_shared_copy_proofs_do_not_publish(string defect)
    {
        var state = Create(); Apply(state, Begin());
        var commit = Commit();
        switch (defect)
        {
            case "node": commit.Copies[1].NodeIdentity = commit.Copies[0].NodeIdentity; break;
            case "volume": commit.Copies[1].StorageIdentity = commit.Copies[0].StorageIdentity; break;
            case "volume-case": commit.Copies[1].StorageIdentity = commit.Copies[0].StorageIdentity.ToUpperInvariant(); break;
            case "missing": commit.Copies = [commit.Copies[0]]; break;
        }
        Assert.False(Apply(state, commit).Applied);
        Assert.Empty(state.Queues);
        Assert.False(state.KeyValues.ContainsKey(commit.MetadataKey));
        Assert.Equal(FileAdmissionPhase.Pending, Entry(state).Phase);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("id")]
    public void Copy_for_another_revision_is_rejected_before_state_mutation(string defect)
    {
        var state = Create(); Apply(state, Begin());
        var snapshot = state.Capture();
        var commit = Commit();
        switch (defect)
        {
            case "hash": commit.Copies[1].Sha256Hex = new string('f', 64); break;
            case "length": commit.Copies[1].Length++; break;
            case "id": commit.Copies[1].FileId = "another"; break;
        }
        Assert.Throws<InvalidDataException>(() => Apply(state, commit));
        Assert.Same(snapshot, state.Capture());
    }

    [Fact]
    public void Local_only_policy_requires_one_copy_but_cannot_weaken_an_existing_two_copy_pin()
    {
        var local = Create(); Apply(local, Begin(1));
        var commit = Commit(); commit.Copies = [commit.Copies[0]];
        Assert.True(Apply(local, commit).Applied);
        var replicated = Create(); Apply(replicated, Begin(2));
        Assert.False(Apply(replicated, Begin(1)).Applied);
        commit.RequiredCopies = 1;
        Assert.False(Apply(replicated, commit).Applied);
    }

    [Fact]
    public void Conflicting_token_or_packet_cannot_replace_a_publication()
    {
        var state = Create(); Apply(state, Begin());
        var wrong = Commit(); wrong.Token = Guid.NewGuid().ToString("N");
        Assert.False(Apply(state, wrong).Applied);
        var commit = Commit(); Assert.True(Apply(state, commit).Applied);
        var changed = Commit(); changed.Value = [99];
        Assert.False(Apply(state, changed).Applied);
        Assert.Equal(commit.Value, Assert.Single(state.Queues[commit.QueueKey]).Value.ToArray());
    }

    [Fact]
    public void Expiry_retirement_and_forgetting_fence_delayed_commands_even_after_clock_rollback()
    {
        var state = Create(); var begin = Begin(); Apply(state, begin);
        long expired = begin.DeadlineUtcTicks;
        Assert.False(Apply(state, Commit(), expired).Applied);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Retire), expired).Applied);
        Assert.False(Apply(state, Commit(), Now).Applied);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Forget), Now).Applied);
        Assert.Null(FileAdmissionStore.Find(state.CapturePayload(), FileId));
        Assert.False(Apply(state, begin, Now).Applied);
        Assert.False(Apply(state, Commit(), Now).Applied);
        Assert.Empty(state.Queues);
        Assert.False(state.KeyValues.ContainsKey(Commit().MetadataKey));
    }

    [Fact]
    public void Live_public_alias_is_replaced_by_a_new_physical_admission_without_deleting_the_new_copy()
    {
        var state = Create(); Apply(state, Begin()); Apply(state, Commit(true));
        var nextBegin = Begin(); nextBegin.FileId = "next-unique-file"; nextBegin.Token = Guid.NewGuid().ToString("N");
        Apply(state, nextBegin);
        var next = Commit(true); next.FileId = nextBegin.FileId; next.Token = nextBegin.Token;
        next.MetadataKey = "data:file:next-unique-file:meta"; next.AliasValue = [88];
        foreach (var proof in next.Copies) proof.FileId = next.FileId;
        Assert.True(Apply(state, next).Applied);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
        Assert.False(state.KeyValues.ContainsKey(Commit(true).MetadataKey));
        Assert.Equal(next.AliasValue, state.KeyValues[next.AliasKey].ToArray());
        Assert.True(state.KeyValues.ContainsKey(next.MetadataKey));
        Assert.False(Apply(state, Commit(true)).Applied);
    }

    [Fact]
    public void Public_expiration_releases_the_reference_and_retirement_removes_its_physical_descriptor()
    {
        var state = Create(); Apply(state, Begin());
        var commit = Commit(true); commit.AliasExpireAtUtcTicks = Now + TimeSpan.TicksPerSecond;
        Apply(state, commit);
        Assert.Equal(commit.AliasExpireAtUtcTicks, BitConverter.ToInt64(state.KeyValues[SlimDataInterpreter.TtlKey(commit.AliasKey)].Span));
        Assert.False(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Retire), commit.AliasExpireAtUtcTicks).Applied);
        Assert.False(state.KeyValues.ContainsKey(commit.MetadataKey));
        Assert.False(state.KeyValues.ContainsKey(commit.AliasKey));
        Assert.False(state.KeyValues.ContainsKey(SlimDataInterpreter.TtlKey(commit.AliasKey)));
    }

    [Fact]
    public async Task Dead_letter_and_replay_lineage_protect_the_original_body_until_the_last_owner_completes()
    {
        var state = Create(); Apply(state, Begin()); Apply(state, Commit());
        DeadLetterStateMachine.Apply(new()
        {
            Action = DeadLetterAction.Policy, Function = "billing", NowTicks = Now,
            Policy = new() { Enabled = true }
        }, state);
        await SlimDataInterpreter.DoListRightPopAsync(new()
        {
            Key = "Queue:billing", Count = 1, NowTicks = Now, IdTransaction = "attempt"
        }, state);
        DeadLetterStateMachine.Apply(new()
        {
            Action = DeadLetterAction.Complete, Function = "billing", ElementId = "delivery", HttpStatus = 400, NowTicks = Now
        }, state);
        Assert.Empty(state.Queues["Queue:billing"]);
        Assert.False(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
        string messageId = Assert.Single(state.Hashsets[DeadLetterKeys.Messages("billing")]).Key;
        DeadLetterStateMachine.Apply(new()
        {
            Action = DeadLetterAction.Requeue, Function = "billing", MessageId = messageId, NewElementId = "replayed",
            RetryTimeoutSeconds = 30, Retries = [], HttpStatusRetries = [500], NowTicks = Now
        }, state);
        Assert.False(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
        await SlimDataInterpreter.DoListRightPopAsync(new()
        {
            Key = "Queue:billing", Count = 1, NowTicks = Now, IdTransaction = "replay-attempt"
        }, state);
        DeadLetterStateMachine.Apply(new()
        {
            Action = DeadLetterAction.Complete, Function = "billing", ElementId = "replayed", HttpStatus = 200, NowTicks = Now
        }, state);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
    }

    [Fact]
    public void Queue_identifier_collision_leaves_pending_pin_and_metadata_unpublished()
    {
        var state = Create(); Apply(state, Begin());
        state.Queues = state.Queues.Add("Queue:billing", [new(new byte[] { 99 }, "delivery", Now, 30, [], [], [])]);
        Assert.False(Apply(state, Commit()).Applied);
        Assert.Equal(new byte[] { 99 }, Assert.Single(state.Queues["Queue:billing"]).Value.ToArray());
        Assert.False(state.KeyValues.ContainsKey(Commit().MetadataKey));
        Assert.Equal(FileAdmissionPhase.Pending, Entry(state).Phase);
    }

    [Fact]
    public async Task Composite_command_round_trip_preserves_atomic_admission_and_returns_the_typed_result()
    {
        var state = Create();
        var request = new SlimDataCommandBatchRequest
        {
            ProducerId = "producer", GenerationId = "generation", Sequence = 1, RequestId = "request",
            Operations = [Operation(Begin()), Operation(Commit())]
        };
        var envelope = new SlimDataRaftBatchEnvelope { Requests = [request] };
        SlimDataCommandBatchValidator.ValidateEnvelope(envelope);
        var context = new SlimDataCommandBatchContext();
        await SlimDataInterpreter.DoExecuteBatchAsync(new() { Payload = SlimDataRaftBatchEnvelopeCodec.Serialize(envelope) }, state, context);
        Assert.All(Assert.Single(context.Responses).Results, result =>
            Assert.True(MemoryPackSerializer.Deserialize<FileAdmissionResult>(result.KeyValueResult!.Value!)!.Applied));
        Assert.Single(state.Queues["Queue:billing"]);
        Assert.True(SlimDataCommandBatchCoordinator.ContainsQueueMutation(envelope));
        request.Operations = [Operation(Begin())];
        Assert.False(SlimDataCommandBatchCoordinator.ContainsQueueMutation(envelope));
    }

    [Fact]
    public void Leader_stamps_admission_times_without_changing_original_deadline_or_unrelated_operations()
    {
        var begin = Begin(); var admission = Operation(begin);
        var other = new SlimDataBatchOperation { Kind = SlimDataBatchOperationKind.KeyValue, NowTicks = 1 };
        var envelope = new SlimDataRaftBatchEnvelope
        {
            Requests = [new() { Operations = [admission, other] }]
        };
        Endpoints.StampFileAdmissionTimes(envelope, Now + TimeSpan.TicksPerSecond);
        Assert.Equal(Now + TimeSpan.TicksPerSecond, admission.NowTicks);
        Assert.Equal(1, other.NowTicks);
        Assert.Equal(begin.DeadlineUtcTicks, MemoryPackSerializer.Deserialize<FileAdmissionMutation>(admission.Value)!.DeadlineUtcTicks);
    }

    [Theory]
    [InlineData(FileAdmissionStore.IndexKey)]
    [InlineData(FileAdmissionStore.ClockKey)]
    public void Ordinary_mutations_cannot_overwrite_private_admission_state(string key)
    {
        var operation = Operation(Begin()); operation.Kind = SlimDataBatchOperationKind.DeleteKeyValue; operation.Key = key;
        var request = new SlimDataCommandBatchRequest
        {
            ProducerId = "producer", GenerationId = "generation", Sequence = 1, RequestId = "request", Operations = [operation]
        };
        Assert.Throws<InvalidDataException>(() => SlimDataCommandBatchValidator.ValidateRequest(request));
    }

    [Fact]
    public void Empty_volume_identity_is_not_a_persisted_copy_proof()
    {
        var state = Create(); Apply(state, Begin()); var commit = Commit();
        commit.Copies[0].StorageIdentity = Guid.Empty.ToString("N");
        Assert.Throws<InvalidDataException>(() => Apply(state, commit));
        Assert.Equal(FileAdmissionPhase.Pending, Entry(state).Phase);
    }

    [Fact]
    public void Retired_record_is_kept_until_its_original_begin_deadline_and_cannot_be_reopened()
    {
        var state = Create(); var begin = Begin(); Apply(state, begin); Apply(state, Commit(true));
        state.KeyValues = state.KeyValues.Remove(Commit(true).AliasKey);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Retire)).Applied);
        Assert.False(Apply(state, Maintenance(FileAdmissionAction.Forget)).Applied);
        Assert.False(Apply(state, begin).Applied);
        Assert.False(Apply(state, Commit(true)).Applied);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Forget), begin.DeadlineUtcTicks).Applied);
    }

    [Fact]
    public void A_late_copy_after_forgetting_can_be_deleted_without_reopening_its_admission()
    {
        var state = Create(); var begin = Begin(); Apply(state, begin);
        var retire = Maintenance(FileAdmissionAction.Retire); retire.DeadlineUtcTicks = begin.DeadlineUtcTicks;
        Assert.True(Apply(state, retire, begin.DeadlineUtcTicks).Applied);
        Assert.True(Apply(state, Maintenance(FileAdmissionAction.Forget), begin.DeadlineUtcTicks).Applied);
        Assert.True(Apply(state, retire, Now).Applied);
        Assert.Null(FileAdmissionStore.Find(state.CapturePayload(), FileId));
        Assert.False(Apply(state, begin, Now).Applied);
        Assert.False(Apply(state, Commit(), Now).Applied);
        retire.DeadlineUtcTicks++;
        Assert.False(Apply(state, retire, Now).Applied);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void Invalid_persisted_clock_fails_closed_without_expiring_live_references(long clock)
    {
        var state = Create(); Apply(state, Begin()); Apply(state, Commit(true));
        state.KeyValues = state.KeyValues.SetItem(FileAdmissionStore.ClockKey, BitConverter.GetBytes(clock));
        var snapshot = state.Capture();
        Assert.Throws<InvalidDataException>(() => Apply(state, Maintenance(FileAdmissionAction.Retire)));
        Assert.Same(snapshot, state.Capture());
    }

    private static SlimDataBatchOperation Operation(FileAdmissionMutation mutation) => new()
    {
        Kind = SlimDataBatchOperationKind.FileAdmission, Key = FileAdmissionStore.IndexKey, RequestId = Guid.NewGuid().ToString("N"),
        NowTicks = Now, Value = MemoryPackSerializer.Serialize(mutation)
    };
}
