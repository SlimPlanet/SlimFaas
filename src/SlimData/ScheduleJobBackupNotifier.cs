using System.Threading.Channels;

namespace SlimData;

/// <summary>
/// Notifies consumers that ScheduleJob data has changed and a backup is needed.
/// Uses a bounded channel so that multiple rapid changes are coalesced.
/// </summary>
public interface IScheduleJobBackupNotifier
{
    /// <summary>Signal that ScheduleJob data has changed.</summary>
    void NotifyChange();

    /// <summary>Wait for the next change signal.</summary>
    ValueTask<bool> WaitForChangeAsync(CancellationToken cancellationToken);
}

public sealed class ScheduleJobBackupNotifier : IScheduleJobBackupNotifier
{
    // Bounded(1) + DropOldest → multiple rapid writes are coalesced into a single signal
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public void NotifyChange()
    {
        _channel.Writer.TryWrite(true);
    }

    public ValueTask<bool> WaitForChangeAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.WaitToReadAsync(cancellationToken);
    }
}

