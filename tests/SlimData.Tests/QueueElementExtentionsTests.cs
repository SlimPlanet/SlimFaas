using System;
using System.Collections.Immutable;
using Xunit;

namespace SlimData.Tests;

public class QueueElementExtensionsTests
{
    [Fact]
    public static void QueueElementExtensions_GetQueueStates_WorkAsExpected()
    {
        // Arrange
        long nowTicks = DateTime.UtcNow.Ticks;

        // Retries (en secondes) et timeout (en secondes)
        var retries = ImmutableArray.Create(2, 6, 10);
        int httpTimeoutSeconds = 30;

        // Codes HTTP à retenter
        var httpStatusCodesWorthRetrying = ImmutableHashSet.Create(500, 502, 503);

        // Un petit décalage > timeout pour fabriquer un "timeout" côté ticks
        long timeoutSpanTicks = TimeSpan.FromSeconds(httpTimeoutSeconds + 1).Ticks;

        var b = ImmutableArray.CreateBuilder<QueueElement>();

        // -1 : 4 tentatives déjà terminées (code 500) -> retries dépassés => Finished
        b.Add(new QueueElement(
            new ReadOnlyMemory<byte>(new byte[] { 1 }),
            "-1",
            090902,
            httpTimeoutSeconds,
            retries,
            ImmutableArray.Create(
                new QueueHttpTryElement(nowTicks - 100, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks - 50,  "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks - 20,  "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks - 10,  "", nowTicks, 500)
            ),
            httpStatusCodesWorthRetrying
        ));

        // 0 : dernière tentative non terminée et très ancienne => Timeout ; count(tries)=4 > retries(3) => Finished
        b.Add(new QueueElement(
            new ReadOnlyMemory<byte>(new byte[] { 1 }),
            "0",
            090902,
            httpTimeoutSeconds,
            retries,
            ImmutableArray.Create(
                new QueueHttpTryElement(nowTicks - timeoutSpanTicks - 100, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks - timeoutSpanTicks -  50, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks - timeoutSpanTicks -  30, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks - timeoutSpanTicks -  20, "", 0,      0)
            ),
            httpStatusCodesWorthRetrying
        ));

        // 0-ok : dernière tentative terminée et code 200 (non retryable) => Finished
        b.Add(new QueueElement(
            new ReadOnlyMemory<byte>(new byte[] { 1 }),
            "0-ok",
            090902,
            httpTimeoutSeconds,
            retries,
            ImmutableArray.Create(
                new QueueHttpTryElement(nowTicks - 100, "", nowTicks, 200)
            ),
            httpStatusCodesWorthRetrying
        ));

        // 1 : dernière tentative démarrée, pas terminée, pas time-out => Running
        b.Add(new QueueElement(
            new ReadOnlyMemory<byte>(new byte[] { 1 }),
            "1",
            090902,
            httpTimeoutSeconds,
            retries,
            ImmutableArray.Create(
                new QueueHttpTryElement(nowTicks - 1000, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks -  500, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks -  200, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks -  100, "", 0,      0)
            ),
            httpStatusCodesWorthRetrying
        ));

        // 1timeout : dernière tentative démarrée il y a > timeout, non terminée, retries dépassés => Finished
        b.Add(new QueueElement(
            new ReadOnlyMemory<byte>(new byte[] { 1 }),
            "1timeout",
            090902,
            httpTimeoutSeconds,
            retries,
            ImmutableArray.Create(
                new QueueHttpTryElement(nowTicks - 1000, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks -  500, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks -  400, "", nowTicks, 500),
                new QueueHttpTryElement(nowTicks - timeoutSpanTicks, "", 0, 0)
            ),
            httpStatusCodesWorthRetrying
        ));

        // 2, 3, 4 : aucun essai => Available
        b.Add(new QueueElement(new ReadOnlyMemory<byte>(new byte[] { 1 }), "2", 090902, httpTimeoutSeconds, retries, ImmutableArray<QueueHttpTryElement>.Empty, httpStatusCodesWorthRetrying));
        b.Add(new QueueElement(new ReadOnlyMemory<byte>(new byte[] { 1 }), "3", 090902, httpTimeoutSeconds, retries, ImmutableArray<QueueHttpTryElement>.Empty, httpStatusCodesWorthRetrying));
        b.Add(new QueueElement(new ReadOnlyMemory<byte>(new byte[] { 1 }), "4", 090902, httpTimeoutSeconds, retries, ImmutableArray<QueueHttpTryElement>.Empty, httpStatusCodesWorthRetrying));

        var queueElements = b.MoveToImmutable();

        // Act
        var availableElements = queueElements.GetQueueAvailableElement(nowTicks, 3);
        var runningElements   = queueElements.GetQueueRunningElement(nowTicks);
        var finishedElements  = queueElements.GetQueueFinishedElement(nowTicks);

        // Assert
        Assert.Equal(3, availableElements.Length);
        Assert.Equal("2", availableElements[0].Id);
        Assert.Equal("3", availableElements[1].Id);
        Assert.Equal("4", availableElements[2].Id);

        Assert.Equal(1, runningElements.Length);
        Assert.Equal("1", runningElements[0].Id);

        Assert.Equal(4, finishedElements.Length);
        // (optionnel) on peut vérifier qu'ils contiennent bien les ID attendus, quel que soit l'ordre
        var finishedIds = finishedElements.Select(e => e.Id).ToImmutableHashSet();
        Assert.True(finishedIds.SetEquals(new[] { "-1", "0", "0-ok", "1timeout" }));
    }
}
