using System.Reflection;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

/// <summary>
/// Tests for KubernetesService.ExtractJobConfigurations exercised through the
/// public ListJobsConfigurationAsync method.
/// A fake HttpMessageHandler intercepts the k8s API call and returns a
/// pre-built V1CronJobList, so no real cluster is needed.
/// </summary>
public class ExtractJobConfigurationsTests
{
    // ── infrastructure ────────────────────────────────────────────────────────

    /// <summary>
    /// Fake handler that returns a serialised V1CronJobList for any request.
    /// </summary>
    private sealed class FakeCronJobHandler(V1CronJobList list) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json = KubernetesJson.Serialize(list);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Builds a KubernetesService whose internal k8s client is wired to the
    /// fake handler, so ListJobsConfigurationAsync returns the result of
    /// ExtractJobConfigurations on the provided list.
    /// </summary>
    private static KubernetesService BuildService(V1CronJobList list)
    {
        var config = new KubernetesClientConfiguration { Host = "http://localhost" };
        var k8sClient = new k8s.Kubernetes(config, new FakeCronJobHandler(list));

        // KubernetesService has no constructor accepting a ready-made client,
        // so we inject it via reflection into the private _client field.
        var svc = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(KubernetesService));

        typeof(KubernetesService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(svc, k8sClient);

        typeof(KubernetesService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(svc, NullLogger<KubernetesService>.Instance);

        return svc;
    }

    private static V1CronJob MakeSlimfaasCronJob(
        string name,
        string image = "myimage:latest",
        bool suspend = true,
        bool isSlimfaasJob = true,
        string? imagesWhitelist = null,
        string? dependsOn = null,
        string? visibility = null,
        string? numberParallel = null,
        string restartPolicy = "Never",
        int backoffLimit = 1,
        int ttlSeconds = 60,
        IList<V1EnvVar>? envVars = null,
        IList<V1EnvFromSource>? envFrom = null)
    {
        var annotations = new Dictionary<string, string>();
        if (isSlimfaasJob)
            annotations["SlimFaas/Job"] = "true";
        if (imagesWhitelist != null)
            annotations["SlimFaas/JobImagesWhitelist"] = imagesWhitelist;
        if (dependsOn != null)
            annotations["SlimFaas/DependsOn"] = dependsOn;
        if (visibility != null)
            annotations["SlimFaas/DefaultVisibility"] = visibility;
        if (numberParallel != null)
            annotations["SlimFaas/NumberParallelJob"] = numberParallel;

        var container = new V1Container
        {
            Name = name,
            Image = image,
            Env = envVars,
            EnvFrom = envFrom,
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    { "cpu",    new ResourceQuantity("100m")  },
                    { "memory", new ResourceQuantity("128Mi") }
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    { "cpu",    new ResourceQuantity("200m")  },
                    { "memory", new ResourceQuantity("256Mi") }
                }
            }
        };

