using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SlimData;

public sealed record RateTier(int MinPerMinute, TimeSpan Delay);

public sealed class MultiRateAdaptiveBatcher : IAsyncDisposable
{
    private sealed record Kind
    {
        public required string Name;
        public required Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyList<object>>> Handler;
        public required List<RateTier> Tiers;
        public required int MaxBatchSize;
        public int MaxQueueLength;
        
        public int MaxBatchBytes; // 0 = unlimited
        public required Func<object, int> SizeEstimatorBytes;

        public readonly ConcurrentQueue<(object req, TaskCompletionSource<object> tcs)> Queue = new();
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
        int maxBatchBytes = 512 * 1024 * 1024,                    
        Func<TReq, int>? sizeEstimatorBytes = null )
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
            Tiers = tiersList,
            MaxBatchBytes = Math.Max(0, maxBatchBytes),
            SizeEstimatorBytes = o => typedEstimator((TReq)o),
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

        if (k.MaxQueueLength > 0 && k.Queue.Count >= k.MaxQueueLength)
            throw new InvalidOperationException($"Queue '{kind}' is full");

        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        k.Queue.Enqueue((request!, tcs));
        RecordArrival(k);

        StartWorkerIfNeeded();
        _signal.Release();

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var obj = await tcs.Task.ConfigureAwait(false);
        return (TRes)obj;
    }

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
            // --- Drain avec limite mémoire et "toujours envoyer au moins 1" ---
            var batch = new List<(object req, TaskCompletionSource<object> tcs)>(ksel.MaxBatchSize);
            int usedBytes = 0;
            int cap = ksel.MaxBatchBytes; // 0 = pas de limite

            while (batch.Count < ksel.MaxBatchSize && ksel.Queue.TryPeek(out var next))
            {
                int sz = (cap > 0) ? Math.Max(0, ksel.SizeEstimatorBytes(next.req)) : 0;

                if (cap > 0)
                {
                    if (batch.Count == 0 && sz >= cap)
                    {
                        // ⚠️ 1 seul élément qui dépasse la limite => on l’envoie QUAND MÊME, mais seul
                        ksel.Queue.TryDequeue(out var it);
                        batch.Add(it);
                        usedBytes = sz;
                        break; // on stoppe ici: batch=1, "gros" item
                    }

                    // Sinon, si ajouter cet élément dépasse le cap et qu'on a déjà qqch, on s'arrête
                    if (batch.Count > 0 && (usedBytes + sz) > cap)
                        break;
                }

                // Ok, on peut ajouter cet élément
                ksel.Queue.TryDequeue(out var accepted);
                batch.Add(accepted);
                usedBytes += sz;
            }

            if (batch.Count == 0)
                continue;

            try
            {
                var reqs = batch.Select(b => b.req).ToList();
                Console.WriteLine($"[Batch] kind={ksel.Name} count={batch.Count} bytes~{usedBytes} at {DateTime.UtcNow:O}");
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
            while (k.Queue.TryDequeue(out var it))
                it.tcs.TrySetException(new TaskCanceledException("Batcher stopped"));
    }
}


    // ---- débit par minute & paliers (par kind) ----

    private static void RecordArrival(Kind k)
    {
        k.Arrivals.Enqueue(Stopwatch.GetTimestamp());
        PruneArrivals(k);
    }

    private static int ComputeRatePerMinute(Kind k)
    {
        PruneArrivals(k);
        return k.Arrivals.Count;
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
            if ((now - ts) > windowTicks) k.Arrivals.TryDequeue(out _);
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
