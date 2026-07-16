using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SlimData;

public sealed record RateTier(int MinPerMinute, TimeSpan Delay);

public readonly record struct AdaptiveBatchQueueStatistics(string Kind, int Items, long Bytes);

public sealed class BatchQueueFullException(string kind)
    : Exception($"Adaptive batch queue '{kind}' is full.")
{
    public string Kind { get; } = kind;
}

public sealed class BatchItemTooLargeException(string kind, long itemBytes, long maximumBytes)
    : Exception($"Adaptive batch item for '{kind}' is {itemBytes} bytes; maximum is {maximumBytes} bytes.")
{
    public string Kind { get; } = kind;
    public long ItemBytes { get; } = itemBytes;
    public long MaximumBytes { get; } = maximumBytes;
}

public sealed class MultiRateAdaptiveBatcher : IAsyncDisposable
{
    private sealed record Kind
    {
        public int QueueLength;      // Interlocked
        public long QueueBytes;
        public int ArrivalsCount;
        public required string Name;
        public required Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyList<object>>> Handler;
        public required List<RateTier> Tiers;
        public required int MaxBatchSize;
        public int MaxQueueLength;
        public long MaxQueueBytes;
        
        public int MaxBatchBytes; // 0 = unlimited
        public TimeSpan CoalesceWindow;
        public required Func<object, int> SizeEstimatorBytes;

        public readonly object QueueGate = new();
        public readonly ConcurrentQueue<(object req, TaskCompletionSource<object> tcs, int size)> Queue = new();
        public readonly ConcurrentQueue<long> Arrivals = new(); // Stopwatch ticks in last minute
        public DateTime NextAllowedDequeueAtUtc = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, Kind> _kinds = new();
    private readonly SemaphoreSlim _signal = new(0);

    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;
    private volatile int _workerRunning; // 0 stopped, 1 running
    private Task? _loopTask;

    public TimeSpan IdleStop { get; }
    public TimeSpan MaxWaitPerTick { get; }

    public MultiRateAdaptiveBatcher(TimeSpan? idleStop = null, TimeSpan? maxWaitPerTick = null)
    {
        IdleStop = idleStop ?? TimeSpan.FromSeconds(15);
        MaxWaitPerTick = maxWaitPerTick ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Enregistre un "kind" (cas d'usage) avec son handler batch, ses paliers et sa taille de batch.
    /// </summary>
    public void RegisterKind<TReq, TRes>(
        string kind,
        Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> batchHandler,
        IEnumerable<RateTier>? tiers = null,
        int maxBatchSize = 512,
        int maxQueueLength = 0,
        long maxQueueBytes = 0L,
        int maxBatchBytes = 512 * 1024 * 1024,                    
        Func<TReq, int>? sizeEstimatorBytes = null,
        TimeSpan? coalesceWindow = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MultiRateAdaptiveBatcher));
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentNullException(nameof(kind));
        if (batchHandler is null) throw new ArgumentNullException(nameof(batchHandler));

        var tiersList = (tiers ?? new[]
        {
            new RateTier(32,  TimeSpan.FromMilliseconds(60)),
            new RateTier(64,  TimeSpan.FromMilliseconds(120)),
            new RateTier(128, TimeSpan.FromMilliseconds(240)),
        }).OrderBy(t => t.MinPerMinute).ToList();
        Func<TReq, int> typedEstimator = sizeEstimatorBytes ?? (_ => 0);
        var k = new Kind
        {
            Name = kind,
            MaxBatchSize = Math.Max(1, maxBatchSize),
            MaxQueueLength = Math.Max(0, maxQueueLength),
            MaxQueueBytes = Math.Max(0L, maxQueueBytes),
            Tiers = tiersList,
            MaxBatchBytes = Math.Max(0, maxBatchBytes),
            SizeEstimatorBytes = o => typedEstimator((TReq)o),
            CoalesceWindow = coalesceWindow ?? tiersList[0].Delay, 
            Handler = async (objs, ct) =>
            {
                // cast sûr tant qu'on respecte EnqueueAsync<TReq,TRes> pour ce kind
                var typedReqs = objs.Cast<TReq>().ToList();
                var typedRes = await batchHandler(typedReqs, ct).ConfigureAwait(false);
                return typedRes.Cast<object>().ToList();
            }
        };

        if (!_kinds.TryAdd(kind, k))
            throw new InvalidOperationException($"Kind '{kind}' already registered.");
    }

    /// <summary>
    /// Enfile une requête dans le kind indiqué et récupère le résultat typé.
    /// </summary>
    public async Task<TRes> EnqueueAsync<TReq, TRes>(string kind, TReq request, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MultiRateAdaptiveBatcher));

