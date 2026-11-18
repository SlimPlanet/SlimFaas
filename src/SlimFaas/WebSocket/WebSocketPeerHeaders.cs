using ZLinq;

namespace SlimFaas.WebSocket;

internal static class WebSocketPeerHeaders
{
    internal static Dictionary<string, string[]> Filter(Dictionary<string, string[]> headers)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection", "Upgrade", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
            "TE", "Trailer", "Transfer-Encoding", "Host",
            WebSocketPeerAuthorization.ContextHeader, WebSocketPeerAuthorization.SignatureHeader
        };
        foreach (var (name, values) in headers)
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                foreach (var value in values.AsValueEnumerable().SelectMany(static value =>
                    value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).AsValueEnumerable()))
                    excluded.Add(value);
        return headers.AsValueEnumerable().Where(header => !excluded.Contains(header.Key))
            .ToDictionary(static header => header.Key, static header => header.Value, StringComparer.OrdinalIgnoreCase);
    }
}
