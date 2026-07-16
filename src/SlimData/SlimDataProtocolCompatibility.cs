using System.Text;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using SlimData.Commands;

namespace SlimData;

public interface ISlimDataProtocolCompatibility
{
    bool IsCompatible { get; }

    string Reason { get; }
}

public sealed class SlimDataProtocolCompatibility(ILogger<SlimDataProtocolCompatibility> logger)
    : ISlimDataProtocolCompatibility
{
    private int _status = -1;
    private string _reason = "Protocol compatibility has not been checked.";

    public bool IsCompatible => Volatile.Read(ref _status) is 1;

    public string Reason => Volatile.Read(ref _reason);

    internal void Update(bool compatible, Uri? leader, string reason)
    {
        Volatile.Write(ref _reason, reason);
        var status = compatible ? 1 : 0;
        var previous = Interlocked.Exchange(ref _status, status);
        if (previous == status)
            return;

        if (compatible)
        {
            logger.LogInformation(
                "SlimData Raft protocol is compatible. Protocol={Protocol}, Leader={Leader}, Reason={Reason}",
                SlimDataCommandProtocol.Current,
                leader,
                reason);
        }
        else
        {
            logger.LogWarning(
                "SlimData Raft protocol is incompatible or unavailable. ExpectedProtocol={Protocol}, Leader={Leader}, Reason={Reason}",
                SlimDataCommandProtocol.Current,
                leader,
                reason);
        }
    }
}

internal readonly record struct SlimDataProtocolProbeResult(
    bool IsCompatible,
    string Reason,
    string? Protocol = null,
    string? AssemblyVersion = null);

internal static class SlimDataProtocolClient
{
    internal const string HttpClientName = "SlimDataProtocol";
    private const int MaxProtocolResponseLength = 128;

    internal static async Task<SlimDataProtocolProbeResult> ProbeAsync(
        IHttpClientFactory httpClientFactory,
        Uri endpoint,
        CancellationToken token)
    {
        try
        {
            return await ProbeCoreAsync(httpClientFactory, endpoint, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new SlimDataProtocolProbeResult(false, $"Protocol endpoint is unreachable: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new SlimDataProtocolProbeResult(false, $"Protocol response could not be read: {ex.Message}");
        }
    }

    private static async Task<SlimDataProtocolProbeResult> ProbeCoreAsync(
        IHttpClientFactory httpClientFactory,
        Uri endpoint,
        CancellationToken token)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(endpoint));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return new SlimDataProtocolProbeResult(
                false,
                $"Protocol endpoint returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaxProtocolResponseLength)
            return new SlimDataProtocolProbeResult(false, "Protocol response is too large.");

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[MaxProtocolResponseLength + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0)
                break;

            length += read;
        }

        if (length > MaxProtocolResponseLength)
            return new SlimDataProtocolProbeResult(false, "Protocol response is too large.");

        var protocol = Encoding.UTF8.GetString(buffer, 0, length).Trim();

        var headerProtocol = response.Headers.TryGetValues(SlimDataCommandProtocol.HeaderName, out var protocols)
            ? protocols.FirstOrDefault()
            : null;
        var assemblyVersion = response.Headers.TryGetValues(
                SlimDataCommandProtocol.AssemblyVersionHeaderName,
                out var versions)
            ? versions.FirstOrDefault()
            : null;

        if (!string.Equals(protocol, SlimDataCommandProtocol.Current, StringComparison.Ordinal) ||
            !string.Equals(headerProtocol, SlimDataCommandProtocol.Current, StringComparison.Ordinal) ||
            !string.Equals(
                assemblyVersion,
                SlimDataCommandProtocol.AssemblyVersion,
                StringComparison.Ordinal))
        {
            return new SlimDataProtocolProbeResult(
                false,
                $"Remote protocol is '{protocol}', header is '{headerProtocol ?? "missing"}', " +
                $"and assembly is '{assemblyVersion ?? "missing"}'; expected assembly " +
                $"'{SlimDataCommandProtocol.AssemblyVersion}'.",
                protocol,
                assemblyVersion);
        }

        return new SlimDataProtocolProbeResult(
            true,
            "Remote protocol endpoint matches.",
            protocol,
            assemblyVersion);
    }

    internal static Uri BuildUri(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint)
        {
            Path = Startup.ProtocolResource,
            Query = string.Empty
        };
        return builder.Uri;
    }
}

internal sealed class SlimDataProtocolCompatibilityWorker(
    IRaftHttpCluster cluster,
    IHttpClientFactory httpClientFactory,
    SlimDataProtocolCompatibility compatibility,
    ILogger<SlimDataProtocolCompatibilityWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                compatibility.Update(false, GetLeaderUri(), ex.Message);
                logger.LogDebug(ex, "Unable to verify the SlimData leader protocol");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task CheckAsync(CancellationToken token)
    {
        if (!cluster.LeadershipToken.IsCancellationRequested)
        {
            foreach (var member in ((IRaftCluster)cluster).Members)
            {
                if (member.EndPoint is not UriEndPoint endpoint)
                {
                    compatibility.Update(false, null, "A Raft member has no HTTP endpoint.");
                    return;
                }

                var memberProtocol = await SlimDataProtocolClient.ProbeAsync(
                        httpClientFactory,
                        endpoint.Uri,
                        token)
                    .ConfigureAwait(false);
                if (!memberProtocol.IsCompatible)
                {
                    compatibility.Update(
                        false,
                        endpoint.Uri,
                        $"Raft member protocol check failed: {memberProtocol.Reason}");
                    return;
                }
            }

            compatibility.Update(
                true,
                cluster.LocalMemberAddress,
                "The local leader and all configured members use the current protocol.");
            return;
        }

        var leader = GetLeaderUri();
        if (leader is null)
        {
            compatibility.Update(false, null, "No Raft leader is currently available.");
            return;
        }

        var result = await SlimDataProtocolClient.ProbeAsync(httpClientFactory, leader, token)
            .ConfigureAwait(false);
        compatibility.Update(result.IsCompatible, leader, result.Reason);
    }

    private Uri? GetLeaderUri()
        => cluster.Leader?.EndPoint is UriEndPoint endpoint ? endpoint.Uri : null;
}
