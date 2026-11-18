using System.Security.Cryptography;
using System.Text.Json;
using SlimFaasClient;

namespace SlimFaasBenchmark;

internal static class BenchmarkSdkTarget
{
    internal static async Task<int> RunAsync(CommandArguments arguments)
    {
        string function = arguments.Get("function", "benchmark-dotnet");
        string directory = arguments.GetOptional("receipts") ?? throw new ArgumentException("--receipts is required.");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"receipts-dotnet-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");
        using var writeLock = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        await using var client = new SlimFaasClient.SlimFaasClient(new Uri(arguments.Get("url", "ws://127.0.0.1:31021/ws")),
            new SlimFaasClientConfig { FunctionName = function, NumberParallelRequest = 256, NumberParallelRequestPerPod = 256,
                SubscribeEvents = [new SubscribeEventConfig { Name = arguments.Get("event", "benchmark-event") }] });
        Task RecordAsync(Dictionary<string, string[]> headers, byte[] body, string kind)
            => RecordHashAsync(headers, body.Length, Convert.ToHexStringLower(SHA256.HashData(body)), kind);
        async Task RecordHashAsync(Dictionary<string, string[]> headers, long bytes, string sha256, string kind)
        {
            string? id = headers.FirstOrDefault(pair => pair.Key.Equals("X-Chaos-Id", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(new { @event = kind, id, function,
                bytes, sha256, status = 200,
                utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, pid = Environment.ProcessId });
            await writeLock.WaitAsync();
            try
            {
                await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                await file.WriteAsync(line);
                await file.WriteAsync("\n"u8.ToArray());
#pragma warning disable CA1849 // Durable receiver receipts require fsync; FlushAsync has no flushToDisk overload.
                file.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            finally { writeLock.Release(); }
        }
        client.OnAsyncRequest = async request =>
        {
            await RecordAsync(request.Headers, request.Body ?? [], "started");
            await RecordAsync(request.Headers, request.Body ?? [], "completed");
            return 200;
        };
        client.OnPublishEvent = async request =>
        {
            await RecordAsync(request.Headers, request.Body ?? [], "started");
            await RecordAsync(request.Headers, request.Body ?? [], "completed");
        };
        client.OnSyncRequest = async request =>
        {
            await request.Response.StartAsync(200);
            (long bytes, string checksum) = await CopyAndHashAsync(request.Body, request.Response);
            await RecordHashAsync(request.Headers, bytes, checksum, "started");
            await request.Response.CompleteAsync();
            await RecordHashAsync(request.Headers, bytes, checksum, "completed");
        };
        await client.RunForeverAsync(stop.Token);
        return 0;
    }

    internal static async Task<(long Bytes, string Sha256)> CopyAndHashAsync(Stream source, Stream destination)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) != 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read));
            length += read;
        }
        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

}
