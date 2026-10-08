using System.Net;

namespace SlimFaas.Security;

/// <summary>
/// Exact comparison between the address of the TCP connection and the addresses
/// of known workloads (function pods, job pods, SlimFaas members).
/// </summary>
/// <remarks>
/// Only <see cref="ConnectionInfo.RemoteIpAddress"/> is a trustworthy source. When a
/// reverse proxy sits in front of SlimFaas, <c>SlimFaas:TrustedProxies</c> lets the
/// forwarded-headers middleware replace the remote address with the original client
/// address before this comparison runs; the raw <c>X-Forwarded-For</c> header is
/// never read here.
/// </remarks>
internal static class RemoteAddress
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="remote"/> equals one of <paramref name="addresses"/>.
    /// IPv4-mapped IPv6 addresses (<c>::ffff:10.0.0.1</c>) compare equal to their IPv4 form.
    /// Entries that are empty or not valid IP addresses never match.
    /// </summary>
    public static bool IsAnyOf(IPAddress? remote, IEnumerable<string?> addresses)
    {
        if (remote is null)
        {
            return false;
        }

        IPAddress normalizedRemote = Normalize(remote);
        foreach (string? address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out IPAddress? parsed))
            {
                continue;
            }

            if (Normalize(parsed).Equals(normalizedRemote))
            {
                return true;
            }
        }

        return false;
    }

    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
