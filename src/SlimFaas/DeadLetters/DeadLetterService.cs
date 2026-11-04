using System.Collections.Immutable;
using DotNext;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;

namespace SlimFaas.DeadLetters;

public interface IDeadLetterService
{
    bool CanAccess(HttpContext context);
    bool FrontEnabled { get; }
    Task<DeadLetterSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task ConfigureAsync(DeploymentInformation function);
    Task<DeadLetterMutationResult> MutateAsync(DeadLetterMutation mutation);
    Task<Stream?> OpenBodyAsync(DeadLetterEntry entry, CancellationToken cancellationToken);
}

public sealed record DeadLetterSnapshot(SlimDataPayload State, IReadOnlyList<DeadLetterSummary> Summaries,
    DateTimeOffset CapturedAt, long Revision, long PersistenceFailures = 0);

/// <summary>Retained failures and capture configuration for one function.</summary>
public sealed record DeadLetterSummary(string Function, bool CaptureEnabled, bool FunctionExists, long Count,
    DateTimeOffset? OldestFailedAt, DateTimeOffset? NewestFailedAt, int MaxAgeSeconds, int MaxLength,
    DateTimeOffset CapturedAt, long Revision);

public sealed class DeadLetterService(
    IDatabaseService database,
    ISupplier<SlimDataPayload> state,
    IReplicasService replicas,
    IWebSocketFunctionRepository virtualFunctions,
    IFunctionAccessPolicy access,
    IOptions<SlimFaasOptions> options,
    IClusterFileSync files,
    IMasterService master,
    NetworkActivityTracker activity,
    ILogger<DeadLetterService> logger) : BackgroundService, IDeadLetterService
{
    private long _persistenceFailures;
    private readonly object _activityGate = new();
    private readonly Dictionary<string, long> _publishedRevisions = new(StringComparer.Ordinal);

    public bool FrontEnabled => options.Value.EnableFront && options.Value.DeadLetterQueue.EnableFront;
    public bool IsManaged(string function) => state.Invoke().KeyValues.ContainsKey(DeadLetterKeys.Policy(function));
    public bool CanAccess(HttpContext context) =>
        options.Value.DeadLetterQueue.DefaultVisibility == FunctionVisibility.Public || access.IsInternalRequest(context);

    public async Task ConfigureAsync(DeploymentInformation function)
    {
        var config = function.Configuration.DefaultAsync.DeadLetterQueue;
        var current = state.Invoke().KeyValues.GetValueOrDefault(DeadLetterKeys.Policy(function.Deployment));
        if (current.IsEmpty && !config.Enabled)
            return;
        var previous = current.IsEmpty ? null : MemoryPackSerializer.Deserialize<DeadLetterPolicy>(current.Span);
        if (previous is not null && previous.Enabled == config.Enabled &&
            previous.MaxAgeSeconds == config.MaxAgeSeconds && previous.MaxLength == config.MaxLength)
            return;
        await MutateAsync(new()
        {
            Action = DeadLetterAction.Policy, Function = function.Deployment,
            Policy = new() { Enabled = config.Enabled, MaxAgeSeconds = config.MaxAgeSeconds, MaxLength = config.MaxLength }
        }).ConfigureAwait(false);
    }

    public async Task<DeadLetterMutationResult> MutateAsync(DeadLetterMutation mutation)
    {
        mutation.NowTicks = DateTime.UtcNow.Ticks;
        DeadLetterMutationResult result;
        try
        {
            result = await database.ExecuteDeadLetterAsync(mutation).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _persistenceFailures);
            logger.LogDeadLetterPersistenceFailure(exception, mutation.Function, mutation.Action);
            throw;
        }
        PublishTransitions(state.Invoke(), mutation.Function);
        return result;
    }

    private void PublishTransitions(SlimDataPayload payload, string function)
    {
        if (!payload.KeyValues.TryGetValue(DeadLetterKeys.Stats(function), out var bytes))
            return;
        var stats = MemoryPackSerializer.Deserialize<DeadLetterStatistics>(bytes.Span)!;
        lock (_activityGate)
        {
            long previous = _publishedRevisions.GetValueOrDefault(function);
            foreach (var item in stats.RecentTransitions.Where(item => item.Revision > previous))
            {
                logger.LogDeadLetterTransition(item.Type, function, item.DeadLetterId, item.OriginalElementId, item.ElementId);
                if (FrontEnabled)
                    activity.Record(item.Type, item.Type == "dead_lettered" ? function : "dlq:" + function,
                        item.Type == "dead_letter_requeued" ? function : "dlq:" + function,
                        function, correlationId: item.OriginalElementId, deadLetterId: item.DeadLetterId,
                        elementId: item.ElementId, originalElementId: item.OriginalElementId);
            }
            _publishedRevisions[function] = Math.Max(previous, stats.Revision);
        }
    }

    public async Task<DeadLetterSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await database.GetAsync("\0dlq:read-barrier").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Snapshot(state.Invoke());
    }

    public DeadLetterSnapshot Snapshot(SlimDataPayload payload)
    {
        var now = DateTimeOffset.UtcNow;
        var functions = replicas.Deployments.Functions.Concat(virtualFunctions.GetVirtualDeployments())
            .DistinctBy(function => function.Deployment, StringComparer.Ordinal)
            .ToDictionary(function => function.Deployment, StringComparer.Ordinal);
        var names = functions.Values.Where(function => function.Configuration.DefaultAsync.DeadLetterQueue.Enabled)
            .Select(function => function.Deployment)
            .Concat(payload.KeyValues.Keys.Where(key => key.StartsWith(DeadLetterKeys.PolicyPrefix, StringComparison.Ordinal))
                .Select(key => key[DeadLetterKeys.PolicyPrefix.Length..]))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var summaries = new List<DeadLetterSummary>();
        long revision = 0;
        foreach (string name in names)
        {
            var entries = Entries(payload, name).Where(entry => entry.ExpiresAtTicks > now.UtcTicks).ToList();
            functions.TryGetValue(name, out var function);
            bool enabled = function?.Configuration.DefaultAsync.DeadLetterQueue.Enabled == true;
            var policy = payload.KeyValues.TryGetValue(DeadLetterKeys.Policy(name), out var policyBytes)
                ? MemoryPackSerializer.Deserialize<DeadLetterPolicy>(policyBytes.Span)! : new();
            var stats = payload.KeyValues.TryGetValue(DeadLetterKeys.Stats(name), out var statsBytes)
                ? MemoryPackSerializer.Deserialize<DeadLetterStatistics>(statsBytes.Span)! : new();
            revision += stats.Revision;
            if (!enabled && entries.Count == 0)
                continue;
            summaries.Add(new(name, enabled, function is not null, entries.Count,
                entries.Count == 0 ? null : Time(entries.Min(entry => entry.FailedAtTicks)),
                entries.Count == 0 ? null : Time(entries.Max(entry => entry.FailedAtTicks)),
                policy.MaxAgeSeconds, policy.MaxLength, now, stats.Revision));
        }
        return new(payload, summaries, now, revision, Interlocked.Read(ref _persistenceFailures));
    }

    public static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
    public static IEnumerable<DeadLetterEntry> Entries(SlimDataPayload payload, string function) =>
        payload.Hashsets.TryGetValue(DeadLetterKeys.Messages(function), out var entries)
            ? entries.Values.Select(bytes => MemoryPackSerializer.Deserialize<DeadLetterEntry>(bytes.Span)!)
            : [];

    public static DeadLetterEntry WithPayload(SlimDataPayload payload, DeadLetterEntry entry)
    {
        if (entry.Value.Length != 0)
            return entry;
        if (!payload.Hashsets.TryGetValue(DeadLetterKeys.Payloads(entry.Function), out var values) ||
            !values.TryGetValue(entry.Id, out var bytes))
            throw new InvalidDataException("The retained request is unavailable.");
        entry.Value = bytes.ToArray();
        return entry;
    }

    public async Task<Stream?> OpenBodyAsync(DeadLetterEntry entry, CancellationToken cancellationToken)
    {
        var request = MemoryPackSerializer.Deserialize<CustomRequest>(entry.Value);
        if (string.IsNullOrEmpty(request.OffloadedFileId))
            return new MemoryStream(request.Body ?? [], writable: false);
        byte[]? bytes = await database.GetAsync(DataFileKeys.MetaKey(request.OffloadedFileId)).ConfigureAwait(false);
        if (bytes is null)
            return null;
        var metadata = MemoryPackSerializer.Deserialize<DataSetMetadata>(bytes);
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Sha256Hex))
            return null;
        var result = await files.PullFileIfMissingAsync(request.OffloadedFileId, metadata.Sha256Hex, null, cancellationToken)
            .ConfigureAwait(false);
        return result.Stream ?? throw new SlimDataUnavailableException("The retained payload cannot currently be retrieved from the cluster.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!master.IsMaster)
                continue;
            try
            {
                foreach (var function in replicas.Deployments.Functions.Concat(virtualFunctions.GetVirtualDeployments()))
                    await ConfigureAsync(function).ConfigureAwait(false);
                var payload = state.Invoke();
                var names = payload.KeyValues.Keys.Where(key => key.StartsWith(DeadLetterKeys.PolicyPrefix, StringComparison.Ordinal))
                    .Select(key => key[DeadLetterKeys.PolicyPrefix.Length..]).ToArray();
                foreach (string function in names)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    await MutateAsync(new() { Action = DeadLetterAction.Sweep, Function = function }).ConfigureAwait(false);
                }
                var snapshot = Snapshot(state.Invoke());
                foreach (string function in names)
                {
                    PublishTransitions(snapshot.State, function);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDeadLetterMaintenanceFailure(exception);
            }
        }
    }
}
