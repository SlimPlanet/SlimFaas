namespace SlimData;

internal interface ISlimDataApplyContext
{
    bool IsSkipped { get; }
    string? ErrorMessage { get; }
    void SetApplied();
    void SetSkipped(string errorMessage);
    Task WaitAsync(CancellationToken token);
}

public sealed class CommandApplyContext : ISlimDataApplyContext
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

public sealed class SlimDataCommandBatchContext : ISlimDataApplyContext
{
    private readonly TaskCompletionSource<bool> _applied =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SlimDataCommandBatchResponse[]? _responses;
    private string? _errorMessage;
    private int _status;

    public bool IsSkipped => Volatile.Read(ref _status) is 2;

    public string? ErrorMessage => Volatile.Read(ref _errorMessage);

    public SlimDataCommandBatchResponse[] Responses =>
        Volatile.Read(ref _responses)
        ?? throw new InvalidOperationException("The composite SlimData response has not been produced.");

    internal void SetResponses(SlimDataCommandBatchResponse[] responses)
        => Volatile.Write(ref _responses, responses);

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
