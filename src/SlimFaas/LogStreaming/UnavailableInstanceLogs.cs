namespace SlimFaas.Logs;

public sealed class UnavailableInstanceLogs : IInstanceLogProvider
{
    public Task<LogSources> GetSourcesAsync(LogTarget target, CancellationToken ct) =>
        Task.FromResult(new LogSources("Logs unavailable", []));

    public IAsyncEnumerable<LogReadItem> ReadAsync(string source, CancellationToken ct) =>
        throw new FileNotFoundException("Logs unavailable");
}
