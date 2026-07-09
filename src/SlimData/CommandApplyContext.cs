namespace SlimData;

public sealed class CommandApplyContext
{
    private readonly TaskCompletionSource<bool> _applied =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetApplied() => _applied.TrySetResult(true);

    public Task WaitAsync(CancellationToken token) => _applied.Task.WaitAsync(token);
}
