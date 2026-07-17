namespace SlimData;

public sealed class CommandApplyContext
{
    private readonly TaskCompletionSource<bool> _applied =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _errorMessage;
    private int _status;

    public bool IsSkipped => Volatile.Read(ref _status) is 2;

    public string? ErrorMessage => Volatile.Read(ref _errorMessage);

    public void SetApplied()
    {
        if (Interlocked.CompareExchange(ref _status, 1, 0) is 0)
            _applied.TrySetResult(true);
    }

    public void SetSkipped(string errorMessage)
    {
        if (Interlocked.CompareExchange(ref _status, 2, 0) is not 0)
            return;

        Volatile.Write(ref _errorMessage, errorMessage);
        _applied.TrySetResult(false);
    }

    public Task WaitAsync(CancellationToken token) => _applied.Task.WaitAsync(token);
}
