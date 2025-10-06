using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public class DockerServiceTests
{
    // === utilitaires ===


    private static HttpClient MakeHttpClient(FakeDockerHandler handler, out Uri baseAddress)
    {
        // Le constructeur de DockerService invoque CreateHttpClientForDocker(...)
        // avec "dockerHost" null et, sur Windows, basculera sur http://127.0.0.1:2375
        // Ici on cale la BaseAddress sur http://localhost:2375
        baseAddress = new Uri("http://localhost:2375");
        HttpClient client = new(handler) { BaseAddress = baseAddress };
        return client;
    }

    private static DockerService MakeService(FakeDockerHandler handler)
    {
        Uri baseAddress = new("http://localhost:2375");

        HttpClient client = new(handler) { BaseAddress = baseAddress };

        Mock<IHttpClientFactory> mockFactory = new();
        mockFactory.Setup(f => f.CreateClient(DockerService.HttpClientName)).Returns(client);

        ILogger<DockerService> logger = new Mock<ILogger<DockerService>>().Object;

        return new DockerService(mockFactory.Object, logger);
    }


    private static string UrlEncode(string s) => WebUtility.UrlEncode(s);

    // === Tests ===

    [Fact]
    public async Task ListJobsAsync_FiltersByNamespace_MapsStatuses_AndPurgesFinishedPastTTL()
    {
        string ns = "myns";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ContainerSummary cRun = new("id-run", new List<string> { "/job-run" }, "img", "running", "Up 1s",
            new Dictionary<string, string> { ["slimfaas-job-name"] = "job-run", ["slimfaas/namespace"] = ns });
        ContainerSummary cOk = new("id-ok", new List<string> { "/job-ok" }, "img", "exited", "Exited (0)",
            new Dictionary<string, string> { ["slimfaas-job-name"] = "job-ok", ["slimfaas/namespace"] = ns });
        ContainerSummary cPull = new("id-pull", new List<string> { "/job-pull" }, "img", "exited", "Exited (1)",
            new Dictionary<string, string> { ["slimfaas-job-name"] = "job-pull", ["slimfaas/namespace"] = ns });
        ContainerSummary cTtl = new("id-ttl", new List<string> { "/job-ttl" }, "img", "exited", "Exited (0)",
            new Dictionary<string, string>
            {
                ["slimfaas-job-name"] = "job-ttl",
                ["slimfaas/namespace"] = ns,
                ["SlimFaas/TtlSecondsAfterFinished"] = "5"
            });

        List<ContainerSummary> listForNs = new() { cRun, cOk, cPull, cTtl };

        FakeDockerHandler handler = new();

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
                    new Dictionary<string, string> { ["slimfaas-job-name"] = "job-run", ["slimfaas/namespace"] = ns },
                    new Dictionary<string, object>()),
                new Inspect_State(true, 0, null, null, now, null),
                new Inspect_NetworkSettings(null,
                    new Dictionary<string, Inspect_EndpointSettings> { ["slimfaas-net"] = new("10.0.0.10") },
                    new Dictionary<string, List<Inspect_PortBinding>?>()))
        );

        // Inspect OK
        handler.WhenGET("/containers/id-ok/json").RespondJson(
            new InspectContainerResponse("id-ok", "/job-ok", "img", now.AddMinutes(-1),
                new Inspect_Config("img", Array.Empty<string>(), Array.Empty<string>(),
                    new Dictionary<string, string> { ["slimfaas-job-name"] = "job-ok", ["slimfaas/namespace"] = ns },
                    new Dictionary<string, object>()),
                new Inspect_State(false, 0, null, null, now.AddSeconds(-10), now.AddSeconds(-5)),
                new Inspect_NetworkSettings(null, new Dictionary<string, Inspect_EndpointSettings>(),
                    new Dictionary<string, List<Inspect_PortBinding>?>()))
        );

        // Inspect PULL error -> ImagePullBackOff
        handler.WhenGET("/containers/id-pull/json").RespondJson(
            new InspectContainerResponse("id-pull", "/job-pull", "img", now.AddMinutes(-1),
                new Inspect_Config("img", Array.Empty<string>(), Array.Empty<string>(),
                    new Dictionary<string, string> { ["slimfaas-job-name"] = "job-pull", ["slimfaas/namespace"] = ns },
                    new Dictionary<string, object>()),
                new Inspect_State(false, 1, "pull failed: not found", null, now.AddSeconds(-10), now.AddSeconds(-8)),
                new Inspect_NetworkSettings(null, new Dictionary<string, Inspect_EndpointSettings>(),
                    new Dictionary<string, List<Inspect_PortBinding>?>()))
        );

        // Inspect TTL
        handler.WhenGET("/containers/id-ttl/json").RespondJson(
            new InspectContainerResponse("id-ttl", "/job-ttl", "img", now.AddMinutes(-2),
                new Inspect_Config("img", Array.Empty<string>(), Array.Empty<string>(),
                    new Dictionary<string, string>
                    {
                        ["slimfaas-job-name"] = "job-ttl",
                        ["slimfaas/namespace"] = ns,
                        ["SlimFaas/TtlSecondsAfterFinished"] = "5"
                    }, new Dictionary<string, object>()),
                new Inspect_State(false, 0, null, null, now.AddMinutes(-1), now.AddMinutes(-1)),
                new Inspect_NetworkSettings(null, new Dictionary<string, Inspect_EndpointSettings>(),
                    new Dictionary<string, List<Inspect_PortBinding>?>()))
        );

        // suppression du TTL
        handler.WhenDELETE("/containers/id-ttl?force=true").RespondNoContent();

        DockerService svc = MakeService(handler);

        // Act
        IList<Job> jobs = await svc.ListJobsAsync(ns);

        // Assert
        Assert.Equal(3, jobs.Count);

        Job jRun = jobs.Single(x => x.Name.Contains("job-run", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(JobStatus.Running, jRun.Status);
        Assert.Contains("10.0.0.10", jRun.Ips);

        Job jOk = jobs.Single(x => x.Name.Contains("job-ok", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(JobStatus.Succeeded, jOk.Status);

        Job jPull = jobs.Single(x => x.Name.Contains("job-pull", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(JobStatus.ImagePullBackOff, jPull.Status);

        Assert.True(handler.WasCalled(HttpMethod.Delete, "/v1.43/containers/id-ttl?force=true")
                    || handler.WasCalled(HttpMethod.Delete, "/containers/id-ttl?force=true"));
    }


    [Fact]
    public async Task DeleteJobAsync_ResolvesByName_StopsAndRemoves()
    {
        string ns = "myns";
        string jobName = "fibonacci-slimfaas-job-xyz";

        FakeDockerHandler handler = new();

        // ctor: TryGetApiVersionAsync appelle "/version" (sans préfixe)
        handler.WhenGET("/version").RespondJson(new DockerVersionResponse("1.43", "25.0.0"));

        // IMPORTANT: enregistrer d'abord la route "list" ciblée
        handler.WhenGETStartsWith("/v1.43/containers/json").RespondJson(new List<ContainerSummary>
        {
            new("abc123",
                new List<string> { "/fibonacci-slimfaas-job-xyz" },
                "img", "running", "Up", null)
        });

        // (optionnel) si tu veux simuler un 404 sur l'inspect "self",
        // fais-le de manière ciblée plutôt que large:
        handler.WhenGETStartsWith("/v1.43/containers/").RespondStatus(HttpStatusCode.NotFound);

        // stop + delete exacts
        handler.WhenPOST("/v1.43/containers/abc123/stop").RespondNoContent();
        handler.WhenDELETE("/v1.43/containers/abc123?force=true").RespondNoContent();

        DockerService svc = MakeService(handler);

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
        string ns = "jobs-ns";
        string name = "fibo";
        string jobFullName = "fibo-slimfaas-job-123";
        string elementId = "elt-42";
        long inQueue = DateTimeOffset.UtcNow.Ticks;

        FakeDockerHandler handler = new();

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
                "cid-999",
                "/" + jobFullName,
                "python:3.11",
                DateTimeOffset.UtcNow,
                new Inspect_Config("python:3.11",
                    new[] { "A=B" },
                    Array.Empty<string>(),
                    new Dictionary<string, string>
                    {
                        ["slimfaas-job-name"] = jobFullName,
                        ["slimfaas-job-element-id"] = elementId,
                        ["SlimFaas/Namespace"] = ns
                    },
                    new Dictionary<string, object>()),
                new Inspect_State(true, 0, null, null, DateTimeOffset.UtcNow, null),
                new Inspect_NetworkSettings(null, null, null)
            ));

        DockerService svc = MakeService(handler);

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
        CreateContainerRequest? lastCreateBody =
            handler.LastJsonBody<CreateContainerRequest>("/containers/create?name=" + jobFullName);
        Assert.NotNull(lastCreateBody);
        Assert.Equal("python:3.11", lastCreateBody!.Image);
        Assert.Equal(jobFullName, lastCreateBody.Name);
        Assert.Contains(lastCreateBody.Labels, kv => kv.Key == "slimfaas-job-name" && kv.Value == jobFullName);
        Assert.Contains(lastCreateBody.Labels, kv => kv.Key == "slimfaas-job-element-id" && kv.Value == elementId);
        Assert.Contains(lastCreateBody.Labels, kv => kv.Key == "SlimFaas/Namespace" && kv.Value == ns);
    }


    [Fact]
    public async Task ScaleAsync_ScaleUp_UsesTemplate_AndCreatesStartsDesiredReplicas()
    {
        // Arrange
        string ns = "ns1";
        string app = "myapp";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        FakeDockerHandler handler = new();

        // ctor
        handler.WhenGET("/version").RespondJson(new DockerVersionResponse("1.43", "25.0.0"));

        // (optionnel) l’auto-inspect du conteneur "self" peut exister → 404 neutre
        handler.WhenGETStartsWith("/v1.43/containers/_self_").RespondStatus(HttpStatusCode.NotFound);

        // 1) LIST #1 (function=true, app=myapp, ns=ns1) → 0 running (current=0)
        handler.WhenGETStartsWith("/v1.43/containers/json")
            .RespondJsonOnce(new List<ContainerSummary>());

        // 2) LIST #2 → 1 template
        var upTemplateList = new List<ContainerSummary>
        {
            new(
                "tpl-1",
                new List<string> { "/myapp-template" },
                "repo:tag",
                "exited",
                "Exited",
                new Dictionary<string, string>
                {
                    ["app"] = app,
                    ["SlimFaas/Namespace"] = ns,
                    ["SlimFaas/Function"] = "true",
                    ["SlimFaas/Template"] = "true"
                })
        };

// (appel #2)
        handler.WhenGETStartsWith("/v1.43/containers/json")
            .RespondJsonOnce(upTemplateList);

// 👉 fallback pour tout appel supplémentaire
        handler.WhenGETStartsWith("/v1.43/containers/json")
            .RespondJson(upTemplateList);

        // 2bis) Fallback pour les listes supplémentaires (ScaleAsync fait désormais plusieurs "list")
        handler.WhenGETStartsWith("/v1.43/containers/json")
            .RespondJson(new List<ContainerSummary>());

        // 3) Inspect du template
        handler.WhenGET("/v1.43/containers/tpl-1/json").RespondJson(
            new InspectContainerResponse(
                "tpl-1",
                "/myapp-template",
                "repo:tag",
                now,
                new Inspect_Config(
                    "repo:tag",
                    new[] { "A=B" },
                    new[] { "dotnet", "run" },
                    new Dictionary<string, string>
                    {
                        ["app"] = app,
                        ["SlimFaas/Namespace"] = ns,
                        ["SlimFaas/Function"] = "true",
                        ["SlimFaas/Template"] = "true"
                    },
                    new Dictionary<string, object> { ["5000/tcp"] = new() }
                ),
                new Inspect_State(false, 0, null, null, null, null),
                new Inspect_NetworkSettings(null, new Dictionary<string, Inspect_EndpointSettings>(),
                    new Dictionary<string, List<Inspect_PortBinding>?>())
            )
        );

        // 4) Pull image (best-effort) — peut être appelé 1+ fois
        handler.WhenPOSTStartsWith("/v1.43/images/create?fromImage=repo&tag=tag").RespondText(200, "{}");
        handler.WhenPOSTStartsWith("/v1.43/images/create?fromImage=repo&tag=tag").RespondText(200, "{}");

        // (optionnel) si le service connecte au réseau compose
        handler.WhenPOSTStartsWith("/v1.43/networks/").RespondNoContent();

        // 5) Création de 2 réplicas + start
        handler.WhenPOSTStartsWith("/v1.43/containers/create?name=")
            .RespondJsonOnce(new CreateContainerResponse("rep-1"));
        handler.WhenPOSTStartsWith("/v1.43/containers/create?name=")
            .RespondJsonOnce(new CreateContainerResponse("rep-2"));
        // --- style sans ?name= (nouveau) ---
        handler.WhenPOSTStartsWith("/v1.43/containers/create")
            .RespondJsonOnce(new CreateContainerResponse("rep-1"));
        handler.WhenPOSTStartsWith("/v1.43/containers/create")
            .RespondJsonOnce(new CreateContainerResponse("rep-2"));

        handler.WhenPOST("/v1.43/containers/rep-1/start").RespondNoContent();
        handler.WhenPOST("/v1.43/containers/rep-2/start").RespondNoContent();

        // (optionnel) certains chemins inspectent les réplicas après start
        handler.WhenGET("/v1.43/containers/rep-1/json").RespondJson(
            new InspectContainerResponse(
                "rep-1", "/rep-1", "repo:tag", now,
                new Inspect_Config("repo:tag", Array.Empty<string>(), new[] { "dotnet","run" },
                    new Dictionary<string,string>(), new Dictionary<string,object>()),
                new Inspect_State(true,0,null,null, now, null),
                new Inspect_NetworkSettings(null, new Dictionary<string, Inspect_EndpointSettings>(),
                    new Dictionary<string, List<Inspect_PortBinding>?>())
            )
        );
        handler.WhenGET("/v1.43/containers/rep-2/json").RespondJson(
            new InspectContainerResponse(
                "rep-2", "/rep-2", "repo:tag", now,
                new Inspect_Config("repo:tag", Array.Empty<string>(), new[] { "dotnet","run" },
                    new Dictionary<string,string>(), new Dictionary<string,object>()),
                new Inspect_State(true,0,null,null, now, null),
                new Inspect_NetworkSettings(null, new Dictionary<string, Inspect_EndpointSettings>(),
                    new Dictionary<string, List<Inspect_PortBinding>?>())
            )
        );

        DockerService svc = MakeService(handler);

        // Act
        ReplicaRequest req = new(app, ns, 2, PodType.Deployment);
        ReplicaRequest? res = await svc.ScaleAsync(req);

        // Assert
        Assert.Equal(2, res!.Replicas);
        Assert.True(
            handler.WasCalledContains(HttpMethod.Post, "/containers/create?name=") ||
            handler.WasCalled(HttpMethod.Post, "/v1.43/containers/create") ||
            handler.WasCalled(HttpMethod.Post, "/containers/create")
        );
        Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/containers/rep-1/start"));
        Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/containers/rep-2/start"));
    }



 /* [Fact]
public async Task ScaleAsync_ScaleDownToZero_CreatesTemplate_ThenStopsAndRemovesRunning()
{
    // Arrange
    string ns = "ns2";
    string app = "svc";
    DateTimeOffset now = DateTimeOffset.UtcNow;

    FakeDockerHandler handler = new();

    // ctor
    handler.WhenGET("/version").RespondJson(new DockerVersionResponse("1.43", "25.0.0"));

    // (optionnel) self-inspect
    handler.WhenGETStartsWith("/v1.43/containers/_self_").RespondStatus(HttpStatusCode.NotFound);

    // 1) LIST #1 (all=true, filters=function/app/ns) → 2 running
    handler.WhenGETStartsWith("/v1.43/containers/json")
        .RespondJsonOnce(new List<ContainerSummary>
        {
            new("run-1", new List<string> { "/svc-1" }, "repo:tag", "running", "Up",
                new Dictionary<string, string>
                {
                    { "app", app }, { "SlimFaas/Namespace", ns }, { "SlimFaas/Function", "true" }
                }),
            new("run-2", new List<string> { "/svc-2" }, "repo:tag", "running", "Up",
                new Dictionary<string, string>
                {
                    { "app", app }, { "SlimFaas/Namespace", ns }, { "SlimFaas/Function", "true" }
                })
        });

    // 2) LIST #2 (check template) → 0
    handler.WhenGETStartsWith("/v1.43/containers/json")
        .RespondJsonOnce(new List<ContainerSummary>());

    // 3) LIST #3 (resolve name "svc-template") → 0
    handler.WhenGETStartsWith("/v1.43/containers/json")
        .RespondJsonOnce(new List<ContainerSummary>());

    // 4) Fallback: toutes autres listes → []
    handler.WhenGETStartsWith("/v1.43/containers/json")
        .RespondJson(new List<ContainerSummary>());

    // TryInspect run-1 (pour fabriquer le template)
    handler.WhenGET("/v1.43/containers/run-1/json").RespondJson(
        new InspectContainerResponse(
            "run-1",
            "/svc-1",
            "repo:tag",
            now.AddMinutes(-5),
            new Inspect_Config(
                "repo:tag",
                new[] { "X=Y" },
                new[] { "/bin/app" },
                new Dictionary<string, string>
                {
                    { "app", app }, { "SlimFaas/Namespace", ns }, { "SlimFaas/Function", "true" }
                },
                new Dictionary<string, object> { ["8080/tcp"] = new() }
            ),
            new Inspect_State(true, 0, null, null, now.AddMinutes(-5), null),
            new Inspect_NetworkSettings(null, new Dictionary<string, Inspect_EndpointSettings>(),
                new Dictionary<string, List<Inspect_PortBinding>?>())
        )
    );

    // (optionnel) Pull image pendant EnsureTemplateContainerAsync
    handler.WhenPOSTStartsWith("/v1.43/images/create?fromImage=repo&tag=tag").RespondText(200, "{}");

    // Création du template (stopped, pas de start)
    handler.WhenPOSTStartsWith("/v1.43/containers/create?name=svc-template")
        .RespondJsonOnce(new CreateContainerResponse("tpl-new"));

    // Scale down: stop + remove les 2 running
    handler.WhenPOST("/v1.43/containers/run-1/stop").RespondNoContent();
    handler.WhenDELETE("/v1.43/containers/run-1?force=true").RespondNoContent();
    handler.WhenPOST("/v1.43/containers/run-2/stop").RespondNoContent();
    handler.WhenDELETE("/v1.43/containers/run-2?force=true").RespondNoContent();

    DockerService svc = MakeService(handler);

    // Act
    ReplicaRequest req = new(app, ns, 0, PodType.Deployment);
    ReplicaRequest? res = await svc.ScaleAsync(req);

    // Assert
    Assert.Equal(0, res!.Replicas);
    Assert.True(handler.WasCalledContains(HttpMethod.Post, "/containers/create?name=svc-template"));
    Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/containers/run-1/stop"));
    Assert.True(handler.WasCalled(HttpMethod.Delete, "/v1.43/containers/run-1?force=true"));
    Assert.True(handler.WasCalled(HttpMethod.Post, "/v1.43/containers/run-2/stop"));
    Assert.True(handler.WasCalled(HttpMethod.Delete, "/v1.43/containers/run-2?force=true"));
}*/

}

// ============================================================
//         FAKE DOCKER HTTP HANDLER (routes in-memory)
// ============================================================

internal class FakeDockerHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod method, string path)> _calls = new();

    internal readonly List<(string path, string body)> _lastBodies = new();

    private readonly List<(Predicate<HttpRequestMessage> match,
        Func<HttpRequestMessage, HttpResponseMessage> resp,
        bool once)> _routes = new();

    public RouteBuilder WhenGET(string path) => new(this, HttpMethod.Get, path, true);
    public RouteBuilder WhenGETStartsWith(string prefix) => new(this, HttpMethod.Get, prefix, false);
    public RouteBuilder WhenPOST(string path) => new(this, HttpMethod.Post, path, true);
    public RouteBuilder WhenDELETE(string path) => new(this, HttpMethod.Delete, path, true);

    public RouteBuilder WhenPOSTStartsWith(string prefix) => new(this, HttpMethod.Post, prefix, false);


    public bool WasCalled(HttpMethod m, string pathOrSuffix) =>
        _calls.Any(c =>
            c.method == m &&
            (c.path.EndsWith(pathOrSuffix, StringComparison.Ordinal) ||
             Normalize(c.path) == Normalize(pathOrSuffix)));

    public bool WasCalledContains(HttpMethod m, string fragment) =>
        _calls.Any(c => c.method == m && c.path.Contains(fragment, StringComparison.Ordinal));

    public T? LastJsonBody<T>(string pathSuffix)
    {
        (string path, string body) rec =
            _lastBodies.LastOrDefault(x => x.path.EndsWith(pathSuffix, StringComparison.Ordinal));
        if (rec.path == null)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(rec.body);
    }

    private static string Normalize(string p) => p.Replace("/v1.43", "", StringComparison.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string full = request.RequestUri!.AbsolutePath + request.RequestUri!.Query;
        _calls.Add((request.Method, full));

        if (request.Content != null && request.Method == HttpMethod.Post)
        {
            string body = await request.Content.ReadAsStringAsync();
            _lastBodies.Add((full, body));
        }

        for (int i = 0; i < _routes.Count; i++)
        {
            (Predicate<HttpRequestMessage> match, Func<HttpRequestMessage, HttpResponseMessage> resp, bool once) =
                _routes[i];
            if (match(request))
            {
                HttpResponseMessage result = resp(request);
                if (once)
                {
                    _routes.RemoveAt(i); // 👈 consomme la route “once”
                }

                return result;
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No fake route for {request.Method} {full}")
        };
    }

    public class RouteBuilder
    {
        private readonly bool _exact;
        private readonly FakeDockerHandler _h;
        private readonly HttpMethod _m;
        private readonly string _p;

        public RouteBuilder(FakeDockerHandler h, HttpMethod m, string p, bool exact)
        {
            _h = h;
            _m = m;
            _p = p;
            _exact = exact;
        }

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
            if (req.Method != _m)
            {
                return false;
            }

            string full = req.RequestUri!.AbsolutePath + req.RequestUri!.Query; // 👈 inclut la query
            if (_exact)
            {
                return full.EndsWith(_p, StringComparison.Ordinal)
                       || full.Equals(_p, StringComparison.Ordinal)
                       || full.EndsWith("/v1.43" + _p, StringComparison.Ordinal);
            }

            return full.Contains(_p, StringComparison.Ordinal);
        }

        private static HttpResponseMessage MakeJson(object obj, int status)
        {
            HttpResponseMessage msg = new((HttpStatusCode)status);
            msg.Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
            return msg;
        }

        private static HttpResponseMessage MakeText(int status, string text)
        {
            HttpResponseMessage msg = new((HttpStatusCode)status);
            msg.Content = new StringContent(text, Encoding.UTF8, "application/json");
            return msg;
        }
    }
}
