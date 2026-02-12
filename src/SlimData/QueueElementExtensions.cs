using System;
using System.Collections.Immutable;

namespace SlimData;

public static class QueueElementExtensions
{
    // ---------- Petites aides internes (inlinables) ----------
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool TryGetLastTry(in QueueElement e, out QueueHttpTryElement last)
    {
        var arr = e.RetryQueueElements;
        if (arr.IsDefaultOrEmpty) { last = null!; return false; }
        last = arr[^1];
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static long SecondsToTicks(int seconds) => (long)seconds * TimeSpan.TicksPerSecond;

    public static int NumberOfTries(this QueueElement e)
    {
        var arr = e.RetryQueueElements;
        return arr.IsDefaultOrEmpty ? 0 : arr.Length;
    }
    
    public static bool IsLastTry(this QueueElement e) 
    {
        var tries = e.RetryQueueElements;
        var count = tries.IsDefault ? 0 : tries.Length;
        var retries = e.TimeoutRetriesSeconds;
        return count > 0 && (retries.IsDefaultOrEmpty || count <= retries.Length);
    }
    
    // ---------- États élémentaires ----------
    public static bool IsTimeout(this QueueElement e, long nowTicks)
    {
        if (!TryGetLastTry(e, out var last)) return false;
        // timeout si pas terminé et délai écoulé
        return last.EndTimeStamp == 0 && (last.StartTimeStamp + e.HttpTimeoutTicks) <= nowTicks;
    }

    public static bool IsRunning(this QueueElement e, long nowTicks)
    {
        if (!TryGetLastTry(e, out var last)) return false;
        // en cours seulement si non timeout et pas d’End
        return last.EndTimeStamp == 0 && !e.IsTimeout(nowTicks);
    }

    public static bool IsFinished(this QueueElement e, long nowTicks)
    {
        var tries = e.RetryQueueElements;
        var count = tries.IsDefault ? 0 : tries.Length;
        if (count <= 0) return false;

        var retries = e.TimeoutRetriesSeconds;
        var last = tries[^1];

        // Terminé si on a un End et que le code HTTP n’est pas dans les "à retenter"
        if (last.EndTimeStamp > 0 && !e.HttpStatusRetries.Contains(last.HttpCode))
            return true;

        // Si on a consommé tous les retries, on termine au premier timeout ou fin
        if (!retries.IsDefaultOrEmpty && retries.Length < count)
        {
            if (e.IsTimeout(nowTicks) || last.EndTimeStamp > 0)
                return true;
        }

        return false;
    }

    public static bool IsWaitingForRetry(this QueueElement e, long nowTicks)
    {
        var retries = e.TimeoutRetriesSeconds;
        if (retries.IsDefaultOrEmpty) return false;

        var tries = e.RetryQueueElements;
        var count = tries.IsDefault ? 0 : tries.Length;

        if (count == 0 || count > retries.Length) return false;
        if (e.IsFinished(nowTicks)) return false;
        if (e.IsRunning(nowTicks)) return false;

        var last = tries[^1];
        var retryTimeoutSec = retries[count - 1];
        var retryTicks = SecondsToTicks(retryTimeoutSec);

        if (e.IsTimeout(nowTicks))
        {
            // fenêtre d’attente après un timeout en cours (pas encore End)
            // NB: " + e.HttpTimeout" dans votre code original semble une erreur d’unités (sec vs ticks).
            // On compare des ticks à des ticks ici (correct).
            return (nowTicks - last.StartTimeStamp) <= (e.HttpTimeoutTicks + retryTicks);
        }

        if (last.EndTimeStamp != 0)
        {
            // fenêtre d’attente après un essai terminé
            return (nowTicks - last.EndTimeStamp) <= retryTicks;
        }

        return false;
    }

    // ---------- Sélections mono-pass ----------
    public static ImmutableArray<QueueElement> GetQueueTimeoutElement(this ImmutableArray<QueueElement> elements, long nowTicks)
    {
        var builder = ImmutableArray.CreateBuilder<QueueElement>(elements.Length);
        foreach (var e in elements)
            if (e.IsTimeout(nowTicks)) builder.Add(e);
        return builder.ToImmutable();
    }

    public static ImmutableArray<QueueElement> GetQueueRunningElement(this ImmutableArray<QueueElement> elements, long nowTicks)
    {
        var builder = ImmutableArray.CreateBuilder<QueueElement>(elements.Length);
        foreach (var e in elements)
            if (e.IsRunning(nowTicks)) builder.Add(e);
        return builder.ToImmutable();
    }

    public static ImmutableArray<QueueElement> GetQueueWaitingForRetryElement(this ImmutableArray<QueueElement> elements, long nowTicks)
    {
        var builder = ImmutableArray.CreateBuilder<QueueElement>(elements.Length);
        foreach (var e in elements)
            if (e.IsWaitingForRetry(nowTicks)) builder.Add(e);
        return builder.ToImmutable();
    }

    public static ImmutableArray<QueueElement> GetQueueFinishedElement(this ImmutableArray<QueueElement> elements, long nowTicks)
    {
        var builder = ImmutableArray.CreateBuilder<QueueElement>(elements.Length);
        foreach (var e in elements)
            if (e.IsFinished(nowTicks)) builder.Add(e);
        return builder.ToImmutable();
    }

    /// <summary>
    /// Version O(n) sans Except / allocations intermédiaires.
    /// Renvoie jusqu’à <paramref name="maximum"/> éléments disponibles.
    /// </summary>
    public static ImmutableArray<QueueElement> GetQueueAvailableElement(this ImmutableArray<QueueElement> elements, long nowTicks, int maximum)
    {
        if (maximum <= 0 || elements.IsDefaultOrEmpty)
            return ImmutableArray<QueueElement>.Empty;

        var builder = ImmutableArray.CreateBuilder<QueueElement>(Math.Min(maximum, elements.Length));

        foreach (var e in elements)
        {
            // ordre : on élimine rapidement les cas fréquents
            if (e.IsFinished(nowTicks)) continue;
            if (e.IsRunning(nowTicks)) continue;
            if (e.IsWaitingForRetry(nowTicks)) continue;

            builder.Add(e);
            if (builder.Count == maximum) break;
        }

        return builder.ToImmutable();
    }
}
