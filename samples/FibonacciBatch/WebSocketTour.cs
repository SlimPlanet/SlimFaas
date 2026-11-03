using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class WebSocketTour(HttpClient http, string functionName, Func<int, int> calculate)
{
    internal static Uri[] ParseUrls(string value)
    {
        var urls = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new Uri(token, UriKind.Absolute)).Distinct().ToArray();
        if (urls.Length == 0 || urls.Any(uri => uri.Scheme is not ("ws" or "wss")))
            throw new ArgumentException("Provide at least one ws:// or wss:// URL.", nameof(value));
        return urls;
    }

    internal async Task ReportRegistrationAsync(IReadOnlyList<SlimFaasClient.SlimFaasClient> clients,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var ids = clients.Select(client => client.IsConnected ? client.ConnectionId : null).OfType<string>().ToArray();
            if (ids.Length == clients.Count)
            {
                var receipt = new WebSocketTourRegistration(functionName, ids.Length, ids);
                await WriteJsonAsync("ready",
                    JsonSerializer.SerializeToUtf8Bytes(receipt, WebSocketTourJsonContext.Default.WebSocketTourRegistration),
                    cancellationToken);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    internal bool Matches(string path, string mode) =>
        path.TrimStart('/') == $"tour/ws/{functionName}/{mode}";

    internal WebSocketTourReceipt? Compute(byte[]? body, string mode, string elementId,
        string? streamName = null, string? messageId = null)
    {
        WebSocketTourInput? input;
        try
        {
            input = body is null ? null : JsonSerializer.Deserialize(body, WebSocketTourJsonContext.Default.WebSocketTourInput);
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"[WS] Invalid tour JSON for {functionName}: {exception.Message}");
            return null;
        }
        if (input is null || input.Input is < 0 or > 40 || string.IsNullOrWhiteSpace(input.Nonce) || input.Nonce.Length > 128)
        {
            Console.Error.WriteLine($"[WS] Invalid tour input for {functionName}: provide input 0..40 and a nonempty nonce of at most 128 characters.");
            return null;
        }
        return new(functionName, mode, input.Nonce, input.Input, calculate(input.Input), elementId, streamName, messageId);
    }

    internal static byte[] Serialize(WebSocketTourReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(receipt, WebSocketTourJsonContext.Default.WebSocketTourReceipt);

    internal async Task WriteAsync(WebSocketTourReceipt receipt, CancellationToken cancellationToken)
    {
        await WriteJsonAsync(receipt.Mode, Serialize(receipt), cancellationToken);
        Console.WriteLine($"[WS] Processed {receipt.Mode}: function={functionName}, nonce={receipt.Nonce}, element={receipt.ElementId}, message={receipt.MessageId}");
    }

    private async Task WriteJsonAsync(string mode, byte[] body, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.PostAsync(new Uri($"data/sets/{Uri.EscapeDataString(functionName + "-" + mode)}?ttl=600000", UriKind.Relative),
            content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

internal sealed record WebSocketTourRegistration(string Function, int Connections, string[] ConnectionIds);
internal sealed record WebSocketTourInput(
    [property: JsonRequired] int Input,
    [property: JsonRequired] string Nonce);

internal sealed record WebSocketTourReceipt(
    string Function, string Mode, string Nonce, int Input, int Result, string ElementId,
    string? StreamName, string? MessageId);

[JsonSerializable(typeof(WebSocketTourInput))]
[JsonSerializable(typeof(WebSocketTourReceipt))]
[JsonSerializable(typeof(WebSocketTourRegistration))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WebSocketTourJsonContext : JsonSerializerContext;
