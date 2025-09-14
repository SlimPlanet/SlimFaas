using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes
{
    public class DockerServiceTests
    {
        // === utilitaires ===


        private static HttpClient MakeHttpClient(FakeDockerHandler handler, out Uri baseAddress)
        {
            // Le constructeur de DockerService invoque CreateHttpClientForDocker(...)
            // avec "dockerHost" null et, sur Windows, basculera sur http://127.0.0.1:2375
            // Ici on cale la BaseAddress sur http://localhost:2375
            baseAddress = new Uri("http://localhost:2375");
            var client = new HttpClient(handler) { BaseAddress = baseAddress };
            return client;
        }

        private static DockerService MakeService(FakeDockerHandler handler)
        {
            var baseAddress = new Uri("http://localhost:2375");

            var client = new HttpClient(handler)
            {
                BaseAddress = baseAddress
            };

            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(DockerService.HttpClientName)).Returns(client);

            var logger = new Mock<ILogger<DockerService>>().Object;

            return new DockerService(mockFactory.Object, logger);
        }


        private static string UrlEncode(string s) => WebUtility.UrlEncode(s);

        // === Tests ===

        [Fact]
public async Task ListJobsAsync_FiltersByNamespace_MapsStatuses_AndPurgesFinishedPastTTL()
{
    var ns = "myns";
    var now = DateTimeOffset.UtcNow;

    var cRun  = new ContainerSummary("id-run",  new(){"/job-run"},  "img","running","Up 1s",
        new(){ ["slimfaas-job-name"]="job-run",  ["slimfaas/namespace"]=ns });
    var cOk   = new ContainerSummary("id-ok",   new(){"/job-ok"},   "img","exited","Exited (0)",
        new(){ ["slimfaas-job-name"]="job-ok",   ["slimfaas/namespace"]=ns });
    var cPull = new ContainerSummary("id-pull", new(){"/job-pull"}, "img","exited","Exited (1)",
        new(){ ["slimfaas-job-name"]="job-pull", ["slimfaas/namespace"]=ns });
    var cTtl  = new ContainerSummary("id-ttl",  new(){"/job-ttl"},  "img","exited","Exited (0)",
        new(){ ["slimfaas-job-name"]="job-ttl",  ["slimfaas/namespace"]=ns, ["SlimFaas/TtlSecondsAfterFinished"]="5" });

    var listForNs = new List<ContainerSummary> { cRun, cOk, cPull, cTtl };

    var handler = new FakeDockerHandler();

    // /version (appelé au ctor)
    handler.WhenGET("/version").RespondJson(new DockerVersionResponse("1.43", "25.0.0"));

    // ⚠️ D’ABORD la route "list json" ciblée...
    handler.WhenGETStartsWith("/v1.43/containers/json").RespondJson(listForNs);
    // ...PUIS (optionnel) un 404 ciblé pour l’auto‑inspect "self"
    handler.WhenGETStartsWith("/v1.43/containers/_self_").RespondStatus(HttpStatusCode.NotFound);
    // (ou supprime totalement l’ancienne règle large "/containers/" qui catch‑all)

    // Inspect RUNNING
    handler.WhenGET("/containers/id-run/json").RespondJson(
        new InspectContainerResponse("id-run", "/job-run", "img", now,
            new Inspect_Config("img", Array.Empty<string>(), Array.Empty<string>(),
                new(){ ["slimfaas-job-name"]="job-run", ["slimfaas/namespace"]=ns }, new()),
            new Inspect_State(true, 0, null, null, now, null),
            new Inspect_NetworkSettings(null,
                new(){ ["slimfaas-net"]=new Inspect_EndpointSettings("10.0.0.10") }, new()))
    );

    // Inspect OK
    handler.WhenGET("/containers/id-ok/json").RespondJson(
        new InspectContainerResponse("id-ok", "/job-ok", "img", now.AddMinutes(-1),
            new Inspect_Config("img", Array.Empty<string>(), Array.Empty<string>(),
                new(){ ["slimfaas-job-name"]="job-ok", ["slimfaas/namespace"]=ns }, new()),
            new Inspect_State(false, 0, null, null, now.AddSeconds(-10), now.AddSeconds(-5)),
            new Inspect_NetworkSettings(null, new(), new()))
    );

    // Inspect PULL error -> ImagePullBackOff
    handler.WhenGET("/containers/id-pull/json").RespondJson(
        new InspectContainerResponse("id-pull", "/job-pull", "img", now.AddMinutes(-1),
            new Inspect_Config("img", Array.Empty<string>(), Array.Empty<string>(),
                new(){ ["slimfaas-job-name"]="job-pull", ["slimfaas/namespace"]=ns }, new()),
            new Inspect_State(false, 1, "pull failed: not found", null, now.AddSeconds(-10), now.AddSeconds(-8)),
            new Inspect_NetworkSettings(null, new(), new()))
    );

    // Inspect TTL
    handler.WhenGET("/containers/id-ttl/json").RespondJson(
        new InspectContainerResponse("id-ttl", "/job-ttl", "img", now.AddMinutes(-2),
            new Inspect_Config("img", Array.Empty<string>(), Array.Empty<string>(),
                new(){
                    ["slimfaas-job-name"]="job-ttl",
                    ["slimfaas/namespace"]=ns,
                    ["SlimFaas/TtlSecondsAfterFinished"]="5"
                }, new()),
            new Inspect_State(false, 0, null, null, now.AddMinutes(-1), now.AddMinutes(-1)),
            new Inspect_NetworkSettings(null, new(), new()))
    );

    // suppression du TTL
    handler.WhenDELETE("/containers/id-ttl?force=true").RespondNoContent();

    var svc = MakeService(handler);

    // Act
    var jobs = await svc.ListJobsAsync(ns);

    // Assert
    Assert.Equal(3, jobs.Count);

    var jRun = jobs.Single(x => x.Name.Contains("job-run", StringComparison.OrdinalIgnoreCase));
    Assert.Equal(JobStatus.Running, jRun.Status);
    Assert.Contains("10.0.0.10", jRun.Ips);

    var jOk = jobs.Single(x => x.Name.Contains("job-ok", StringComparison.OrdinalIgnoreCase));
    Assert.Equal(JobStatus.Succeeded, jOk.Status);

    var jPull = jobs.Single(x => x.Name.Contains("job-pull", StringComparison.OrdinalIgnoreCase));
    Assert.Equal(JobStatus.ImagePullBackOff, jPull.Status);

    Assert.True(handler.WasCalled(HttpMethod.Delete, "/v1.43/containers/id-ttl?force=true")
             || handler.WasCalled(HttpMethod.Delete, "/containers/id-ttl?force=true"));
}


        [Fact]
        public async Task DeleteJobAsync_ResolvesByName_StopsAndRemoves()
        {
            var ns = "myns";
            var jobName = "fibonacci-slimfaas-job-xyz";

            var handler = new FakeDockerHandler();

            // ctor: TryGetApiVersionAsync appelle "/version" (sans préfixe)
            handler.WhenGET("/version").RespondJson(new DockerVersionResponse("1.43", "25.0.0"));

            // IMPORTANT: enregistrer d'abord la route "list" ciblée
            handler.WhenGETStartsWith("/v1.43/containers/json").RespondJson(new List<ContainerSummary> {
                new ContainerSummary("abc123",
                    new List<string>{"/fibonacci-slimfaas-job-xyz"},
                    "img", "running", "Up", null)
            });

            // (optionnel) si tu veux simuler un 404 sur l'inspect "self",
            // fais-le de manière ciblée plutôt que large:
            handler.WhenGETStartsWith("/v1.43/containers/").RespondStatus(HttpStatusCode.NotFound);

            // stop + delete exacts
            handler.WhenPOST("/v1.43/containers/abc123/stop").RespondNoContent();
            handler.WhenDELETE("/v1.43/containers/abc123?force=true").RespondNoContent();

            var svc = MakeService(handler);

            // Act
            await svc.DeleteJobAsync(ns, jobName);

            // Assert
            Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/containers/abc123/stop")
                        || handler.WasCalled(HttpMethod.Post, "/containers/abc123/stop"));
            Assert.True(handler.WasCalled(HttpMethod.Delete, "/v1.43/containers/abc123?force=true")
                        || handler.WasCalled(HttpMethod.Delete, "/containers/abc123?force=true"));
        }


        [Fact]
        public async Task CreateJobAsync_PullsWhenMissing_CreatesAndStarts_WithLabels()
        {
            // Arrange
            var ns = "jobs-ns";
            var name = "fibo";
            var jobFullName = "fibo-slimfaas-job-123";
            var elementId = "elt-42";
            var inQueue = DateTimeOffset.UtcNow.Ticks;

            var handler = new FakeDockerHandler();

            // ctor
            handler.WhenGET("/version").RespondJson(new DockerVersionResponse("1.43", "25.0.0"));
            handler.WhenGETStartsWith("/containers/").RespondStatus(HttpStatusCode.NotFound);


// 1) Inspect avant pull -> 404 (chemin ENCODÉ + avec /v1.43)
            handler.WhenGET("/v1.43/images/python%3A3.11/json").RespondNotFoundOnce();

// 2) Pull -> 200 (la query doit matcher)
            handler.WhenPOST("/v1.43/images/create?fromImage=python&tag=3.11").RespondText(200, "{}");

// 3) Re-inspect après pull -> 200
            handler.WhenGET("/v1.43/images/python%3A3.11/json")
                .RespondJson(new InspectImageResponse(
                    "imgid",
                    new InspectImage_Config(Array.Empty<string>(), null),
                    new InspectImage_Config(Array.Empty<string>(), null)));

            // TryPullImageAsync (appelée aussi)
            handler.WhenPOST("/v1.43/images/create?fromImage=python&tag=3.11").RespondText(200, "{}");

            // containers/create
            handler.WhenPOST("/containers/create?name=" + jobFullName)
                   .RespondJson(new CreateContainerResponse("cid-999"));

            // start
            handler.WhenPOST("/containers/cid-999/start").RespondNoContent();

            // inspect post-start (logging)
            handler.WhenGET("/containers/cid-999/json")
                .RespondJson(new InspectContainerResponse(
                    Id: "cid-999",
                    Name: "/" + jobFullName,
                    Image: "python:3.11",
                    Created: DateTimeOffset.UtcNow,
                    Config: new Inspect_Config("python:3.11",
                        new []{ "A=B" },
                        Array.Empty<string>(),
                        new Dictionary<string, string>{
                            ["slimfaas-job-name"] = jobFullName,
                            ["slimfaas-job-element-id"] = elementId,
                            ["slimfaas/namespace"] = ns
                        },
                        new Dictionary<string, object>()),
                    State: new Inspect_State(true, 0, null, null, DateTimeOffset.UtcNow, null),
                    NetworkSettings: new Inspect_NetworkSettings(null, null, null)
                ));

            var svc = MakeService(handler);

            CreateJob createJob = new(["python", "-c", "print(1)"],
                "python:3.11",
                1,
                0,
                "Never",
                null,
                [new EnvVarInput("A", "B")]);

            // Act
            await svc.CreateJobAsync(ns, name, createJob, elementId, jobFullName, inQueue);

            // Assert: endpoints critiques appelés
            Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/images/create?fromImage=python&tag=3.11")
                      || handler.WasCalled(HttpMethod.Post, "/images/create?fromImage=python&tag=3.11"));
            Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/containers/create?name=" + jobFullName)
                      || handler.WasCalled(HttpMethod.Post, "/containers/create?name=" + jobFullName));
            Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/containers/cid-999/start")
                      || handler.WasCalled(HttpMethod.Post, "/containers/cid-999/start"));

            // Vérifie que le body de création contient nos labels clés
            var lastCreateBody = handler.LastJsonBody<CreateContainerRequest>("/containers/create?name=" + jobFullName);
            Assert.NotNull(lastCreateBody);
            Assert.Equal("python:3.11", lastCreateBody!.Image);
            Assert.Equal(jobFullName, lastCreateBody.Name);
            Assert.Contains(lastCreateBody.Labels, kv => kv.Key == "slimfaas-job-name" && kv.Value == jobFullName);
            Assert.Contains(lastCreateBody.Labels, kv => kv.Key == "slimfaas-job-element-id" && kv.Value == elementId);
            Assert.Contains(lastCreateBody.Labels, kv => kv.Key == "slimfaas/namespace" && kv.Value == ns);
        }
    }

    // ============================================================
    //         FAKE DOCKER HTTP HANDLER (routes in-memory)
    // ============================================================

    internal class FakeDockerHandler : HttpMessageHandler
    {
        private readonly List<(Predicate<HttpRequestMessage> match,
            Func<HttpRequestMessage, HttpResponseMessage> resp,
            bool once)> _routes = new();

        private readonly List<(HttpMethod method, string path)> _calls = new();

        public RouteBuilder WhenGET(string path) => new(this, HttpMethod.Get, path, exact: true);
        public RouteBuilder WhenGETStartsWith(string prefix) => new(this, HttpMethod.Get, prefix, exact: false);
        public RouteBuilder WhenPOST(string path) => new(this, HttpMethod.Post, path, exact: true);
        public RouteBuilder WhenDELETE(string path) => new(this, HttpMethod.Delete, path, exact: true);

        public bool WasCalled(HttpMethod m, string pathOrSuffix)
        {
            return _calls.Any(c =>
                c.method == m &&
                (c.path.EndsWith(pathOrSuffix, StringComparison.Ordinal) ||
                 Normalize(c.path) == Normalize(pathOrSuffix)));
        }
        public bool WasCalledContains(HttpMethod m, string fragment) =>
            _calls.Any(c => c.method == m && c.path.Contains(fragment, StringComparison.Ordinal));
        public T? LastJsonBody<T>(string pathSuffix)
        {
            var rec = _lastBodies.LastOrDefault(x => x.path.EndsWith(pathSuffix, StringComparison.Ordinal));
            if (rec.path == null) return default;
            return JsonSerializer.Deserialize<T>(rec.body);
        }

        private static string Normalize(string p) => p.Replace("/v1.43", "", StringComparison.Ordinal);

        internal readonly List<(string path, string body)> _lastBodies = new();

        public class RouteBuilder
        {
            private readonly FakeDockerHandler _h;
            private readonly HttpMethod _m;
            private readonly string _p;
            private readonly bool _exact;

            public RouteBuilder(FakeDockerHandler h, HttpMethod m, string p, bool exact)
            { _h = h; _m = m; _p = p; _exact = exact; }

            public void RespondJson<T>(T obj, int status = 200)
                => _h._routes.Add((Match, _ => MakeJson(obj, status), false));

            public void RespondJsonOnce<T>(T obj, int status = 200)
                => _h._routes.Add((Match, _ => MakeJson(obj, status), true)); // 👈 one-shot

            public void RespondText(int status, string text)
                => _h._routes.Add((Match, _ => MakeText(status, text), false));

            public void RespondNoContent()
                => _h._routes.Add((Match, _ => new HttpResponseMessage(HttpStatusCode.NoContent), false));

            public void RespondStatus(HttpStatusCode code)
                => _h._routes.Add((Match, _ => new HttpResponseMessage(code), false));

            public void RespondNotFoundOnce()
                => _h._routes.Add((Match, _ => new HttpResponseMessage(HttpStatusCode.NotFound), true)); // 👈 one-shot

            private bool Match(HttpRequestMessage req)
            {
                if (req.Method != _m) return false;
                var full = req.RequestUri!.AbsolutePath + req.RequestUri!.Query; // 👈 inclut la query
                if (_exact)
                    return full.EndsWith(_p, StringComparison.Ordinal)
                           || full.Equals(_p, StringComparison.Ordinal)
                           || full.EndsWith("/v1.43" + _p, StringComparison.Ordinal);
                return full.Contains(_p, StringComparison.Ordinal);
            }

            private static HttpResponseMessage MakeJson(object obj, int status)
            {
                var msg = new HttpResponseMessage((HttpStatusCode)status);
                msg.Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
                return msg;
            }

            private static HttpResponseMessage MakeText(int status, string text)
            {
                var msg = new HttpResponseMessage((HttpStatusCode)status);
                msg.Content = new StringContent(text, Encoding.UTF8, "application/json");
                return msg;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var full = request.RequestUri!.AbsolutePath + request.RequestUri!.Query;
            _calls.Add((request.Method, full));

            if (request.Content != null && request.Method == HttpMethod.Post)
            {
                var body = await request.Content.ReadAsStringAsync();
                _lastBodies.Add((full, body));
            }

            for (int i = 0; i < _routes.Count; i++)
            {
                var (match, resp, once) = _routes[i];
                if (match(request))
                {
                    var result = resp(request);
                    if (once) _routes.RemoveAt(i);              // 👈 consomme la route “once”
                    return result;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fake route for {request.Method} {full}")
            };
        }
    }
}
