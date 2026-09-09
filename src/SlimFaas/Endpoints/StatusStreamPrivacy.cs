using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SlimFaas.Endpoints;

/// <summary>
/// Projects network addresses at the public SSE boundary. Raw events and deployment
/// snapshots remain available to routing and access-controlled inter-node aggregation.
/// </summary>
internal static class StatusStreamPrivacy
{
    // A public salt or an unkeyed hash would allow enumeration of private IP ranges.
    // One private process key keeps all snapshots, subscribers and aggregated events
    // consistent, without retaining an unbounded address-to-token dictionary.
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static FunctionStatusDetailed ForBrowser(FunctionStatusDetailed function) => function with
    {
        Pods = function.Pods.Select(pod => pod with { Identity = ProtectAddress(pod.Identity)! }).ToArray()
    };

    public static NetworkActivityEvent ForBrowser(NetworkActivityEvent activity) => activity with
    {
        SourcePod = ProtectAddress(activity.SourcePod, source: true),
        TargetPod = ProtectAddress(activity.TargetPod)
    };

    private static string? ProtectAddress(string? value, bool source = false)
    {
        // Pod names, local routing keys and job execution names are already identities.
        if (!IPAddress.TryParse(value, out var address)) return value;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        // Local functions share loopback with tools and callers. Never attribute an
        // incoming loopback request to an arbitrary replica after hiding its address.
        if (source && IPAddress.IsLoopback(address)) return null;

        // Canonical text also retains IPv6 scope IDs, unlike the address bytes alone.
        return "id_" + Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(address.ToString())));
    }
}
