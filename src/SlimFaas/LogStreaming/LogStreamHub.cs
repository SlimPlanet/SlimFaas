using System.Text;

namespace SlimFaas.Logs;

/// <summary>One bounded reader per viewed source. Subscribers poll cursors, never own unbounded queues.</summary>
public sealed class LogStreamHub(IInstanceLogProvider provider) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    internal int ActiveSources { get { lock (_gate) return _sessions.Count; } }

    public Subscription? Subscribe(string source)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(source, out var session))
            {
                if (_sessions.Count >= LogLimits.Sources) return null;
                session = new Session();
                _sessions.Add(source, session);
                session.Reader = Task.Run(() => ReadAsync(source, session));
            }
            session.Clients++;
            return new Subscription(this, source, session);
        }
    }

    private async Task ReadAsync(string source, Session session)
    {
        try
        {
            session.SetStatus("Live");
            await foreach (var input in provider.ReadAsync(source, session.Stop.Token)) session.Add(input);
            session.SetStatus("Ended");
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested) { }
        catch (UnauthorizedAccessException) { session.SetStatus("Access denied"); }
        catch (FileNotFoundException) { session.SetStatus("Source removed"); }
        catch (Exception) { session.SetStatus("Disconnected"); }
    }

    private void Release(string source, Session session)
    {
        lock (_gate)
        {
            if (--session.Clients != 0) return;
            _sessions.Remove(source);
            session.Stop.Cancel();
            _ = session.Reader.ContinueWith(_ => session.Stop.Dispose(), TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        lock (_gate) foreach (var session in _sessions.Values) session.Stop.Cancel();
    }

    internal sealed class Session
    {
        private readonly object _gate = new();
        private readonly Queue<(LogLine Line, int Bytes)> _lines = new();
        private int _bytes;
        private long _next;
        private long _dropped;
        private string _status = "Connecting";
        internal readonly CancellationTokenSource Stop = new();
        internal Task Reader = Task.CompletedTask;
        internal int Clients;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal void SetStatus(string status) { lock (_gate) _status = status; }
        internal void Add(LogReadItem input)
        {
            var normalized = LogText.Normalize(input.Text, input.Truncated);
            input = normalized with { TimestampMs = input.TimestampMs ?? normalized.TimestampMs };
            int bytes = Encoding.UTF8.GetByteCount(input.Text);
            lock (_gate)
            {
                _lines.Enqueue((new LogLine(++_next, input.Text, input.TimestampMs, input.Truncated), bytes));
                _bytes += bytes;
                while (_lines.Count > LogLimits.Lines || _bytes > LogLimits.Bytes)
                { _bytes -= _lines.Dequeue().Bytes; _dropped++; }
            }
        }
        internal (LogState State, LogLine[] Lines, long Cursor) Read(long cursor, int tail)
        {
            lock (_gate)
            {
                long after = cursor < 0 ? Math.Max(0, _next - tail) : cursor;
                var lines = new List<LogLine>();
                var bytes = 0;
                foreach (var item in _lines)
                {
                    if (item.Line.Id <= after) continue;
                    if (lines.Count >= 200 || bytes + item.Bytes > 256 * 1024) break;
                    lines.Add(item.Line); bytes += item.Bytes;
                }
                return (new LogState(_status, Id, _dropped), lines.ToArray(), lines.Count > 0 ? lines[^1].Id : Math.Max(after, _next));
            }
        }
    }

    public sealed class Subscription : IDisposable
    {
        private LogStreamHub? _owner;
        private readonly string _source;
        private readonly Session _session;
        internal Subscription(LogStreamHub owner, string source, Session session)
        { _owner = owner; _source = source; _session = session; }
        public (LogState State, LogLine[] Lines, long Cursor) Read(long cursor, int tail) => _session.Read(cursor, tail);
        internal Task Completion => _session.Reader;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_source, _session);
    }
}
