using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SlimData;


public sealed class RateAdaptiveBatcher<TReq, TRes> : IAsyncDisposable
{
    private readonly Func<TReq, CancellationToken, Task<TRes>> _directHandler;
    private readonly Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> _batchHandler;

    private readonly ConcurrentQueue<(TReq req, TaskCompletionSource<TRes> tcs)> _queue = new();
    private readonly ConcurrentQueue<long> _arrivals = new();
    private readonly SemaphoreSlim _signal = new(0);

    private readonly List<RateTier> _tiers;
    private readonly int _maxBatchSize;
    private readonly int _maxQueueLength;
    private readonly TimeSpan _directBypassDelay;

    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;

    // Worker paresseux
    private volatile int _workerRunning; // 0 = arrêté, 1 = en cours
    private Task? _loopTask;
    public TimeSpan IdleStop { get; }   // arrêt après inactivité
    public TimeSpan MaxWaitPerTick { get; } // borne sup de Wait pour recalcul palier régulièrement

    public RateAdaptiveBatcher(
        Func<TReq, CancellationToken, Task<TRes>> directHandler,
        Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> batchHandler,
        IEnumerable<RateTier>? tiers = null,
        int maxBatchSize = 512,
        int maxQueueLength = 0,
        TimeSpan? directBypassDelay = null,
        TimeSpan? idleStop = null,
        TimeSpan? maxWaitPerTick = null)
    {
        _directHandler  = directHandler ?? throw new ArgumentNullException(nameof(directHandler));
        _batchHandler   = batchHandler  ?? throw new ArgumentNullException(nameof(batchHandler));
        _maxBatchSize   = Math.Max(1, maxBatchSize);
        _maxQueueLength = Math.Max(0, maxQueueLength);
        _directBypassDelay = directBypassDelay ?? TimeSpan.Zero;

        IdleStop = idleStop ?? TimeSpan.FromSeconds(15);
        MaxWaitPerTick = maxWaitPerTick ?? TimeSpan.FromSeconds(5);

        _tiers = (tiers ?? new[]
        {
            new RateTier(32,   TimeSpan.FromMilliseconds(60)),
            new RateTier(64, TimeSpan.FromMilliseconds(120)),
            new RateTier(128, TimeSpan.FromMilliseconds(240)),
        }).OrderBy(t => t.MinPerMinute).ToList();
    }

    public async Task<TRes> EnqueueAsync(TReq request, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RateAdaptiveBatcher<TReq, TRes>));

        if (_maxQueueLength > 0 && _queue.Count >= _maxQueueLength)
            throw new InvalidOperationException("Batcher queue is full");

        var tcs = new TaskCompletionSource<TRes>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((request, tcs));
        RecordArrival();

        // Démarre le worker si nécessaire
        StartWorkerIfNeeded();

        // Réveille le worker
        _signal.Release();

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartWorkerIfNeeded()
    {
        if (_disposed) return;

        // un seul gagnant démarre le worker
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
        {
            var ct = _disposeCts.Token;
            _loopTask = Task.Run(() => LoopAsync(ct), ct);
        }
    }
    
    static TimeSpan Min(TimeSpan a, TimeSpan b)
        => TimeSpan.FromTicks(Math.Min(a.Ticks, b.Ticks));

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            // On boucle tant qu’on n’est pas disposé ET qu’il y a de l’activité
            while (!ct.IsCancellationRequested)
            {
                var delay = ComputeDelayFromRatePerMinute();

                // Si vide, on attend soit un signal, soit IdleStop (arrêt si toujours vide)
                if (_queue.IsEmpty)
                {
                    // borne supérieure pour se « réveiller » périodiquement si delay est énorme
                    var wait = delay > TimeSpan.Zero ? Min(delay, MaxWaitPerTick) : MaxWaitPerTick;
                    // mais on impose un vrai timeout d’inactivité pour stopper
                    var idleDeadline = DateTime.UtcNow + IdleStop;

                    // boucle d’attente « douce » jusqu’à IdleStop, interrompue par un signal
                    while (!_disposed && DateTime.UtcNow < idleDeadline && _queue.IsEmpty)
                    {
                        await _signal.WaitAsync(wait, ct).ConfigureAwait(false);
                        if (!ct.IsCancellationRequested && !_queue.IsEmpty)
                            break; // du travail est arrivé
                    }

                    if (_queue.IsEmpty)
                    {
                        // Toujours rien après IdleStop -> on éteint le worker
                        Interlocked.Exchange(ref _workerRunning, 0);

                        // Double-check contre la course: si des items sont arrivés juste après,
                        // on retente de redevenir le worker et on repart.
                        if (!_queue.IsEmpty &&
                            Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
                        {
                            continue; // redevenu le worker
                        }

                        return; // s’arrête réellement
                    }

                    // du travail est arrivé, on continue
                }

                // Traitement direct si 1 seul item et délai quasi nul
                if (delay <= _directBypassDelay && _queue.Count == 1 && _queue.TryDequeue(out var single))
                {
                    try
                    {
                        var res = await _directHandler(single.req, ct).ConfigureAwait(false);
                        single.tcs.TrySetResult(res);
                    }
                    catch (Exception ex)
                    {
                        single.tcs.TrySetException(ex);
                    }
                    continue;
                }

                // Sinon on attend "un peu" (palier) mais sans bloquer trop longtemps
                if (delay > TimeSpan.Zero)
                {
                    var wait = Min(delay, MaxWaitPerTick);
                    await _signal.WaitAsync(wait, ct).ConfigureAwait(false);
                }

                // Drain en batch
                var batch = new List<(TReq req, TaskCompletionSource<TRes> tcs)>(_maxBatchSize);
                while (batch.Count < _maxBatchSize && _queue.TryDequeue(out var wi))
                    batch.Add(wi);

                if (batch.Count == 0)
                    continue;

                if (batch.Count == 1 && delay <= _directBypassDelay)
                {
                    var (req, tcs) = batch[0];
                    try
                    {
                        var res = await _directHandler(req, ct).ConfigureAwait(false);
                        tcs.TrySetResult(res);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    continue;
                }

                try
                {
                    var reqs = batch.Select(b => b.req).ToList();
                    var results = await _batchHandler(reqs, ct).ConfigureAwait(false);

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
            }
        }
        catch (OperationCanceledException) { /* normal on dispose */ }
        finally
        {
            // En cas d’arrêt, on rejette ce qui reste
            while (_queue.TryDequeue(out var it))
                it.tcs.TrySetException(new TaskCanceledException("Batcher stopped"));
        }
    }

    // ——— Débit / paliers ———

    private void RecordArrival()
    {
        _arrivals.Enqueue(Stopwatch.GetTimestamp());
        PruneArrivals();
    }

    private int ComputeRatePerMinute()
    {
        PruneArrivals();
        return _arrivals.Count;
    }

    private TimeSpan ComputeDelayFromRatePerMinute()
    {
        var rpm = ComputeRatePerMinute();
        TimeSpan delay = TimeSpan.Zero;
        foreach (var tier in _tiers)
        {
            if (rpm > tier.MinPerMinute) delay = tier.Delay;
            else break;
        }
        return delay;
    }

    private void PruneArrivals()
    {
        var now = Stopwatch.GetTimestamp();
        var windowTicks = Stopwatch.Frequency * 60L;
        while (_arrivals.TryPeek(out var ts))
        {
            if ((now - ts) > windowTicks) _arrivals.TryDequeue(out _);
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