        if (!_kinds.TryGetValue(kind, out var k))
            throw new KeyNotFoundException($"Kind '{kind}' is not registered.");

        var size = Math.Max(0, k.SizeEstimatorBytes(request!));
        var maximumItemBytes = GetMaximumItemBytes(k);
        if (maximumItemBytes > 0L && size > maximumItemBytes)
            throw new BatchItemTooLargeException(kind, size, maximumItemBytes);

        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (k.QueueGate)
        {
            if ((k.MaxQueueLength > 0 && k.QueueLength >= k.MaxQueueLength) ||
                (k.MaxQueueBytes > 0L && k.QueueBytes + size > k.MaxQueueBytes))
            {
                throw new BatchQueueFullException(kind);
            }

            k.Queue.Enqueue((request!, tcs, size));
            k.QueueLength++;
            k.QueueBytes += size;
        }
        RecordArrival(k);

        StartWorkerIfNeeded();
        _signal.Release();

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var obj = await tcs.Task.ConfigureAwait(false);
        return (TRes)obj;
    }

    private static long GetMaximumItemBytes(Kind kind)
    {
        if (kind.MaxBatchBytes <= 0)
            return kind.MaxQueueBytes;
        if (kind.MaxQueueBytes <= 0L)
            return kind.MaxBatchBytes;
        return Math.Min(kind.MaxBatchBytes, kind.MaxQueueBytes);
    }

    public IReadOnlyList<AdaptiveBatchQueueStatistics> GetQueueStatistics()
        => _kinds.Values
            .Select(kind => new AdaptiveBatchQueueStatistics(
                kind.Name,
                Volatile.Read(ref kind.QueueLength),
                Volatile.Read(ref kind.QueueBytes)))
            .ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartWorkerIfNeeded()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
        {
            var ct = _disposeCts.Token;
            _loopTask = Task.Run(() => LoopAsync(ct), ct);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b)
        => TimeSpan.FromTicks(Math.Min(a.Ticks, b.Ticks));

    // --- Remplace entièrement la méthode LoopAsync par celle-ci ---
