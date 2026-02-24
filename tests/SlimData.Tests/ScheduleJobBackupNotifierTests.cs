using SlimData;

namespace SlimData.Tests;

public class ScheduleJobBackupNotifierTests
{
    [Fact(DisplayName = "NotifyChange suivi de WaitForChangeAsync retourne true")]
    public async Task NotifyChange_Should_Signal_WaitForChangeAsync()
    {
        // Arrange
        var sut = new ScheduleJobBackupNotifier();

        // Act
        sut.NotifyChange();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await sut.WaitForChangeAsync(cts.Token);

        // Assert
        Assert.True(result);
    }

    [Fact(DisplayName = "WaitForChangeAsync bloque tant qu'il n'y a pas de signal")]
    public async Task WaitForChangeAsync_Should_Block_Until_Signal()
    {
        // Arrange
        var sut = new ScheduleJobBackupNotifier();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act & Assert — doit être annulé car pas de signal
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.WaitForChangeAsync(cts.Token));
    }

    [Fact(DisplayName = "Plusieurs NotifyChange rapides sont coalescés — WaitForChangeAsync retourne immédiatement")]
    public async Task Multiple_NotifyChange_Should_Coalesce_Into_Immediate_Signal()
    {
        // Arrange
        var sut = new ScheduleJobBackupNotifier();

        // Act : 5 notifications rapides
        for (int i = 0; i < 5; i++)
        {
            sut.NotifyChange();
        }

        // Le channel bounded(1) + DropOldest coalèsce tout en 1 seul élément
        // WaitForChangeAsync doit retourner immédiatement (pas de blocage)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await sut.WaitForChangeAsync(cts.Token);
        Assert.True(result);
    }

    [Fact(DisplayName = "NotifyChange après consommation produit un nouveau signal")]
    public async Task NotifyChange_After_Consume_Should_Produce_New_Signal()
    {
        // Arrange
        var sut = new ScheduleJobBackupNotifier();

        // Premier cycle
        sut.NotifyChange();
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await sut.WaitForChangeAsync(cts1.Token);

        // Deuxième cycle
        sut.NotifyChange();
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await sut.WaitForChangeAsync(cts2.Token);

        // Assert
        Assert.True(result);
    }

    [Fact(DisplayName = "WaitForChangeAsync respecte l'annulation")]
    public async Task WaitForChangeAsync_Should_Respect_Cancellation()
    {
        // Arrange
        var sut = new ScheduleJobBackupNotifier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert — TaskCanceledException hérite de OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.WaitForChangeAsync(cts.Token));
    }
}



