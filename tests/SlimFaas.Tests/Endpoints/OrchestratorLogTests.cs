using System.Buffers.Binary;
using System.Net;
using System.Text;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Logs;

namespace SlimFaas.Tests.Endpoints;

public sealed class OrchestratorLogTests
{
    [Theory]
    [InlineData("Source removed")]
    [InlineData("Access denied")]
    [InlineData("Disconnected")]
    public async Task Native_control_channel_preserves_terminal_states_without_displaying_them_as_lines(string status)
    {
        var source = LogSourceIds.Create(new(new("function", "f", "f-0"), "f-0", "process", "generation"));
        var handler = new Handler(request =>
        {
            Assert.Equal("test-token", Assert.Single(request.Headers.GetValues(ProcessKubernetesService.TokenHeaderName)));
            return request.RequestUri!.AbsolutePath.EndsWith("log-sources")
                ? Json(System.Text.Json.JsonSerializer.Serialize(new LogSources("Available", [source]), LogJsonContext.Default.LogSources))
                : Json(System.Text.Json.JsonSerializer.Serialize(new LogReadItem("", Status: status), LogJsonContext.Default.LogReadItem) + "\n");
        });
        using var http = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(f => f.CreateClient(ProcessKubernetesService.HttpClientName)).Returns(http);
        var settings = new SlimFaas.Options.SlimFaasOptions();
        settings.Process.SupervisorUrl = "http://localhost"; settings.Process.Token = "test-token";
        var provider = new ProcessKubernetesService(factory.Object, Microsoft.Extensions.Options.Options.Create(settings));
        await using var reader = provider.ReadAsync(source.Id, default).GetAsyncEnumerator();
        if (status == "Source removed") await Assert.ThrowsAsync<FileNotFoundException>(() => reader.MoveNextAsync().AsTask());
        else if (status == "Access denied") await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.MoveNextAsync().AsTask());
        else await Assert.ThrowsAsync<IOException>(() => reader.MoveNextAsync().AsTask());
    }

    private static (IReplicasService Replicas, IJobService Jobs, INamespaceProvider Ns) Inventory()
    {
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(r => r.Deployments).Returns(new DeploymentsInformations(
            [new("f", "test", [new("f-0", true, true, "10.0.0.1", "f")], new(), 1),
             new("ws", "websocket-virtual", [new("ws-0", true, true, "", "ws")], new(), 1)],
            new(1, [new("slimfaas-0", true, true, "10.0.0.2", "slimfaas")]), []));
        var jobs = new Mock<IJobService>();
        jobs.SetupGet(j => j.Jobs).Returns([new("job-slimfaas-job-123", JobStatus.Succeeded, [], [], "element", 1, 2)]);
        var ns = new Mock<INamespaceProvider>(); ns.SetupGet(n => n.CurrentNamespace).Returns("test");
        return (replicas.Object, jobs.Object, ns.Object);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : DelegatingHandler
    {
        internal readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!.PathAndQuery); var result = response(request); result.RequestMessage = request; return Task.FromResult(result); }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Pod(string name, string ownerKind, string ownerName, string ownerUid, string generation = "pod-uid") => $$$$"""
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"{{{{name}}}}","uid":"{{{{generation}}}}","ownerReferences":[{"kind":"{{{{ownerKind}}}}","name":"{{{{ownerName}}}}","uid":"{{{{ownerUid}}}}","controller":true}]},"spec":{"containers":[{"name":"app","image":"image"},{"name":"sidecar","image":"image"}]},"status":{"containerStatuses":[{"name":"app","restartCount":0},{"name":"sidecar","restartCount":0}]}}
        """;

    [Theory]
    [InlineData("function", "f", "f-0")]
    [InlineData("slimfaas", "slimfaas", "slimfaas-0")]
    [InlineData("job", "job", "job-slimfaas-job-123")]
    public async Task Kubernetes_sources_follow_controller_ownership_and_stream_the_selected_container(string kind, string name, string replica)
    {
        var inventory = Inventory();
        var handler = new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/log")) return new(HttpStatusCode.OK) { Content = new StringContent("2026-09-09T12:00:00Z application line\n", Encoding.UTF8, "text/plain") };
            if (path.EndsWith("/pods/f-0")) return Json(Pod("f-0", "ReplicaSet", "f-rs", "rs-uid"));
            if (path.EndsWith("/replicasets/f-rs")) return Json("""{"metadata":{"uid":"rs-uid","ownerReferences":[{"kind":"Deployment","name":"f","uid":"f-uid","controller":true}]}}""");
            if (path.EndsWith("/deployments/f")) return Json("""{"metadata":{"uid":"f-uid"}}""");
            if (path.EndsWith("/pods/slimfaas-0")) return Json(Pod("slimfaas-0", "StatefulSet", "slimfaas", "slimfaas-uid"));
            if (path.EndsWith("/statefulsets/slimfaas")) return Json("""{"metadata":{"uid":"slimfaas-uid"}}""");
            if (path.EndsWith("/jobs/job-slimfaas-job-123")) return Json("""{"metadata":{"uid":"job-uid"}}""");
            if (path.EndsWith("/pods")) return Json("{\"items\":[" + Pod("job-pod", "Job", replica, "job-uid") + "]}");
            return Json("{}", HttpStatusCode.NotFound);
        });
        using var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost" }, handler);
        var provider = new KubernetesInstanceLogs(client, inventory.Replicas, inventory.Jobs, inventory.Ns);
        var sources = await provider.GetSourcesAsync(new(kind, name, replica), default);
        Assert.Equal(2, sources.Sources.Count);
        var source = sources.Sources.Single(s => s.Container == "sidecar");
        var lines = new List<LogReadItem>();
        await foreach (var line in provider.ReadAsync(source.Id, default)) lines.Add(line);
        Assert.Equal("application line", Assert.Single(lines).Text);
        Assert.Contains(handler.Requests, p => p.Contains("container=sidecar") && p.Contains("follow=true") && p.Contains("tailLines=10000"));
        Assert.All(handler.Requests, path => Assert.Contains("/namespaces/test/", path));
        int requests = handler.Requests.Count;
        Assert.Empty((await provider.GetSourcesAsync(new("function", "ws", "ws-0"), default)).Sources);
        Assert.Empty((await provider.GetSourcesAsync(new("function", "f", "unmanaged"), default)).Sources);
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Kubernetes_missing_resources_and_rbac_denials_have_explicit_states(HttpStatusCode status)
    {
        var inventory = Inventory();
        using var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost" },
            new Handler(_ => Json("{}", status)));
        var provider = new KubernetesInstanceLogs(client, inventory.Replicas, inventory.Jobs, inventory.Ns);
        if (status == HttpStatusCode.Forbidden)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.GetSourcesAsync(new("function", "f", "f-0"), default));
        else Assert.Empty((await provider.GetSourcesAsync(new("function", "f", "f-0"), default)).Sources);
    }

    [Fact]
    public async Task Kubernetes_rejects_a_pod_whose_controller_belongs_to_another_deployment()
    {
        var inventory = Inventory();
        using var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost" },
            new Handler(_ => Json(Pod("f-0", "Deployment", "private-app", "private-uid"))));
        var provider = new KubernetesInstanceLogs(client, inventory.Replicas, inventory.Jobs, inventory.Ns);
        Assert.Empty((await provider.GetSourcesAsync(new("function", "f", "f-0"), default)).Sources);
    }

    [Theory]
    [InlineData(true, "function", "f", "f-0")]
    [InlineData(false, "function", "f", "f-0")]
    [InlineData(false, "job", "job", "job-slimfaas-job-123")]
    [InlineData(false, "slimfaas", "slimfaas", "slimfaas-0")]
    public async Task Docker_logs_resolve_known_instances_and_support_tty_and_multiplexed_output(bool tty, string kind, string name, string replica)
    {
        var inventory = Inventory();
        var handler = new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/version") return Json("""{"ApiVersion":"1.43"}""");
            if (path.EndsWith("/containers/json")) return Json("[]");
            if (path.EndsWith($"/containers/{replica}/json")) return Json($$$$"""
                {"Id":"container-id","Name":"/{{{{replica}}}}","Image":"test","Config":{"Tty":{{{{tty.ToString().ToLowerInvariant()}}}},"Labels":{},"ExposedPorts":{}},"State":{"Running":false,"ExitCode":0,"StartedAt":"2026-09-09T12:00:00Z"},"NetworkSettings":{"Networks":{}}}
                """);
            if (path.EndsWith("/logs")) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(tty ? "hello\n"u8.ToArray() : Frame(1, "hello\n"u8.ToArray())) };
            return Json("{}", HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:2375") };
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(f => f.CreateClient(DockerService.HttpClientName)).Returns(http);
        var docker = new DockerService(factory.Object, NullLogger<DockerService>.Instance);
        var provider = new DockerInstanceLogs(docker, inventory.Replicas, inventory.Jobs);
        var source = Assert.Single((await provider.GetSourcesAsync(new(kind, name, replica), default)).Sources);
        var lines = new List<LogReadItem>();
        await foreach (var line in provider.ReadAsync(source.Id, default)) lines.Add(line);
        Assert.Equal("hello", Assert.Single(lines).Text);
        Assert.Empty((await provider.GetSourcesAsync(new("function", "ws", "ws-0"), default)).Sources);
        var forged = LogSourceIds.Create(LogSourceIds.Parse(source.Id) with { Generation = "old" });
        await using var reader = provider.ReadAsync(forged.Id, default).GetAsyncEnumerator();
        await Assert.ThrowsAsync<FileNotFoundException>(() => reader.MoveNextAsync().AsTask());
    }

    internal static byte[] Frame(byte kind, byte[] data)
    {
        var frame = new byte[data.Length + 8]; frame[0] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)data.Length);
        data.CopyTo(frame, 8); return frame;
    }

    [Fact]
    public async Task Docker_demultiplexing_preserves_split_utf8_and_keeps_partial_stdout_separate_from_stderr()
    {
        var emoji = Encoding.UTF8.GetBytes("🍋");
        var bytes = Frame(1, "hello "u8.ToArray()).Concat(Frame(1, emoji[..2]))
            .Concat(Frame(2, "error\n"u8.ToArray())).Concat(Frame(1, emoji[2..].Concat("\n"u8.ToArray()).ToArray()))
            .Concat(Frame(2, "partial"u8.ToArray())).ToArray();
        using var stream = new MemoryStream(bytes);
        var lines = new List<LogReadItem>();
        await foreach (var line in DockerLogReader.ReadAsync(stream, default)) lines.Add(line);
        Assert.Equal(["error", "hello 🍋", "partial"], lines.Select(l => l.Text));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_or_incomplete_docker_frames_fail_without_allocating_the_declared_size(bool invalid)
    {
        var bytes = invalid ? Frame(9, []) : Frame(1, "hello"u8.ToArray())[..9];
        using var stream = new MemoryStream(bytes);
        await using var reader = DockerLogReader.ReadAsync(stream, default).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<IOException>(() => reader.MoveNextAsync().AsTask());
    }
}
