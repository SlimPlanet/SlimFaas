using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Local;
using SlimFaas.Logs;
using System.Runtime.CompilerServices;
using SlimFaas.Options;

namespace SlimFaas.Kubernetes;

/// <summary>
/// Orchestrator adapter used by SlimFaas nodes launched by the native local
/// supervisor. The supervisor owns every process; nodes only observe topology
/// and submit absolute desired state changes.
/// </summary>
public sealed class ProcessKubernetesService : IKubernetesService, IInstanceLogProvider
{
    public const string HttpClientName = "ProcessKubernetesService";
    public const string TokenHeaderName = "X-SlimFaas-Token";

    private readonly HttpClient _client;

    public ProcessKubernetesService(
        IHttpClientFactory httpClientFactory,
        IOptions<SlimFaasOptions> options)
    {
        ProcessOrchestratorOptions processOptions = options.Value.Process;
        if (!Uri.TryCreate(processOptions.SupervisorUrl, UriKind.Absolute, out Uri? supervisorUri))
        {
            throw new InvalidOperationException(
                "SlimFaas:Process:SupervisorUrl must be an absolute URL.");
        }
        if (string.IsNullOrWhiteSpace(processOptions.Token))
        {
            throw new InvalidOperationException("SlimFaas:Process:Token must not be empty.");
        }

        _client = httpClientFactory.CreateClient(HttpClientName);
        _client.BaseAddress = supervisorUri;
        _client.DefaultRequestHeaders.Add(TokenHeaderName, processOptions.Token);
    }

    public async Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request)
    {
        using HttpResponseMessage response = await _client.PutAsJsonAsync(
            $"v1/functions/{Uri.EscapeDataString(request.Deployment)}/scale",
            new ProcessScaleCommand(request.Replicas),
            ProcessControlJsonContext.Default.ProcessScaleCommand);
        await EnsureSuccessAsync(response);
        return request;
    }

    public async Task<DeploymentsInformations> ListFunctionsAsync(
        string kubeNamespace,
        DeploymentsInformations previousDeployments)
    {
        try
        {
            DeploymentsInformations? result = await _client.GetFromJsonAsync(
                $"v1/topology?namespace={Uri.EscapeDataString(kubeNamespace)}",
                ProcessControlJsonContext.Default.DeploymentsInformations);
            return result ?? previousDeployments;
        }
        catch (HttpRequestException)
        {
            return previousDeployments;
        }
    }

    public async Task<SlimFaasJobConfiguration?> ListJobsConfigurationAsync(string kubeNamespace)
        => await _client.GetFromJsonAsync(
            $"v1/jobs/configuration?namespace={Uri.EscapeDataString(kubeNamespace)}",
            ProcessControlJsonContext.Default.SlimFaasJobConfiguration);

    public async Task CreateJobAsync(
        string kubeNamespace,
        string name,
        CreateJob createJob,
        string elementId,
        string jobFullName,
        long inQueueTimestamp)
    {
        var command = new ProcessCreateJobCommand(
            kubeNamespace,
            name,
            createJob,
            elementId,
            jobFullName,
            inQueueTimestamp);
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "v1/jobs",
            command,
            ProcessControlJsonContext.Default.ProcessCreateJobCommand);
        await EnsureSuccessAsync(response);
    }

    public async Task<IList<Job>> ListJobsAsync(string ns)
    {
        List<Job>? result = await _client.GetFromJsonAsync(
            $"v1/jobs?namespace={Uri.EscapeDataString(ns)}",
            ProcessControlJsonContext.Default.ListJob);
        return result ?? [];
    }

    public async Task DeleteJobAsync(string kubeNamespace, string jobName)
    {
        using HttpResponseMessage response = await _client.DeleteAsync(
            $"v1/jobs/{Uri.EscapeDataString(jobName)}?namespace={Uri.EscapeDataString(kubeNamespace)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        await EnsureSuccessAsync(response);
    }

    public async Task<LogSources> GetSourcesAsync(LogTarget target, CancellationToken ct)
    {
        var query = $"kind={Uri.EscapeDataString(target.Kind)}&name={Uri.EscapeDataString(target.Name)}&replica={Uri.EscapeDataString(target.Replica ?? "")}";
        return await _client.GetFromJsonAsync($"v1/log-sources?{query}", LogJsonContext.Default.LogSources, ct)
            ?? new LogSources("Logs unavailable", []);
    }

    public async IAsyncEnumerable<LogReadItem> ReadAsync(string source, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = LogSourceIds.Parse(source);
        if (!(await GetSourcesAsync(key.Target, ct)).Sources.Any(s => s.Id == source))
            throw new FileNotFoundException("Source removed");
        using var response = await _client.GetAsync($"v1/logs?source={Uri.EscapeDataString(source)}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var item = JsonSerializer.Deserialize(line, LogJsonContext.Default.LogReadItem);
            if (item?.Status == "Source removed") throw new FileNotFoundException("Source removed or restarted");
            if (item?.Status == "Access denied") throw new UnauthorizedAccessException("Log access denied");
            if (item?.Status is not null) throw new IOException("Log stream interrupted");
            if (item is not null) yield return item;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        string detail = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"Local process supervisor returned {(int)response.StatusCode}: {detail}",
            null,
            response.StatusCode);
    }
}