private async Task LoopAsync(CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            // 1) Idle-stop si tout est vide
            if (_kinds.Values.All(k => k.Queue.IsEmpty))
            {
                var idleDeadline = DateTime.UtcNow + IdleStop;
                while (DateTime.UtcNow < idleDeadline && _kinds.Values.All(k => k.Queue.IsEmpty))
                {
                    // ici on peut attendre sur le signal (on n’a rien en file)
                    await _signal.WaitAsync(Min(MaxWaitPerTick, TimeSpan.FromMilliseconds(250)), ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;
                }

                if (_kinds.Values.All(k => k.Queue.IsEmpty))
                {
                    Interlocked.Exchange(ref _workerRunning, 0);
                    if (_kinds.Values.Any(k => !k.Queue.IsEmpty) &&
                        Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
                    {
                        continue; // du travail est arrivé juste après l’arrêt
                    }
                    return;
                }
            }

            // 2) Sélection du kind prêt + gestion du throttling par tiers
            var now = DateTime.UtcNow;
            Kind? readyKind = null;
            DateTime? nextReadyAt = null; // plus proche échéance autorisée

            foreach (var k in _kinds.Values)
            {
                if (k.Queue.IsEmpty) continue;

                // prêt si pas de cooldown ou cooldown expiré
                if (k.NextAllowedDequeueAtUtc <= now)
                {
                    readyKind = k;
                    break;
                }

                // on garde la plus proche échéance
                if (nextReadyAt is null || k.NextAllowedDequeueAtUtc < nextReadyAt.Value)
                    nextReadyAt = k.NextAllowedDequeueAtUtc;
            }

            if (readyKind is null)
            {
                // aucun kind prêt -> attendre jusqu'au plus proche "ready", plafonné par MaxWaitPerTick
                var wait = MaxWaitPerTick;
                if (nextReadyAt is not null)
                {
                    var until = nextReadyAt.Value - now;
                    if (until > TimeSpan.Zero && until < wait) wait = until;
                }

                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }
            
            var ksel = readyKind;
            // 🔴 Fenêtre de coalescence : laisse la rafale se remplir (sans toucher au sémaphore)
            if (ksel.CoalesceWindow > TimeSpan.Zero)
            {
                // Si on n'a pas déjà de quoi remplir un gros batch, on attend un poil
                if (ksel.Queue.Count < ksel.MaxBatchSize)
                    await Task.Delay(ksel.CoalesceWindow, ct).ConfigureAwait(false);
            }
            // --- Drain avec limite mémoire et "toujours envoyer au moins 1" ---
            var batch = new List<(object req, TaskCompletionSource<object> tcs)>(ksel.MaxBatchSize);
            int usedBytes = 0;
            int cap = ksel.MaxBatchBytes; // 0 = pas de limite

            while (batch.Count < ksel.MaxBatchSize)
            {
                (object req, TaskCompletionSource<object> tcs, int size) next;
                lock (ksel.QueueGate)
                {
                    if (!ksel.Queue.TryPeek(out next))
                        break;
                }

                var sz = next.size;

                if (cap > 0)
                {
                    if (batch.Count == 0 && sz >= cap)
                    {
                        if (!TryDequeue(ksel, out var item))
                            continue;
                        if (item.tcs.Task.IsCompleted)
                            continue;
                        batch.Add((item.req, item.tcs));
                        usedBytes = sz;
                        break;
                    }

                    // Sinon, si ajouter cet élément dépasse le cap et qu'on a déjà qqch, on s'arrête
                    if (batch.Count > 0 && (usedBytes + sz) > cap)
                        break;
                }

                // Ok, on peut ajouter cet élément
                if (!TryDequeue(ksel, out var accepted))
                    continue;
                if (accepted.tcs.Task.IsCompleted)
                    continue;
                batch.Add((accepted.req, accepted.tcs));
                usedBytes += sz;
            }

            if (batch.Count == 0)
                continue;

            try
            {
                var reqs = batch.Select(b => b.req).ToList();
                var results = await ksel.Handler(reqs, ct).ConfigureAwait(false);
                if (results.Count != batch.Count)
                    throw new InvalidOperationException("Batch handler must return as many results as inputs.");

                for (int i = 0; i < results.Count; i++)
                    batch[i].tcs.TrySetResult(results[i]);
            }
            catch (Exception ex)
            {
                foreach (var it in batch)
                    it.tcs.TrySetException(ex);
            }

            // reset pour permettre un nouveau dequeue immédiat si la pression retombe
            var delayAfterBatch = ComputeDelayFromRatePerMinute(ksel);
            if (delayAfterBatch > TimeSpan.Zero)
                ksel.NextAllowedDequeueAtUtc = DateTime.UtcNow + delayAfterBatch;
            else
                ksel.NextAllowedDequeueAtUtc = DateTime.MinValue;
        }
    }
    catch (OperationCanceledException) { /* normal on dispose */ }
    finally
    {
        foreach (var k in _kinds.Values)
            while (TryDequeue(k, out var it))
                it.tcs.TrySetException(new TaskCanceledException("Batcher stopped"));
    }
}

    private static bool TryDequeue(
        Kind kind,
        out (object req, TaskCompletionSource<object> tcs, int size) item)
    {
        lock (kind.QueueGate)
        {
            if (!kind.Queue.TryDequeue(out item))
                return false;

            kind.QueueLength--;
            kind.QueueBytes -= item.size;
            return true;
        }
    }


    // ---- débit par minute & paliers (par kind) ----

    private static void RecordArrival(Kind k)
    {
        k.Arrivals.Enqueue(Stopwatch.GetTimestamp());
        Interlocked.Increment(ref k.ArrivalsCount);
        PruneArrivals(k);
    }

    private static int ComputeRatePerMinute(Kind k)
    {
        PruneArrivals(k);
        return Math.Max(0, Volatile.Read(ref k.ArrivalsCount));
    }

    private static TimeSpan ComputeDelayFromRatePerMinute(Kind k)
    {
        var rpm = ComputeRatePerMinute(k);
        TimeSpan delay = TimeSpan.Zero;
        foreach (var tier in k.Tiers)
        {
            if (rpm > tier.MinPerMinute) delay = tier.Delay;
            else break;
        }
        return delay;
    }

    private static void PruneArrivals(Kind k)
    {
        var now = Stopwatch.GetTimestamp();
        var windowTicks = Stopwatch.Frequency * 60L;
        while (k.Arrivals.TryPeek(out var ts))
        {
            if ((now - ts) > windowTicks)
            {
                if (k.Arrivals.TryDequeue(out _))
                    Interlocked.Decrement(ref k.ArrivalsCount);
            }
            else break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _disposeCts.Cancel();
        try { if (_loopTask is not null) await _loopTask.ConfigureAwait(false); } catch { /* ignore */ }

        _signal.Dispose();
        _disposeCts.Dispose();
    }
}
