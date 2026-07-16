using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using DotNext;
using DotNext.Buffers;
using DotNext.Diagnostics;
using DotNext.IO;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using MemoryPack;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Database;
using SlimFaas;
using SlimData.Commands;

namespace SlimData.Tests;

[ExcludeFromCodeCoverage]
internal sealed class AdvancedDebugProvider : Disposable, ILoggerProvider
{
    private readonly string prefix;

    internal AdvancedDebugProvider(string prefix) => this.prefix = prefix;

    public ILogger CreateLogger(string name) => new Logger(name, prefix);

    private sealed class Logger : ILogger
    {
        private readonly string prefix, name;

        internal Logger(string name, string prefix)
        {
            this.prefix = prefix;
            this.name = name;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Debugger.IsAttached && logLevel is not LogLevel.None;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string message = formatter?.Invoke(state, exception);

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            BufferWriterSlim<char> buffer = new BufferWriterSlim<char>(stackalloc char[128]);
            //buffer.WriteString($"[{prefix}]({new Timestamp()}){logLevel}: {message}");

            if (exception is not null)
            {
                buffer.WriteLine();
                buffer.WriteLine();
                buffer.Write(exception.ToString());
            }

            message = buffer.ToString();
            buffer.Dispose();

            Debug.WriteLine(message, name);
        }
    }
}

[ExcludeFromCodeCoverage]
internal static class TestLoggers
{
    private static AdvancedDebugProvider CreateProvider(this string prefix, IServiceProvider services)
        => new(prefix);

    internal static ILoggingBuilder AddDebugLogger(this ILoggingBuilder builder, string prefix)
    {
        AddDebugLogger(prefix, builder);
        return builder;
    }

    private static void AddDebugLogger(this string prefix, ILoggingBuilder builder)
        => builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, AdvancedDebugProvider>(prefix.CreateProvider));

    internal static ILoggerFactory CreateDebugLoggerFactory(string prefix, Action<ILoggingBuilder> builder)
        => LoggerFactory.Create(prefix.AddDebugLogger + builder);
}

[ExcludeFromCodeCoverage]
internal class LeaderChangedEvent : TaskCompletionSource<IClusterMember>
{
    internal LeaderChangedEvent()
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
    }

    internal void OnLeaderChanged(ICluster sender, IClusterMember leader)
    {
        if (leader is not null)
        {
            TrySetResult(leader);
        }
    }
}

internal sealed class LeaderTracker : LeaderChangedEvent, IClusterMemberLifetime
{
    void IClusterMemberLifetime.OnStart(IRaftCluster cluster, IDictionary<string, string> metadata)
        => cluster.LeaderChanged += OnLeaderChanged;

    void IClusterMemberLifetime.OnStop(IRaftCluster cluster)
        => cluster.LeaderChanged -= OnLeaderChanged;
}

