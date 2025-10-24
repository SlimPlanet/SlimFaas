namespace SlimFaas.Database;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

public sealed class AdaptiveBatcher<TReq, TRes> : IAsyncDisposable
{
    private readonly Func<TReq, CancellationToken, Task<TRes>> _directHandler;
    private readonly Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> _batchHandler;
    private readonly Channel<(TReq req, TaskCompletionSource<TRes> tcs)> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _enterWindow;
    private readonly int _enterCount;
    private readonly TimeSpan _exitWindow;
    private readonly int _exitCount;
    private readonly int _ringSizeMask;
    private readonly long[] _ring;
    private int _idx;
    private volatile bool _batchMode;
    private readonly int _maxBatchSize;

    public AdaptiveBatcher(
        Func<TReq, CancellationToken, Task<TRes>> directHandler,
        Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> batchHandler,
        TimeSpan flushInterval,
        AdaptiveBatcherThresholds thresholds,
        int ringSizePowerOf2 = 10,   // 2^10 = 1024
        int maxBatchSize = 512)      // boucle HTTP/traitement bornée
    {
        _directHandler = directHandler ?? throw new ArgumentNullException(nameof(directHandler));
        _batchHandler  = batchHandler  ?? throw new ArgumentNullException(nameof(batchHandler));
        _flushInterval = flushInterval;
        _enterWindow   = thresholds.EnterWindow;
        _enterCount    = thresholds.EnterCount;
        _exitWindow    = thresholds.ExitWindow;
        _exitCount     = thresholds.ExitCount;
        _maxBatchSize  = Math.Max(1, maxBatchSize);

        var capacity = 1 << ringSizePowerOf2;
        _ring = new long[capacity];
        _ringSizeMask = capacity - 1;

        _channel = Channel.CreateUnbounded<(TReq, TaskCompletionSource<TRes>)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        _loopTask = Task.Run(LoopAsync);
    }

    private int _pending; // nombre d'éléments actuellement dans le channel


    public async Task<TRes> EnqueueAsync(TReq request, CancellationToken ct = default)
    {
        RecordArrival();

       /* if (!ShouldBatch() && Volatile.Read(ref _pending) == 0)
        {
            return await _directHandler(request, ct).ConfigureAwait(false);
        }*/

        var tcs = new TaskCompletionSource<TRes>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _pending);
        try
        {
            await _channel.Writer.WriteAsync((request, tcs), ct).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _pending); // rollback si l'écriture échoue
            throw;
        }
        return await tcs.Task.ConfigureAwait(false);
    }


    private async Task LoopAsync()
    {
        var reader = _channel.Reader;
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                (TReq req, TaskCompletionSource<TRes> tcs) first;
                try { first = await reader.ReadAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var buffer = new List<(TReq req, TaskCompletionSource<TRes> tcs)> { first };
                var start = ValueStopwatch.StartNew();

                while (start.Elapsed < _flushInterval && buffer.Count < _maxBatchSize && reader.TryRead(out var more))
                    buffer.Add(more);

                // on a retiré 'buffer.Count' éléments du channel
                Interlocked.Add(ref _pending, -buffer.Count);

                try
                {
                    await ProcessBufferAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    foreach (var item in buffer)
                        item.tcs.TrySetException(ex);
                }
            }
        }
        finally
        {
            // vider la file en erreur
            while (reader.TryRead(out var item))
            {
                Interlocked.Decrement(ref _pending);
                item.tcs.TrySetException(new TaskCanceledException("Batcher stopped"));
            }
        }
    }


    private async Task ProcessBufferAsync(List<(TReq req, TaskCompletionSource<TRes> tcs)> buffer, CancellationToken ct)
    {
        // Si seulement 1 élément et pas « obligé » de batcher, on reste direct
        if (!_batchMode && buffer.Count == 1)
        {
            var (req, tcs) = buffer[0];
            var res = await _directHandler(req, ct).ConfigureAwait(false);
            tcs.TrySetResult(res);
            return;
        }

        // Batch
        var reqs = buffer.Select(x => x.req).ToList();
        var results = await _batchHandler(reqs, ct).ConfigureAwait(false);

        if (results.Count != buffer.Count)
            throw new InvalidOperationException("Batch handler must return same count of results as inputs.");

        for (int i = 0; i < results.Count; i++)
            buffer[i].tcs.TrySetResult(results[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordArrival()
    {
        var i = Interlocked.Increment(ref _idx);
        _ring[i & _ringSizeMask] = Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldBatch()
    {
        var now = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;

        int inEnter = 0, inExit = 0;
        var enterTicks = (long)(_enterWindow.TotalSeconds * freq);
        var exitTicks  = (long)(_exitWindow.TotalSeconds  * freq);

        for (int k = 0; k < _ring.Length; k++)
        {
            var t = Volatile.Read(ref _ring[k]);
            if (t == 0) continue;
            var dt = now - t;
            if (dt <= enterTicks) inEnter++;
            if (dt <= exitTicks)  inExit++;
        }

        if (!_batchMode && inEnter >= _enterCount) _batchMode = true;
        else if (_batchMode && inExit < _exitCount) _batchMode = false;

        return _batchMode;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loopTask.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private readonly struct ValueStopwatch
    {
        private static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;
        private readonly long _start;
        private ValueStopwatch(long start) => _start = start;
        public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());
        public TimeSpan Elapsed
        {
            get
            {
                var dt = Stopwatch.GetTimestamp() - _start;
                return TimeSpan.FromSeconds(dt * TickToSeconds);
            }
        }
    }
}

public readonly record struct AdaptiveBatcherThresholds(
    TimeSpan EnterWindow, int EnterCount,
    TimeSpan ExitWindow,  int ExitCount);
