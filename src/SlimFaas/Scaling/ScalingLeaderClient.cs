using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Scaling;

public interface IScalingLeaderClient
{
    Task<ScalingState> GetStateAsync(string function, CancellationToken ct);
    Task<ScalingSimulationResponse> SimulateAsync(ScalingSimulationRequest request, CancellationToken ct);
}

public sealed class ScalingLeaderClient(IMasterService master, IReplicasService replicas, StatusLeader leader,
    ScalingDiagnosticsStore diagnostics, ScalingSimulationService simulator, IHttpClientFactory clients,
    IOptions<SlimFaasOptions> options, INamespaceProvider namespaceProvider) : IScalingLeaderClient
{
    public const string HttpClientName = "ScalingDiagnostics";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Leader, long Time, ScalingState State)> _cache = new(StringComparer.Ordinal);

    public async Task<ScalingState> GetStateAsync(string function, CancellationToken ct)
    {
        if (master.IsMaster) return LocalState(function);
        await _gate.WaitAsync(ct);
        try
        {
            var endpoint = LeaderEndpoint();
            long now = Environment.TickCount64;
            foreach (var old in _cache.Where(e => now - e.Value.Time > 10_000).Select(e => e.Key).ToArray()) _cache.Remove(old);
            if (_cache.TryGetValue(function, out var cache) && cache.Leader == endpoint.AbsoluteUri &&
                now - cache.Time < options.Value.StatusStream.StateIntervalMilliseconds) return cache.State;
            using var message = new HttpRequestMessage(HttpMethod.Get,
                new Uri(endpoint, "/internal/scaling/state?function=" + Uri.EscapeDataString(function)));
            var state = await SendAsync(message, ScalingJsonContext.Default.ScalingState, ct);
            if (state.Function != function || state.Status != "Live" && state.Status != "WaitingForDecision")
                throw new ScalingSimulationException(503, "The leader is changing. Reconnect shortly.");
            // At most eight bounded frames are cached, shared by all viewers on this node.
            if (_cache.Count >= 8) _cache.Remove(_cache.MinBy(e => e.Value.Time).Key);
            _cache[function] = (endpoint.AbsoluteUri, Environment.TickCount64, state);
            return state;
        }
        finally { _gate.Release(); }
    }

    public ScalingState LocalState(string function)
    {
        if (!master.IsMaster) throw new ScalingSimulationException(503, "This node is no longer the leader.");
        var workload = replicas.Deployments.Functions.FirstOrDefault(f => f.Deployment == function)
            ?? throw new ScalingSimulationException(404, "Function not found.");
        diagnostics.SetLeadership(true);
        return diagnostics.GetState(function, workload.Pods.Count(p => p.Ready == true), workload.Replicas,
            options.Value.StatusStream.StateIntervalMilliseconds);
    }

    public async Task<ScalingSimulationResponse> SimulateAsync(ScalingSimulationRequest request, CancellationToken ct)
    {
        if (master.IsMaster) return await LocalSimulationAsync(request, ct);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(LeaderEndpoint(), "/internal/scaling/simulate"));
        message.Content = JsonContent.Create(request, ScalingJsonContext.Default.ScalingSimulationRequest);
        return await SendAsync(message, ScalingJsonContext.Default.ScalingSimulationResponse, ct);
    }

    public async Task<ScalingSimulationResponse> LocalSimulationAsync(ScalingSimulationRequest request, CancellationToken ct)
    {
        if (!master.IsMaster) throw new ScalingSimulationException(503, "This node is no longer the leader.");
        var response = await simulator.SimulateAsync(request, ct);
        if (!master.IsMaster) throw new ScalingSimulationException(503, "Leadership changed during capture. Retry the simulation.");
        return response;
    }

    private Uri LeaderEndpoint()
    {
        var pods = replicas.Deployments.SlimFaas.Pods;
        string? name = leader.FindLeader(pods);
        var pod = pods.FirstOrDefault(p => p.Name == name);
        var endpoint = pod is null ? null : NetworkActivitySyncWorker.ActivityEndpoint(pod, options.Value, namespaceProvider.CurrentNamespace);
        return endpoint ?? throw new ScalingSimulationException(503, "The scaling leader is not available yet.");
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Limit both declared and streamed bodies from peers, including older/misconfigured nodes.
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new ScalingSimulationException(503, "Leader response is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > 1024 * 1024) throw new ScalingSimulationException(503, "Leader response is too large.");
                buffer.Write(chunk, 0, count);
            }
            if (!response.IsSuccessStatusCode)
            {
                var error = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), ScalingJsonContext.Default.ScalingError);
                throw new ScalingSimulationException((int)response.StatusCode, error?.Error ?? "Leader request failed.");
            }
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), type)
                ?? throw new ScalingSimulationException(503, "Leader returned an empty response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ScalingSimulationException(503, "The scaling leader did not respond in time."); }
        catch (HttpRequestException) { throw new ScalingSimulationException(503, "Unable to reach the scaling leader."); }
        catch (JsonException) { throw new ScalingSimulationException(503, "The leader does not support scaling diagnostics yet."); }
    }
}
