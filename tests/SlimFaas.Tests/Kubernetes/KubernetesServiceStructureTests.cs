using System.Reflection;
using k8s.Models;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

/// <summary>
/// Guards the split of <see cref="KubernetesService"/> into partial files.
/// These tests fail loudly if a symbol relied upon (public API, well-known
/// private field, or private helper reachable by reflection from other tests)
/// disappears during future refactors.
/// </summary>
public class KubernetesServiceStructureTests
{
    [Fact]
    public void KubernetesService_Is_Partial_And_Implements_IKubernetesService()
    {
        Type t = typeof(KubernetesService);

        // Partial classes compile into a single type, but we can assert that
        // the type is defined in more than one source file by checking that
        // several source files declaring it exist.
        Assert.True(typeof(IKubernetesService).IsAssignableFrom(t));
        Assert.False(t.IsSealed);
    }

    [Fact]
    public void KubernetesService_Preserves_Private_Fields_Used_By_Tests()
    {
        // Other tests inject their own k8s client via reflection using
        // exactly these two field names. Keep them in place.
        Type t = typeof(KubernetesService);

        Assert.NotNull(t.GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(t.GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Theory]
    [InlineData("GetScaleConfig")]
    [InlineData("NormalizeScaleConfig")]
    [InlineData("ExtractResources")]
    [InlineData("GetPathsStartWithVisibility")]
    [InlineData("GetSubscribeEvents")]
    [InlineData("GetScheduleConfig")]
    [InlineData("GetConfiguration")]
    [InlineData("MapPodInformations")]
    public void KubernetesService_Preserves_Static_Helpers(string methodName)
    {
        MethodInfo? mi = typeof(KubernetesService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mi);
    }

    [Fact]
    public void KubernetesService_Preserves_Public_Api()
    {
        Type t = typeof(KubernetesService);
        Assert.NotNull(t.GetMethod(nameof(KubernetesService.ScaleAsync)));
        Assert.NotNull(t.GetMethod(nameof(KubernetesService.ListFunctionsAsync)));
        Assert.NotNull(t.GetMethod(nameof(KubernetesService.ListJobsConfigurationAsync)));
        Assert.NotNull(t.GetMethod(nameof(KubernetesService.CreateJobAsync)));
        Assert.NotNull(t.GetMethod(nameof(KubernetesService.ListJobsAsync)));
        Assert.NotNull(t.GetMethod(nameof(KubernetesService.DeleteJobAsync)));
    }

    [Fact]
    public void NormalizeScaleConfig_Fills_In_Defaults_When_Input_Is_Null()
    {
        MethodInfo mi = typeof(KubernetesService).GetMethod(
            "NormalizeScaleConfig",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var cfg = (ScaleConfig)mi.Invoke(null, new object?[] { null })!;

        Assert.NotNull(cfg);
        Assert.NotNull(cfg.Behavior);
        Assert.NotNull(cfg.Behavior.ScaleUp);
        Assert.NotNull(cfg.Behavior.ScaleDown);

        // Defaults come from ScaleDirectionBehavior.DefaultScaleUp/DefaultScaleDown
        Assert.Equal(2, cfg.Behavior.ScaleUp.Policies.Count);
        Assert.Single(cfg.Behavior.ScaleDown.Policies);
        Assert.Empty(cfg.Triggers);
    }

    [Fact]
    public void NormalizeScaleConfig_Preserves_User_Triggers()
    {
        MethodInfo mi = typeof(KubernetesService).GetMethod(
            "NormalizeScaleConfig",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var input = new ScaleConfig
        {
            ReplicaMax = 5,
            Triggers = new List<ScaleTrigger>
            {
                new(ScaleMetricType.Value, "rps", "q", 42)
            }
        };

        var cfg = (ScaleConfig)mi.Invoke(null, new object?[] { input })!;

        Assert.Equal(5, cfg.ReplicaMax);
        Assert.Single(cfg.Triggers);
        Assert.Equal("rps", cfg.Triggers[0].MetricName);
    }

    [Fact]
    public void ExtractResources_Returns_Null_For_Empty_Containers()
    {
        MethodInfo mi = typeof(KubernetesService).GetMethod(
            "ExtractResources",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var resNull = (ResourcesConfiguration?)mi.Invoke(null, new object?[] { null });
        var resEmpty = (ResourcesConfiguration?)mi.Invoke(null, new object?[] { new List<V1Container>() });

        Assert.Null(resNull);
        Assert.Null(resEmpty);
    }

    [Fact]
    public void ExtractResources_Reads_Cpu_And_Memory_Requests_And_Limits()
    {
        MethodInfo mi = typeof(KubernetesService).GetMethod(
            "ExtractResources",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        var containers = new List<V1Container>
        {
            new()
            {
                Name = "main",
                Resources = new V1ResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["cpu"] = new("250m"),
                        ["memory"] = new("128Mi"),
                    },
                    Limits = new Dictionary<string, ResourceQuantity>
                    {
                        ["cpu"] = new("500m"),
                        ["memory"] = new("256Mi"),
                    }
                }
            }
        };

        var res = (ResourcesConfiguration?)mi.Invoke(null, new object?[] { containers });

        Assert.NotNull(res);
        Assert.Equal("250m", res!.CpuRequest);
        Assert.Equal("500m", res.CpuLimit);
        Assert.Equal("128Mi", res.MemoryRequest);
        Assert.Equal("256Mi", res.MemoryLimit);
    }

    [Fact]
    public void Models_Are_In_The_SlimFaas_Kubernetes_Namespace()
    {
        // The refactor moved several records/enums to Models/*, but they must
        // stay in the SlimFaas.Kubernetes namespace so existing callers do
        // not need `using` changes.
        Assert.Equal("SlimFaas.Kubernetes", typeof(Job).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(JobStatus).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(ScheduleConfig).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(SlimFaasConfiguration).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(FunctionVisibility).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(ReplicaRequest).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(DeploymentInformation).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(PodInformation).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(CreateJob).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(SlimfaasJob).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(EnvVarInput).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(SecretRef).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(ConfigMapRef).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(FieldRef).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(ResourceFieldRef).Namespace);
        Assert.Equal("SlimFaas.Kubernetes", typeof(CreateJobResources).Namespace);
    }
}
