using System.Collections.Concurrent;
using System.Diagnostics;

namespace SlimData;

// Palier: au-delà de MinPerMinute, on attend Delay avant d'envoyer (pour regrouper)
public sealed record RateTier(int MinPerMinute, TimeSpan Delay);

public sealed class RateAdaptiveBatcher<TReq, TRes> : IAsyncDisposable
{
    private readonly Func<TReq, CancellationToken, Task<TRes>> _directHandler;
    private readonly Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> _batchHandler;

    private readonly ConcurrentQueue<(TReq req, TaskCompletionSource<TRes> tcs)> _queue = new();
    private readonly ConcurrentQueue<long> _arrivals = new(); // timestamps (ticks) des arrivées pour calcul/minute
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private readonly List<RateTier> _tiers; // ordonnés par MinPerMinute croissant
    private readonly int _maxBatchSize;
    private readonly int _maxQueueLength; // 0 = illimité
    private readonly TimeSpan _directBypassDelay; // pour sécurité; 0 par défaut

    public RateAdaptiveBatcher(
        Func<TReq, CancellationToken, Task<TRes>> directHandler,
        Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> batchHandler,
        IEnumerable<RateTier>? tiers = null,
        int maxBatchSize = 512,
        int maxQueueLength = 0,
        TimeSpan? directBypassDelay = null)
    {
        _directHandler = directHandler ?? throw new ArgumentNullException(nameof(directHandler));
        _batchHandler  = batchHandler  ?? throw new ArgumentNullException(nameof(batchHandler));
        _maxBatchSize  = Math.Max(1, maxBatchSize);
        _maxQueueLength = Math.Max(0, maxQueueLength);
        _directBypassDelay = directBypassDelay ?? TimeSpan.Zero;

        // Paliers par défaut (tu peux les changer à l’appel)
        // >8/min  =>  25ms
        // >30/min =>  60ms
        // >120/min => 120ms
        // >300/min => 250ms
        _tiers = (tiers ?? new[]
        {
            new RateTier(8,   TimeSpan.FromMilliseconds(60)),
            new RateTier(30,  TimeSpan.FromMilliseconds(120)),
            new RateTier(120, TimeSpan.FromMilliseconds(250)),
            new RateTier(300, TimeSpan.FromMilliseconds(500)),
        }).OrderBy(t => t.MinPerMinute).ToList();

        _loopTask = Task.Run(LoopAsync);
    }

    public async Task<TRes> EnqueueAsync(TReq request, CancellationToken ct = default)
    {
        // Backpressure optionnel
        if (_maxQueueLength > 0 && _queue.Count >= _maxQueueLength)
            throw new InvalidOperationException("Batcher queue is full");

        var tcs = new TaskCompletionSource<TRes>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((request, tcs));
        RecordArrival();
        _signal.Release(); // réveille la boucle
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Calcule le délai d’attente dynamique selon le débit courant
                var delay = ComputeDelayFromRatePerMinute();

                // S’il n’y a rien dans la queue, on attend un signal ou le délai (pour recalculer le palier)
                if (_queue.IsEmpty)
                {
                    await _signal.WaitAsync(delay, ct).ConfigureAwait(false);
                    continue; // repart au calcul
                }

                // S’il n’y a rien à attendre (palier direct) et qu’un seul item, traite en direct
                if (delay <= _directBypassDelay && _queue.Count == 1)
                {
                    if (_queue.TryDequeue(out var item))
                    {
                        try
                        {
                            var res = await _directHandler(item.req, ct).ConfigureAwait(false);
                            item.tcs.TrySetResult(res);
                        }
                        catch (Exception ex)
                        {
                            item.tcs.TrySetException(ex);
                        }
                    }
                    continue;
                }

                // Sinon on attend le délai pour grouper
                if (delay > TimeSpan.Zero)
                {
                    // Double condition: si pendant le délai, de nouveaux items arrivent, _signal fera sortir plus tôt
                    // mais on utilise une WaitAsync avec timeout pour ne pas bloquer si aucun signal
                    await _signal.WaitAsync(delay, ct).ConfigureAwait(false);
                }

                // Drain en batch (jusqu’à _maxBatchSize)
                var batch = new List<(TReq req, TaskCompletionSource<TRes> tcs)>(_maxBatchSize);
                while (batch.Count < _maxBatchSize && _queue.TryDequeue(out var wi))
                    batch.Add(wi);

                if (batch.Count == 0)
                    continue;

                // Si 1 seul élément ET palier direct, on fait quand même direct (évite overhead batch)
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

                // Exécution batch
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
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            // Vide la file en erreur
            while (_queue.TryDequeue(out var it))
                it.tcs.TrySetException(new TaskCanceledException("Batcher stopped"));
        }
    }

    // ——— Débit par minute & paliers ———

    private void RecordArrival()
    {
        // On stocke des ticks (Stopwatch plus précis que DateTime.UtcNow)
        _arrivals.Enqueue(Stopwatch.GetTimestamp());
        PruneArrivals(); // nettoie > 60s
    }

    private int ComputeRatePerMinute()
    {
        PruneArrivals();
        return _arrivals.Count; // fenêtre glissante ~60s
    }

    private TimeSpan ComputeDelayFromRatePerMinute()
    {
        var rpm = ComputeRatePerMinute();

        // Cherche le palier le plus élevé atteint
        TimeSpan delay = TimeSpan.Zero;
        foreach (var tier in _tiers)
        {
            if (rpm > tier.MinPerMinute)
                delay = tier.Delay;
            else
                break;
        }

        return delay;
    }

    private void PruneArrivals()
    {
        // Supprime les timestamps plus vieux que ~60s
        var now = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;
        var windowTicks = freq * 60L;

        while (_arrivals.TryPeek(out var ts))
        {
            if ((now - ts) > windowTicks)
                _arrivals.TryDequeue(out _);
            else
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loopTask.ConfigureAwait(false); } catch { /* ignore */ }
        _signal.Dispose();
        _cts.Dispose();
    }
}
