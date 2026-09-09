using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlimFaas.Endpoints;
using SlimFaas.Logs;
using SlimFaas.Options;

namespace SlimFaas.Tests.Endpoints;

public sealed class LogStreamTests
{
    private sealed class Provider : IInstanceLogProvider
    {
        internal readonly LogTarget Target = new("function", "function", "replica");
        internal LogSource Source => LogSourceIds.Create(new LogSourceKey(Target, "replica", "app", "generation"));
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Readers;
        internal Exception? Failure;
        public Task<LogSources> GetSourcesAsync(LogTarget target, CancellationToken ct) =>
            Task.FromResult(new LogSources("Available", target == Target ? [Source] : []));
        public async IAsyncEnumerable<LogReadItem> ReadAsync(string source, [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref Readers);
            try
            {
                if (Failure is not null) throw Failure;
                yield return new LogReadItem("application log", 123);
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { Stopped.TrySetResult(); }
        }
    }

    private static Task<IHost> Host(Provider provider, bool expose = true, bool front = true, int limit = 2) =>
        new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting(); services.AddOptions<SlimFaasOptions>().Configure(o =>
            { o.ExposeLogs = expose; o.EnableFront = front; o.StatusStream.MaxSseClients = limit; });
            services.AddSingleton<IInstanceLogProvider>(provider);
            services.AddSingleton<LogStreamHub>(); services.AddSingleton<NetworkActivityTracker>();
            services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
        }).Configure(app => { app.UseRouting(); app.UseEndpoints(e => e.MapLogStreamEndpoints()); })).StartAsync();

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Log_endpoints_require_both_explicit_exposure_and_front(bool expose, bool front)
    {
        var provider = new Provider(); using var host = await Host(provider, expose, front);
        using var client = host.GetTestClient();
        foreach (var path in new[] { "status-log-sources?kind=function&name=function&replica=replica", $"status-logs-stream?source={provider.Source.Id}" })
        {
            using var response = await client.GetAsync("http://localhost:5000/" + path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(0, provider.Readers);
    }

    [Fact]
    public async Task Discovery_only_returns_managed_sources_and_validates_queries_and_ports()
    {
        var provider = new Provider(); using var host = await Host(provider); using var client = host.GetTestClient();
        using var response = await client.GetAsync("http://localhost:5000/status-log-sources?kind=function&name=function&replica=replica");
        var sources = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), LogJsonContext.Default.LogSources)!;
        Assert.Equal(provider.Source, Assert.Single(sources.Sources));
        Assert.Equal(0, provider.Readers);
        using var invalid = await client.GetAsync("http://localhost:5000/status-log-sources?kind=filesystem&name=/etc/passwd");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var port = await client.GetAsync("http://localhost:12345/status-log-sources?kind=function&name=function");
        Assert.Equal(HttpStatusCode.NotFound, port.StatusCode);
    }

    [Theory]
    [InlineData("source=invalid", 400)]
    [InlineData("tail=10001", 400)]
    [InlineData("tail=0", 400)]
    [InlineData("tail=text", 400)]
    public async Task Invalid_sources_and_tail_limits_do_not_open_readers(string query, int expected)
    {
        var provider = new Provider(); using var host = await Host(provider); using var client = host.GetTestClient();
        using var response = await client.GetAsync($"http://localhost:5000/status-logs-stream?{query}");
        Assert.Equal(expected, (int)response.StatusCode); Assert.Equal(0, provider.Readers);
    }

    [Fact]
    public async Task Forged_or_obsolete_source_references_are_revalidated()
    {
        var provider = new Provider(); using var host = await Host(provider); using var client = host.GetTestClient();
        var key = LogSourceIds.Parse(provider.Source.Id);
        foreach (var forged in new[] { key with { Generation = "old" }, key with { Instance = "../../private" }, key with { Target = new("function", "other", "replica") } })
        {
            using var response = await client.GetAsync($"http://localhost:5000/status-logs-stream?source={LogSourceIds.Create(forged).Id}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(0, provider.Readers);
    }

    [Fact]
    public async Task Logs_share_sse_quota_without_starting_traffic_and_cancel_the_reader_on_disconnect()
    {
        var provider = new Provider(); using var host = await Host(provider, limit: 1); using var client = host.GetTestClient();
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string url = $"http://localhost:5000/status-logs-stream?source={provider.Source.Id}";
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, stopping.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tracker = host.Services.GetRequiredService<NetworkActivityTracker>();
        Assert.False(tracker.HasSubscribers); Assert.Equal(0, tracker.LiveSessionStartedAt);
        using var refused = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        await stopping.CancelAsync(); response.Dispose();
        await provider.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, host.Services.GetRequiredService<LogStreamHub>().ActiveSources);
    }

    [Fact]
    public async Task Viewers_share_one_reader_and_all_resources_are_released_by_the_last_viewer()
    {
        var provider = new Provider(); using var hub = new LogStreamHub(provider);
        using var first = hub.Subscribe("one")!; var second = hub.Subscribe("one")!;
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.Readers); Assert.Equal(1, hub.ActiveSources);
        Assert.Equal("application log", Assert.Single(first.Read(-1, 10000).Lines).Text);
        first.Dispose(); Assert.False(provider.Stopped.Task.IsCompleted);
        second.Dispose(); await provider.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, hub.ActiveSources);
    }

    [Fact]
    public void Source_limit_and_slow_viewer_buffers_are_bounded()
    {
        using var hub = new LogStreamHub(new Provider());
        var viewers = Enumerable.Range(0, 4).Select(n => hub.Subscribe(n.ToString())!).ToArray();
        Assert.Null(hub.Subscribe("fifth"));
        foreach (var viewer in viewers) viewer.Dispose();
        Assert.Equal(0, hub.ActiveSources);
        var session = new LogStreamHub.Session();
        for (int i = 0; i < 12000; i++) session.Add(new LogReadItem("same message"));
        var page = session.Read(0, 10000);
        Assert.Equal(200, page.Lines.Length); Assert.Equal(2001, page.Lines[0].Id); Assert.Equal(2000, page.State.DroppedLines);
        Assert.Empty(session.Read(12000, 10000).Lines);
        for (int i = 0; i < 600; i++) session.Add(new LogReadItem(new string('x', LogLimits.LineBytes + 100)));
        page = session.Read(0, 10000);
        Assert.True(page.State.DroppedLines > 12000);
        Assert.True(page.Lines.Sum(l => Encoding.UTF8.GetByteCount(l.Text)) <= 256 * 1024);
        Assert.All(page.Lines, line => Assert.True(line.Truncated));
    }

    [Theory]
    [InlineData("denied", "Access denied")]
    [InlineData("missing", "Source removed")]
    [InlineData("io", "Disconnected")]
    public async Task Reader_errors_are_reported_without_exposing_internal_exception_details(string error, string status)
    {
        var provider = new Provider { Failure = error == "denied" ? new UnauthorizedAccessException("secret") :
            error == "missing" ? new FileNotFoundException("secret") : new IOException("secret") };
        using var hub = new LogStreamHub(provider); using var view = hub.Subscribe("source")!;
        await provider.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await view.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status, view.Read(0, 100).State.Status);
    }

    [Fact]
    public async Task Bounded_line_parser_preserves_unicode_timestamps_and_strips_terminal_sequences()
    {
        var content = "2026-09-09T12:00:00Z \u001b[31mhello\u001b[0m\r\n" + string.Concat(Enumerable.Repeat("🍋", 6000)) + "\nlast";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var lines = new List<LogReadItem>();
        await foreach (var item in LogText.ReadLinesAsync(stream, default)) lines.Add(item);
        Assert.Equal(3, lines.Count); Assert.Equal("hello", lines[0].Text); Assert.NotNull(lines[0].TimestampMs);
        Assert.True(lines[1].Truncated); Assert.DoesNotContain("�", lines[1].Text);
        Assert.True(Encoding.UTF8.GetByteCount(lines[1].Text) <= LogLimits.LineBytes);
        Assert.Equal("last", lines[2].Text);
        Assert.Equal("link", LogText.Normalize("\u001b]8;;https://invalid\a" + "link\u001b]8;;\a").Text);
    }
}
