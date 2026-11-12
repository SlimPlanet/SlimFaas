using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using SlimData;
using SlimData.Options;

if (args.Length == 0 || args[0] == "server")
{
    using var listener = new TcpListener(IPAddress.Loopback, 39990);
    listener.Start(4096);
    Console.WriteLine("LISTENING_WITHOUT_ACCEPT");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

var factory = new RaftClientHandlerFactory(
    Microsoft.Extensions.Options.Options.Create(new RaftClientHandlerOptions()),
    NullLogger<RaftClientHandlerFactory>.Instance);
using var client = new HttpClient(factory.CreateHandler("raftClient"));
var bytes = new byte[8 * 1024 * 1024];
for (var batch = 0; batch < 12; batch++)
{
    await Parallel.ForEachAsync(Enumerable.Range(0, 10), new ParallelOptions { MaxDegreeOfParallelism = 4 },
        async (_, token) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:39990/raft")
            {
                Content = new ByteArrayContent(bytes)
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            try
            {
                using HttpResponseMessage response = await client.SendAsync(request, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
        });
    var rows = await File.ReadAllLinesAsync("/sys/fs/cgroup/memory.stat");
    string socketMemory = rows.Single(row => row.StartsWith("sock ", StringComparison.Ordinal));
    string current = await File.ReadAllTextAsync("/sys/fs/cgroup/memory.current");
    Console.WriteLine($"batch={batch} cancelled={(batch + 1) * 10} {socketMemory} total={current.Trim()}");
}
