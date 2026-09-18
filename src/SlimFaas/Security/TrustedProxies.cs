using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace SlimFaas.Security;

/// <summary>
/// Turns <c>SlimFaas:TrustedProxies</c> (IP addresses or CIDR networks) into the
/// <see cref="ForwardedHeadersOptions"/> used to honour <c>X-Forwarded-For</c>.
/// </summary>
/// <remarks>
/// With no trusted proxy the header is ignored and every caller is classified by its
/// connection address. With trusted proxies, only one hop is honoured
/// (<see cref="ForwardedHeadersOptions.ForwardLimit"/> = 1) and only when the
/// connection comes from one of the declared proxies. The middleware defaults
/// (loopback proxies and networks) are cleared so nothing is trusted implicitly.
/// </remarks>
internal static class TrustedProxies
{
    public static bool TryParse(
        IEnumerable<string>? entries,
        out List<IPAddress> proxies,
        out List<IPNetwork> networks,
        out string? invalidEntry)
    {
        proxies = [];
        networks = [];
        invalidEntry = null;
        if (entries is null)
        {
            return true;
        }

        foreach (string? entry in entries)
        {
            string candidate = entry?.Trim() ?? string.Empty;
            if (candidate.Length == 0)
            {
                continue;
            }

            if (candidate.Contains('/', StringComparison.Ordinal))
            {
                if (IPNetwork.TryParse(candidate, out IPNetwork network))
                {
                    networks.Add(network);
                    continue;
                }
            }
            else if (IPAddress.TryParse(candidate, out IPAddress? address))
            {
                proxies.Add(address);
                continue;
            }

            invalidEntry = candidate;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the forwarded-headers options for the configured proxies, or <c>null</c>
    /// when no proxy is declared (the header must then be ignored).
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is neither an IP address nor a CIDR network.</exception>
    public static ForwardedHeadersOptions? CreateForwardedHeadersOptions(IEnumerable<string>? entries)
    {
        if (!TryParse(entries, out List<IPAddress> proxies, out List<IPNetwork> networks, out string? invalidEntry))
        {
            throw new InvalidOperationException(
                $"SlimFaas:TrustedProxies contains an entry that is neither an IP address nor a CIDR network: '{invalidEntry}'.");
        }

        if (proxies.Count == 0 && networks.Count == 0)
        {
            return null;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (IPAddress proxy in proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (IPNetwork network in networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        return options;
    }
}