public class RaftClusterTests
{
    private protected static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static IHost CreateHost<TStartup>(int port, IDictionary<string, string> configuration,
        IClusterMemberLifetime configurator = null,
        Func<TimeSpan, IRaftClusterMember, IFailureDetector> failureDetectorFactory = null)
        where TStartup : class =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost.UseKestrel(options => options.ListenLocalhost(port))
                .ConfigureServices(services =>
                {
                    if (configurator is not null)
                    {
                        services.AddSingleton(configurator);
                    }

                    if (failureDetectorFactory is not null)
                    {
                        services.AddSingleton(failureDetectorFactory);
                    }

                    services.AddSingleton<IDatabaseService, SlimDataService>();
                    services.AddHttpClient(SlimDataService.HttpClientName)
                        .SetHandlerLifetime(TimeSpan.FromMinutes(5))
                        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true });
                })
                .UseStartup<TStartup>()
            )
            .ConfigureHostOptions(static options => options.ShutdownTimeout = DefaultTimeout)
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(configuration))
            .ConfigureLogging(builder => builder.AddDebugLogger(port.ToString()).SetMinimumLevel(LogLevel.Debug))
            .JoinCluster()
            .Build();

    private static IRaftHttpCluster GetLocalClusterView(IHost host)
        => host.Services.GetRequiredService<IRaftHttpCluster>();

    public static string GetTemporaryDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        if (File.Exists(tempDirectory))
        {
            return GetTemporaryDirectory();
        }

        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    [Fact(Timeout = 120000)]
    public static async Task MessageExchange()
    {
        Dictionary<string, string> config1 = new()
        {
            { "partitioning", "false" },
            { "lowerElectionTimeout", "600" },
            { "upperElectionTimeout", "900" },
            { "publicEndPoint", "http://localhost:3262/" },
            { "coldStart", "true" },
            { "warmupRounds", "512" },
            { "requestTimeout", "00:01:00" },
            { SlimPersistentState.LogLocation, GetTemporaryDirectory() }
        };

        Dictionary<string, string> config2 = new()
        {
            { "partitioning", "false" },
            { "lowerElectionTimeout", "600" },
            { "upperElectionTimeout", "900" },
            { "publicEndPoint", "http://localhost:3263/" },
            { "coldStart", "false" },
            { "warmupRounds", "512" },
            { "requestTimeout", "00:01:00" },
            { SlimPersistentState.LogLocation, GetTemporaryDirectory() }
        };

        Dictionary<string, string> config3 = new()
        {
            { "partitioning", "false" },
            { "lowerElectionTimeout", "600" },
            { "upperElectionTimeout", "900" },
            { "publicEndPoint", "http://localhost:3264/" },
            { "coldStart", "false" },
            { "warmupRounds", "512" },
            { "requestTimeout", "00:01:00" },
            { SlimPersistentState.LogLocation, GetTemporaryDirectory() }
        };

        LeaderTracker listener = new();
        using IHost host1 = CreateHost<Startup>(3262, config1, listener);
        await host1.StartAsync();
        Assert.True(GetLocalClusterView(host1).Readiness.IsCompletedSuccessfully);

        using IHost host2 = CreateHost<Startup>(3263, config2);
        await host2.StartAsync();

        using IHost host3 = CreateHost<Startup>(3264, config3);
        await host3.StartAsync();

        while (GetLocalClusterView(host1).Leader == null)
        {
            await Task.Delay(200);
        }

        Assert.True(await GetLocalClusterView(host1).AddMemberAsync(GetLocalClusterView(host2).LocalMemberAddress));
        await GetLocalClusterView(host2).Readiness.WaitAsync(DefaultTimeout);

        IDatabaseService databaseServiceMaster = host1.Services.GetRequiredService<IDatabaseService>();
        // DotNext starts a new member at the leader's last index and backs up one index per warmup round.
        // Keep the lag well above its default of 10 to cover fresh followers joining an active cluster.
        const int entriesBeforeThirdMember = 128;
        for (var i = 0; i < entriesBeforeThirdMember; i++)
        {
            await databaseServiceMaster.SetAsync(
                $"pre-third-member-{i}",
                Encoding.UTF8.GetBytes(i.ToString()));
        }

        await GetLocalClusterView(host1).ForceReplicationAsync();
        Assert.True(GetLocalClusterView(host1).AuditTrail.LastEntryIndex > entriesBeforeThirdMember);

        async Task<bool> AddThirdMemberAsync() =>
            await GetLocalClusterView(host1).AddMemberAsync(GetLocalClusterView(host3).LocalMemberAddress);

        var addThirdMemberTask = AddThirdMemberAsync();
        var concurrentWritesDuringMemberAdd = Enumerable.Range(0, 5)
            .Select(i => databaseServiceMaster.SetAsync(
                $"member-add-kv-{i}",
                Encoding.UTF8.GetBytes(i.ToString())))
            .ToArray();

        Assert.True(await addThirdMemberTask);
        await Task.WhenAll(concurrentWritesDuringMemberAdd);
        await GetLocalClusterView(host3).Readiness.WaitAsync(DefaultTimeout);
        await GetLocalClusterView(host1).ForceReplicationAsync();

        using (var incompatibleEntryTimeout = new CancellationTokenSource(DefaultTimeout))
        {
            await GetLocalClusterView(host1).ReplicateAsync(
                new IncompatibleCommandLogEntry(ListRightPopCommand.Id, [0]),
                incompatibleEntryTimeout.Token);
            await GetLocalClusterView(host1).ForceReplicationAsync(incompatibleEntryTimeout.Token);

            var states = new[] { host1, host2, host3 }
                .Select(host => host.Services.GetRequiredService<SlimPersistentState>())
                .ToArray();
            while (states.Any(state => state.GetSkippedCommandMetrics()
                       .All(metric => metric.CommandId != ListRightPopCommand.Id || metric.Count < 1L)))
            {
                await Task.Delay(25, incompatibleEntryTimeout.Token);
            }

            Assert.All(states, state => Assert.Contains(
                state.GetSkippedCommandMetrics(),
                metric => metric.CommandId == ListRightPopCommand.Id && metric.Count == 1L));
        }

        for (var i = 0; i < concurrentWritesDuringMemberAdd.Length; i++)
            Assert.Equal(i.ToString(), Encoding.UTF8.GetString(await databaseServiceMaster.GetAsync($"member-add-kv-{i}") ?? []));

        IDatabaseService databaseServiceSlave = host3.Services.GetRequiredService<IDatabaseService>();

        await databaseServiceSlave.SetAsync("key1", MemoryPackSerializer.Serialize("value1") );
        Assert.Equal("value1", MemoryPackSerializer.Deserialize<string>(await databaseServiceMaster.GetAsync("key1")));
        await GetLocalClusterView(host1).ForceReplicationAsync();
        Assert.Equal("value1", MemoryPackSerializer.Deserialize<string>(await databaseServiceSlave.GetAsync("key1")));

        var incrementResult = await databaseServiceSlave.SetAsync(
            "counter1",
            operation: KeyValueOperation.IncrementInteger,
            integerDelta: 1);
        Assert.Equal(KeyValueCommandStatus.Applied, incrementResult.Status);
        Assert.Equal(1L, incrementResult.IntegerValue);
        await GetLocalClusterView(host1).ForceReplicationAsync();
        Assert.Equal("1", Encoding.UTF8.GetString(await databaseServiceMaster.GetAsync("counter1") ?? []));

        const int parallelIncrements = 20;
        var incrementTasks = Enumerable.Range(0, parallelIncrements)
            .Select(_ => databaseServiceSlave.SetAsync(
                "counter-batch",
                operation: KeyValueOperation.IncrementInteger,
                integerDelta: 1))
            .ToArray();

        var batchResults = await Task.WhenAll(incrementTasks);
        Assert.All(batchResults, result => Assert.Equal(KeyValueCommandStatus.Applied, result.Status));
        Assert.Equal(
            Enumerable.Range(1, parallelIncrements).Select(i => (long)i),
            batchResults.Select(result => result.IntegerValue!.Value).OrderBy(value => value));
        await GetLocalClusterView(host1).ForceReplicationAsync();
        Assert.Equal(parallelIncrements.ToString(), Encoding.UTF8.GetString(await databaseServiceMaster.GetAsync("counter-batch") ?? []));

        using (var source = new CancellationTokenSource(DefaultTimeout))
        {
            var leaderCluster = host1.Services.GetRequiredService<IRaftCluster>();
            var nowTicks = DateTime.UtcNow.Ticks;
            var mixedResponse = await Endpoints.AddKeyValueBatchCommand(
                new KeyValueBatchRequest([
                    new KeyValueBatchItem(
                        KeyValueOperation.Set,
                        "kv-batch-a",
                        Encoding.UTF8.GetBytes("a"),
                        null,
                        0,
                        0,
                        nowTicks),
                    new KeyValueBatchItem(
                        KeyValueOperation.IncrementInteger,
                        "kv-batch-b",
                        Array.Empty<byte>(),
                        null,
                        7,
                        0,
                        nowTicks),
                    new KeyValueBatchItem(
                        KeyValueOperation.Set,
                        "kv-batch-c",
                        Encoding.UTF8.GetBytes("c"),
                        null,
                        0,
                        0,
                        nowTicks)
                ]),
                leaderCluster,
                source);

            Assert.All(mixedResponse.Results, result => Assert.Equal(KeyValueCommandStatus.Applied, result.Status));
            Assert.Equal(7L, mixedResponse.Results[1].IntegerValue);
        }

        await GetLocalClusterView(host1).ForceReplicationAsync();
        Assert.Equal("a", Encoding.UTF8.GetString(await databaseServiceSlave.GetAsync("kv-batch-a") ?? []));
        Assert.Equal("7", Encoding.UTF8.GetString(await databaseServiceSlave.GetAsync("kv-batch-b") ?? []));
        Assert.Equal("c", Encoding.UTF8.GetString(await databaseServiceSlave.GetAsync("kv-batch-c") ?? []));

        //await databaseServiceSlave.DeleteAsync("key1");
        //Assert.Null(await databaseServiceMaster.GetAsync("key1"));
        //await GetLocalClusterView(host1).ForceReplicationAsync();
        //Assert.Null(await databaseServiceSlave.GetAsync("key1"));

        await databaseServiceSlave.HashSetAsync("hashsetKey1",
            new Dictionary<string, byte[]> { { "field1",MemoryPackSerializer.Serialize("value1") }, { "field2", MemoryPackSerializer.Serialize("value2") } });
        await GetLocalClusterView(host1).ForceReplicationAsync();
        IDictionary<string, byte[]> hashGet = await databaseServiceSlave.HashGetAllAsync("hashsetKey1");

        Assert.Equal("value1", MemoryPackSerializer.Deserialize<string>(hashGet["field1"]));
        Assert.Equal("value2", MemoryPackSerializer.Deserialize<string>(hashGet["field2"]));

        await databaseServiceSlave.HashSetDeleteAsync("hashsetKey1", "field1");
        await GetLocalClusterView(host1).ForceReplicationAsync();
        hashGet = await databaseServiceSlave.HashGetAllAsync("hashsetKey1");
        Assert.Single(hashGet);

        await databaseServiceSlave.HashSetAsync("hashsetKey1",
            new Dictionary<string, byte[]> { { "field3",MemoryPackSerializer.Serialize("value3") } });
        await databaseServiceSlave.HashSetDeleteAsync("hashsetKey1");
        await GetLocalClusterView(host1).ForceReplicationAsync();
        hashGet = await databaseServiceSlave.HashGetAllAsync("hashsetKey1");
        Assert.Empty(hashGet);

       const string customElementId = "custom-list-element-id";
       var customElementIdReturned = await databaseServiceSlave.ListLeftPushAsync(
           "listKeyCustom",
           MemoryPackSerializer.Serialize("value-custom"),
           new RetryInformation([], 30, []),
           customElementId);
       Assert.Equal(customElementId, customElementIdReturned);
       await GetLocalClusterView(host1).ForceReplicationAsync();
       var customElementList = await databaseServiceSlave.ListCountElementAsync("listKeyCustom", new List<CountType> { CountType.Available });
       Assert.Single(customElementList);
       Assert.Equal(customElementId, customElementList[0].Id);

       await databaseServiceSlave.ListLeftPushAsync("listKey1",   MemoryPackSerializer.Serialize("value1"), new RetryInformation([], 30, []));
       await GetLocalClusterView(host1).ForceReplicationAsync();
       var listLength = await databaseServiceSlave.ListCountElementAsync("listKey1" , new List<CountType>()
       {
           CountType.Available
       });
       Assert.Single(listLength);

        IList<QueueData>? listRightPop = await databaseServiceSlave.ListRightPopAsync("listKey1", Guid.NewGuid().ToString());
        Assert.Equal("value1", MemoryPackSerializer.Deserialize<string>(listRightPop.First().Data));

        ListQueueItemStatus queueItemStatus = new()
        {
            Items = new List<QueueItemStatus> {  },
        };
        foreach (QueueData queueData in listRightPop)
        {
            queueItemStatus.Items.Add(new QueueItemStatus
            {
                Id = queueData.Id,
                HttpCode = 200,
            });
        }
        await databaseServiceSlave.ListCallbackAsync("listKey1", queueItemStatus);

        await GetLocalClusterView(host1).ForceReplicationAsync();
        var listLength2 = await databaseServiceSlave.ListCountElementAsync("listKey1", new List<CountType>() { CountType.Available });

        Assert.Empty(listLength2);

        // Test Batch Queue Insert
        IList<Task<string>> tasks = new List<Task<string>>();
        const int queuedItems = 200;
        for (int i = 0; i < queuedItems; i++)
            tasks.Add(databaseServiceSlave.ListLeftPushAsync("listKey1",   MemoryPackSerializer.Serialize("value" + i), new RetryInformation([], 30, [])));

        await Task.WhenAll(tasks);

        await GetLocalClusterView(host1).ForceReplicationAsync();
        var listLength3 = await databaseServiceSlave.ListCountElementAsync("listKey1", new List<CountType>() { CountType.Available });
        Assert.Equal(queuedItems, listLength3.Count);

        var persistentStates = new[] { host1, host2, host3 }
            .Select(host => host.Services.GetRequiredService<SlimPersistentState>());
        Assert.All(persistentStates, state => Assert.DoesNotContain(
            state.GetSkippedCommandMetrics(),
            metric => metric.CommandId == ListLeftPushBatchCommand.Id));

        await host1.StopAsync();
        await host2.StopAsync();
        await host3.StopAsync();
    }

    private sealed class IncompatibleCommandLogEntry(int commandId, byte[] payload) : IRaftLogEntry
    {
        public long Term => 1L;
        public int? CommandId => commandId;
        public bool IsConfiguration => false;
        public bool IsSnapshot => false;
        public bool IsReusable => true;
        public long? Length => payload.LongLength;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : notnull, IAsyncBinaryWriter
            => writer.Invoke(payload, token);
    }
}
