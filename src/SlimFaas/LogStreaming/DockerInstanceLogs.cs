using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Logs;

public sealed class DockerInstanceLogs(DockerService docker, IReplicasService replicas, IJobService jobs) : IInstanceLogProvider
{
    public async Task<LogSources> GetSourcesAsync(LogTarget target, CancellationToken ct)
    {
        IEnumerable<string> names = target.Kind switch
        {
            "function" => replicas.Deployments.Functions.Where(f => f.Deployment == target.Name && f.Namespace != "websocket-virtual")
                .SelectMany(f => f.Pods).Select(p => p.Name),
            "slimfaas" when target.Name == "slimfaas" => replicas.Deployments.SlimFaas.Pods.Select(p => p.Name),
            "job" => jobs.Jobs.Where(j => j.Name.StartsWith(target.Name + KubernetesService.SlimfaasJobKey, StringComparison.Ordinal)).Select(j => j.Name),
            _ => []
        };
        var result = new List<LogSource>();
        foreach (var name in names.Where(n => target.Replica is null || target.Replica == n))
        {
            ct.ThrowIfCancellationRequested();
            var container = await docker.InspectLogContainerAsync(name, ct);
            if (container is null) continue;
            result.Add(LogSourceIds.Create(new LogSourceKey(target, name, "container",
                $"{container.Id}/{container.State?.StartedAt?.UtcTicks}")));
        }
        return new LogSources(result.Count == 0 ? "Logs unavailable" : "Available", result);
    }

    public async IAsyncEnumerable<LogReadItem> ReadAsync(string source, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = LogSourceIds.Parse(source);
        if (!(await GetSourcesAsync(key.Target, ct)).Sources.Any(s => s.Id == source))
            throw new FileNotFoundException("Source removed or restarted");
        var container = await docker.InspectLogContainerAsync(key.Instance, ct)
            ?? throw new FileNotFoundException("Source removed");
        using var response = await docker.OpenLogResponseAsync(container.Id, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Log access denied");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new FileNotFoundException("Source removed");
        response.EnsureSuccessStatusCode();
        await using var raw = await response.Content.ReadAsStreamAsync(ct);
        var lines = container.Config?.Tty == true ? LogText.ReadLinesAsync(raw, ct) : DockerLogReader.ReadAsync(raw, ct);
        await foreach (var line in lines) yield return line;
    }
}

/// <summary>Docker's non-TTY response frames have an 8-byte header and arbitrary payload boundaries.</summary>
internal static class DockerLogReader
{
    public static async IAsyncEnumerable<LogReadItem> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[8];
        var chunk = new byte[4096];
        var outputs = new[] { new Decoder(), new Decoder() };
        while (await stream.ReadAsync(header.AsMemory(0, 1), ct) != 0)
        {
            await stream.ReadExactlyAsync(header.AsMemory(1), ct);
            if (header[0] is not (1 or 2) || header[1] != 0 || header[2] != 0 || header[3] != 0)
                throw new IOException("Invalid Docker log frame");
            uint remaining = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            var decoder = outputs[header[0] - 1];
            while (remaining > 0)
            {
                int count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min((uint)chunk.Length, remaining)), ct);
                if (count == 0) throw new EndOfStreamException("Incomplete Docker log frame");
                remaining -= (uint)count;
                foreach (var line in decoder.Accept(chunk.AsSpan(0, count), false)) yield return line;
            }
        }
        foreach (var decoder in outputs)
            foreach (var line in decoder.Accept([], true)) yield return line;
    }

    private sealed class Decoder
    {
        private readonly System.Text.Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private readonly char[] _chars = new char[8192];
        private readonly StringBuilder _line = new();
        private bool _truncated;
        internal List<LogReadItem> Accept(ReadOnlySpan<byte> bytes, bool end)
        {
            int count = _utf8.GetChars(bytes, _chars, end);
            var lines = new List<LogReadItem>();
            for (int i = 0; i < count; i++)
            {
                if (_chars[i] == '\n')
                {
                    lines.Add(LogText.Normalize(_line.ToString(), _truncated));
                    _line.Clear(); _truncated = false;
                }
                else if (_line.Length < LogLimits.LineBytes) _line.Append(_chars[i]);
                else _truncated = true;
            }
            if (end && (_line.Length > 0 || _truncated)) lines.Add(LogText.Normalize(_line.ToString(), _truncated));
            return lines;
        }
    }
}