        return new V1CronJob
        {
            Metadata = new V1ObjectMeta { Name = name, Annotations = annotations },
            Spec = new V1CronJobSpec
            {
                Suspend = suspend,
                JobTemplate = new V1JobTemplateSpec
                {
                    Spec = new V1JobSpec
                    {
                        BackoffLimit = backoffLimit,
                        TtlSecondsAfterFinished = ttlSeconds,
                        Template = new V1PodTemplateSpec
                        {
                            Spec = new V1PodSpec
                            {
                                RestartPolicy = restartPolicy,
                                Containers = new List<V1Container> { container }
                            }
                        }
                    }
                }
            }
        };
    }

    private static V1CronJobList List(params V1CronJob[] jobs) =>
        new() { Items = new List<V1CronJob>(jobs) };

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Empty list returns null")]
    public async Task Extract_EmptyList_ReturnsNull()
    {
        var svc = BuildService(List());

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.Null(result);
    }

    [Fact(DisplayName = "CronJob without SlimFaas/Job annotation is ignored")]
    public async Task Extract_NotSlimfaasJob_IsIgnored()
    {
        var svc = BuildService(List(MakeSlimfaasCronJob("my-job", isSlimfaasJob: false)));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.Null(result);
    }

    [Fact(DisplayName = "Non-suspended CronJob is skipped")]
    public async Task Extract_NotSuspended_IsSkipped()
    {
        var svc = BuildService(List(MakeSlimfaasCronJob("my-job", suspend: false)));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.Null(result);
    }

    [Fact(DisplayName = "Valid suspended SlimFaas CronJob is extracted correctly")]
    public async Task Extract_ValidSuspendedJob_IsMapped()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("compute-job", image: "compute:v1",
                backoffLimit: 3, ttlSeconds: 120, restartPolicy: "OnFailure")));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        Assert.True(result.Configurations.ContainsKey("compute-job"));

        SlimfaasJob extracted = result.Configurations["compute-job"];
        Assert.Equal("compute:v1",  extracted.Image);
        Assert.Equal(3,             extracted.BackoffLimit);
        Assert.Equal(120,           extracted.TtlSecondsAfterFinished);
        Assert.Equal("OnFailure",   extracted.RestartPolicy);
        Assert.Equal("100m",        extracted.Resources!.Requests["cpu"]);
        Assert.Equal("200m",        extracted.Resources!.Limits["cpu"]);
    }

    [Fact(DisplayName = "ImagesWhitelist annotation is split on comma")]
    public async Task Extract_ImagesWhitelist_IsParsedCorrectly()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("wl-job", imagesWhitelist: "img1:latest, img2:v2 , img3:v3")));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var whitelist = result.Configurations["wl-job"].ImagesWhitelist;
        Assert.Equal(3, whitelist.Count);
        Assert.Contains("img1:latest", whitelist);
        Assert.Contains("img2:v2",     whitelist);
        Assert.Contains("img3:v3",     whitelist);
    }

    [Fact(DisplayName = "DependsOn annotation is split on comma")]
    public async Task Extract_DependsOn_IsParsedCorrectly()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("dep-job", dependsOn: "jobA, jobB")));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var deps = result.Configurations["dep-job"].DependsOn;
        Assert.NotNull(deps);
        Assert.Equal(2, deps.Count);
        Assert.Contains("jobA", deps);
        Assert.Contains("jobB", deps);
    }

    [Fact(DisplayName = "NumberParallelJob annotation is parsed as integer")]
    public async Task Extract_NumberParallelJob_IsParsed()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("parallel-job", numberParallel: "5")));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        Assert.Equal(5, result.Configurations["parallel-job"].NumberParallelJob);
    }

    [Fact(DisplayName = "DefaultVisibility annotation is mapped")]
    public async Task Extract_Visibility_IsMapped()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("vis-job", visibility: "Public")));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        Assert.Equal("Public", result.Configurations["vis-job"].Visibility);
    }

    [Fact(DisplayName = "Multiple CronJobs: only suspended SlimFaas ones are returned")]
    public async Task Extract_MultipleCronJobs_FiltersCorrectly()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("job-a"),
            MakeSlimfaasCronJob("job-b"),
            MakeSlimfaasCronJob("job-c", isSlimfaasJob: false),
            MakeSlimfaasCronJob("job-d", suspend: false)));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        Assert.Equal(2, result.Configurations.Count);
        Assert.True(result.Configurations.ContainsKey("job-a"));
        Assert.True(result.Configurations.ContainsKey("job-b"));
        Assert.False(result.Configurations.ContainsKey("job-c"));
        Assert.False(result.Configurations.ContainsKey("job-d"));
    }

    [Fact(DisplayName = "CronJob with plain env vars is mapped")]
    public async Task Extract_EnvVars_AreMapped()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("env-job", envVars: new List<V1EnvVar>
            {
                new() { Name = "MY_VAR", Value = "hello" }
            })));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var envs = result.Configurations["env-job"].Environments;
        Assert.NotNull(envs);
        Assert.Single(envs);
        Assert.Equal("MY_VAR", envs[0].Name);
        Assert.Equal("hello",  envs[0].Value);
    }

    [Fact(DisplayName = "CronJob with SecretRef env var is mapped")]
    public async Task Extract_SecretRefEnvVar_IsMapped()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("secret-job", envVars: new List<V1EnvVar>
            {
                new()
                {
                    Name = "SECRET_VAR",
                    ValueFrom = new V1EnvVarSource
                    {
                        SecretKeyRef = new V1SecretKeySelector { Name = "my-secret", Key = "my-key" }
                    }
                }
            })));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var env = result.Configurations["secret-job"].Environments![0];
        Assert.Equal("SECRET_VAR", env.Name);
        Assert.NotNull(env.SecretRef);
        Assert.Equal("my-secret", env.SecretRef!.Name);
        Assert.Equal("my-key",    env.SecretRef.Key);
    }

    [Fact(DisplayName = "EnvFrom with SecretRef is mapped as wildcard entry")]
    public async Task Extract_EnvFrom_SecretRef_IsMapped()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("envfrom-secret-job", envFrom: new List<V1EnvFromSource>
            {
                new()
                {
                    SecretRef = new V1SecretEnvSource { Name = "my-bulk-secret" }
                }
            })));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var envs = result.Configurations["envfrom-secret-job"].Environments;
        Assert.NotNull(envs);
        Assert.Single(envs);

        var entry = envs[0];
        Assert.Equal("*",              entry.Name);
        Assert.Equal("",               entry.Value);
        Assert.NotNull(entry.SecretRef);
        Assert.Equal("my-bulk-secret", entry.SecretRef!.Name);
        Assert.Equal("*",              entry.SecretRef.Key);
        Assert.Null(entry.ConfigMapRef);
    }

    [Fact(DisplayName = "EnvFrom with ConfigMapRef is mapped as wildcard entry")]
    public async Task Extract_EnvFrom_ConfigMapRef_IsMapped()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("envfrom-cm-job", envFrom: new List<V1EnvFromSource>
            {
                new()
                {
                    ConfigMapRef = new V1ConfigMapEnvSource { Name = "my-bulk-config" }
                }
            })));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var envs = result.Configurations["envfrom-cm-job"].Environments;
        Assert.NotNull(envs);
        Assert.Single(envs);

        var entry = envs[0];
        Assert.Equal("*",              entry.Name);
        Assert.Equal("",               entry.Value);
        Assert.NotNull(entry.ConfigMapRef);
        Assert.Equal("my-bulk-config", entry.ConfigMapRef!.Name);
        Assert.Equal("*",              entry.ConfigMapRef.Key);
        Assert.Null(entry.SecretRef);
    }

    [Fact(DisplayName = "EnvFrom with prefix prepends prefix to the wildcard Name")]
    public async Task Extract_EnvFrom_WithPrefix_NameContainsPrefix()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("prefix-job", envFrom: new List<V1EnvFromSource>
            {
                new()
                {
                    Prefix = "APP_",
                    SecretRef = new V1SecretEnvSource { Name = "prefixed-secret" }
                }
            })));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var envs = result.Configurations["prefix-job"].Environments;
        Assert.NotNull(envs);
        Assert.Single(envs);

        var entry = envs[0];
        Assert.Equal("APP_*", entry.Name);
        Assert.NotNull(entry.SecretRef);
        Assert.Equal("prefixed-secret", entry.SecretRef!.Name);
        Assert.Equal("*", entry.SecretRef.Key);
    }

    [Fact(DisplayName = "EnvFrom entries are appended after regular env vars")]
    public async Task Extract_EnvFrom_CombinedWithEnvVars_BothPresent()
    {
        var svc = BuildService(List(
            MakeSlimfaasCronJob("combined-job",
                envVars: new List<V1EnvVar>
                {
                    new() { Name = "PLAIN_VAR", Value = "plain-value" }
                },
                envFrom: new List<V1EnvFromSource>
                {
                    new() { SecretRef  = new V1SecretEnvSource  { Name = "extra-secret" } },
                    new() { ConfigMapRef = new V1ConfigMapEnvSource { Name = "extra-config" } }
                })));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var envs = result.Configurations["combined-job"].Environments;
        Assert.NotNull(envs);
        Assert.Equal(3, envs.Count);

        // First entry is the plain env var
        Assert.Equal("PLAIN_VAR",   envs[0].Name);
        Assert.Equal("plain-value", envs[0].Value);
        Assert.Null(envs[0].SecretRef);
        Assert.Null(envs[0].ConfigMapRef);

        // Second entry: EnvFrom SecretRef wildcard
        Assert.Equal("*",            envs[1].Name);
        Assert.NotNull(envs[1].SecretRef);
        Assert.Equal("extra-secret", envs[1].SecretRef!.Name);
        Assert.Equal("*",            envs[1].SecretRef!.Key);
        Assert.Null(envs[1].ConfigMapRef);

        // Third entry: EnvFrom ConfigMapRef wildcard
        Assert.Equal("*",            envs[2].Name);
        Assert.NotNull(envs[2].ConfigMapRef);
        Assert.Equal("extra-config", envs[2].ConfigMapRef!.Name);
        Assert.Equal("*",            envs[2].ConfigMapRef!.Key);
        Assert.Null(envs[2].SecretRef);
    }

    [Fact(DisplayName = "EnvFrom entries without SecretRef or ConfigMapRef are ignored")]
    public async Task Extract_EnvFrom_UnknownSource_IsIgnored()
    {
        // An EnvFromSource with neither SecretRef nor ConfigMapRef set should be skipped.
        var svc = BuildService(List(
            MakeSlimfaasCronJob("empty-envfrom-job", envFrom: new List<V1EnvFromSource>
            {
                new() // no SecretRef, no ConfigMapRef
            })));

        var result = await svc.ListJobsConfigurationAsync("default");

        Assert.NotNull(result);
        var envs = result.Configurations["empty-envfrom-job"].Environments;
        // The empty EnvFromSource yields null and gets filtered out, so the list is empty.
        Assert.True(envs == null || envs.Count == 0);
    }
}
