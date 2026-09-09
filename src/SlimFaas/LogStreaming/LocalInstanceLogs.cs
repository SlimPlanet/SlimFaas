using System.Runtime.CompilerServices;
using SlimFaas.Kubernetes;
using SlimFaas.Local;

namespace SlimFaas.Logs;

public sealed class LocalInstanceLogs(LoadedLocalManifest manifest, LocalStateStore state,
    Func<DeploymentsInformations> topology, Func<IList<Job>> jobs) : IInstanceLogProvider
{
    public Task<LogSources> GetSourcesAsync(LogTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sources = new List<LogSource>();
        if (target.Kind == "function" && manifest.Manifest.Functions.TryGetValue(target.Name, out var function)
            && string.IsNullOrEmpty(function.DebugUrl))
        {
            foreach (var pod in topology().Functions.Where(f => f.Deployment == target.Name).SelectMany(f => f.Pods)
                         .Where(p => target.Replica is null || target.Replica == p.Name))
                sources.Add(Source(target, pod.Name, pod.ResourceVersion));
        }
        else if (target.Kind == "slimfaas" && target.Name == "slimfaas")
        {
            foreach (var pod in topology().SlimFaas.Pods.Where(p => target.Replica is null || target.Replica == p.Name))
                sources.Add(Source(target, pod.Name, pod.ResourceVersion));
        }
        else if (target.Kind == "job" && manifest.Manifest.Jobs.ContainsKey(target.Name))
        {
            foreach (var job in jobs().Where(j => JobConfiguration(j.Name) == target.Name &&
                         (target.Replica is null || target.Replica == j.Name)))
                sources.Add(Source(target, job.Name, job.StartTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return Task.FromResult(new LogSources(sources.Count == 0 ? "Logs unavailable" : "Available", sources));
    }

    private static string JobConfiguration(string name)
    {
        var split = name.LastIndexOf(KubernetesService.SlimfaasJobKey, StringComparison.Ordinal);
        return split < 0 ? "" : name[..split];
    }

    private static LogSource Source(LogTarget target, string name, string generation) =>
        LogSourceIds.Create(new LogSourceKey(target, name, "process", generation));

    public async IAsyncEnumerable<LogReadItem> ReadAsync(string source, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = LogSourceIds.Parse(source);
        var available = await GetSourcesAsync(key.Target, ct);
        if (!available.Sources.Any(s => s.Id == source)) throw new FileNotFoundException("Source removed");
        // The filename is derived only after matching a managed resource. Never
        // accept directories, symlinks or a browser-supplied filesystem path.
        if (Path.GetFileName(key.Instance) != key.Instance || key.Instance.Contains('\\'))
            throw new FileNotFoundException("Source removed");
        string path = Path.Combine(state.LogsDirectory, key.Instance + ".log");
        await using var stream = new FollowingLogFile(path, async token =>
        {
            if (!(await GetSourcesAsync(key.Target, token)).Sources.Any(s => s.Id == source))
                throw new FileNotFoundException("Source removed or restarted");
            return key.Target.Kind == "job"
                ? jobs().Any(j => j.Name == key.Instance && j.Status is JobStatus.Pending or JobStatus.Running)
                : topology().SlimFaas.Pods.Concat(topology().Functions.SelectMany(f => f.Pods))
                    .Any(p => p.Name == key.Instance && p.Started == true);
        });
        await foreach (var item in LogText.ReadLinesAsync(stream, ct)) yield return item;
    }
}

/// <summary>Tail a managed file without loading it all or buffering unterminated lines.</summary>
internal sealed class FollowingLogFile : Stream
{
    private readonly string _path;
    private readonly Func<CancellationToken, Task<bool>> _running;
    private FileStream _file;
    private DateTime _created;
    private byte[]? _notice;
    private int _noticeOffset;

    internal FollowingLogFile(string path, Func<CancellationToken, Task<bool>> running)
    {
        _path = path; _running = running;
        _file = Open();
        SeekTail(_file);
    }

    private FileStream Open()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.LinkTarget is not null) throw new FileNotFoundException("Source removed");
        _created = info.CreationTimeUtc;
        return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
    }

    internal static void SeekTail(FileStream file)
    {
        long end = file.Length;
        long floor = Math.Max(0, end - LogLimits.Bytes);
        var buffer = new byte[64 * 1024];
        int newlines = 0;
        long position = end;
        while (position > floor)
        {
            int length = (int)Math.Min(buffer.Length, position - floor);
            position -= length; file.Position = position; file.ReadExactly(buffer.AsSpan(0, length));
            for (int i = length - 1; i >= 0; i--)
                if (buffer[i] == '\n' && position + i != end - 1 && ++newlines == LogLimits.Lines)
                { file.Position = position + i + 1; return; }
        }
        file.Position = floor;
        if (floor == 0) return;
        // Drop the partial UTF-8 line at the byte budget boundary.
        while (file.Position < end && file.ReadByte() != '\n') { }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            if (_notice is not null)
            {
                int size = Math.Min(buffer.Length, _notice.Length - _noticeOffset);
                _notice.AsMemory(_noticeOffset, size).CopyTo(buffer); _noticeOffset += size;
                if (_noticeOffset == _notice.Length) { _notice = null; _noticeOffset = 0; }
                return size;
            }
            int count = await _file.ReadAsync(buffer, ct);
            if (count > 0) return count;
            bool running = await _running(ct);
            var info = new FileInfo(_path);
            if (!info.Exists) throw new FileNotFoundException("Source removed");
            if (info.CreationTimeUtc != _created || info.Length < _file.Position)
            {
                await _file.DisposeAsync(); _file = Open(); SeekTail(_file);
                _notice = "\n[Log file rotated or truncated]\n"u8.ToArray();
                continue;
            }
            if (!running) return 0;
            await Task.Delay(200, ct);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
