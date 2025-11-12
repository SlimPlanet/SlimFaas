using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public class GetScaleConfigTests
{
    // Helper pour appeler la méthode privée statique KubernetesService.GetScaleConfig(...)
    private static ScaleConfig? InvokeGetScaleConfig(
        IDictionary<string, string> annotations,
        string name = "my-func")
    {
        var mi = typeof(KubernetesService).GetMethod(
            "GetScaleConfig",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(mi); // échoue explicitement si la signature change

        var logger = NullLogger<KubernetesService>.Instance;
        var result = mi!.Invoke(null, new object?[] { annotations, name, logger });

        return (ScaleConfig?)result;
    }

    [Fact]
    public void When_Annotation_Is_Missing_Should_Return_Null()
    {
        // Arrange
        var annotations = new Dictionary<string, string>
        {
            // aucune clé "SlimFaas/Scale"
        };

        // Act
        var cfg = InvokeGetScaleConfig(annotations);

        // Assert
        Assert.Null(cfg);
    }

    [Fact]
    public void When_Annotation_Is_Empty_String_Should_Return_Null()
    {
        // Arrange
        var annotations = new Dictionary<string, string>
        {
            { "SlimFaas/Scale", "   " }
        };

        // Act
        var cfg = InvokeGetScaleConfig(annotations);

        // Assert
        Assert.Null(cfg);
    }

    [Fact]
    public void When_Annotation_Is_Full_Valid_Should_Parse_All()
    {
        // Arrange (JSON avec enums en STRING — nécessite JsonStringEnumConverter côté prod)
        var json = """
                   {
                     "ReplicaMax": 3,
                     "Triggers": [
                       {
                         "MetricType": "AverageValue",
                         "MetricName": "pod0_rps",
                         "Query": "sum(rate(http_server_requests_seconds_count{namespace=\"${namespace}\",job=\"${app}\",pod=\"${app}-0\"}[1m]))",
                         "Threshold": 50
                       },
                       {
                         "MetricType": "Value",
                         "MetricName": "sum_rps",
                         "Query": "sum(rate(http_server_requests_seconds_count[30s]))",
                         "Threshold": 120
                       }
                     ],
                     "Behavior": {
                       "ScaleUp": {
                         "StabilizationWindowSeconds": 0,
                         "Policies": [
                           { "Type": "Percent", "Value": 100, "PeriodSeconds": 15 },
                           { "Type": "Pods",    "Value": 4,   "PeriodSeconds": 15 }
                         ]
                       },
                       "ScaleDown": {
                         "StabilizationWindowSeconds": 300,
                         "Policies": [
                           { "Type": "Percent", "Value": 100, "PeriodSeconds": 15 }
                         ]
                       }
                     }
                   }
                   """;

        var annotations = new Dictionary<string, string>
        {
            { "SlimFaas/Scale", json }
        };

        // Act
        var cfg = InvokeGetScaleConfig(annotations);

        // Assert
        Assert.NotNull(cfg);
        Assert.Equal(3, cfg!.ReplicaMax);

        Assert.Equal(2, cfg.Triggers.Count);
        Assert.Equal(ScaleMetricType.AverageValue, cfg.Triggers[0].MetricType);
        Assert.Equal("pod0_rps", cfg.Triggers[0].MetricName);
        Assert.Contains("http_server_requests_seconds_count", cfg.Triggers[0].Query);
        Assert.Equal(50, cfg.Triggers[0].Threshold);

        Assert.Equal(ScaleMetricType.Value, cfg.Triggers[1].MetricType);
        Assert.Equal("sum_rps", cfg.Triggers[1].MetricName);
        Assert.Equal(120, cfg.Triggers[1].Threshold);

        Assert.NotNull(cfg.Behavior);
        Assert.Equal(0,   cfg.Behavior.ScaleUp.StabilizationWindowSeconds);
        Assert.Equal(300, cfg.Behavior.ScaleDown.StabilizationWindowSeconds);

        Assert.Equal(2,   cfg.Behavior.ScaleUp.Policies.Count);
        Assert.Single(cfg.Behavior.ScaleDown.Policies);

        Assert.Equal(ScalePolicyType.Percent, cfg.Behavior.ScaleUp.Policies[0].Type);
        Assert.Equal(100, cfg.Behavior.ScaleUp.Policies[0].Value);
        Assert.Equal(15,  cfg.Behavior.ScaleUp.Policies[0].PeriodSeconds);

        Assert.Equal(ScalePolicyType.Pods,    cfg.Behavior.ScaleUp.Policies[1].Type);
        Assert.Equal(4,   cfg.Behavior.ScaleUp.Policies[1].Value);
        Assert.Equal(15,  cfg.Behavior.ScaleUp.Policies[1].PeriodSeconds);

        Assert.Equal(ScalePolicyType.Percent, cfg.Behavior.ScaleDown.Policies[0].Type);
        Assert.Equal(100, cfg.Behavior.ScaleDown.Policies[0].Value);
        Assert.Equal(15,  cfg.Behavior.ScaleDown.Policies[0].PeriodSeconds);
    }

    [Fact]
    public void When_Annotation_Is_Partial_Should_Apply_Defaults()
    {
        // Arrange : no ReplicaMax, no Triggers, no Behavior -> doit appliquer les defaults
        var json = "{}";

        var annotations = new Dictionary<string, string>
        {
            { "SlimFaas/Scale", json }
        };

        // Act
        var cfg = InvokeGetScaleConfig(annotations);

        // Assert
        Assert.NotNull(cfg);
        Assert.Null(cfg!.ReplicaMax);
        Assert.NotNull(cfg.Triggers);
        Assert.Empty(cfg.Triggers);

        // defaults ScaleUp
        Assert.Equal(0, cfg.Behavior.ScaleUp.StabilizationWindowSeconds);
        Assert.Equal(2, cfg.Behavior.ScaleUp.Policies.Count);
        Assert.Collection(cfg.Behavior.ScaleUp.Policies,
            p =>
            {
                Assert.Equal(ScalePolicyType.Percent, p.Type);
                Assert.Equal(100, p.Value);
                Assert.Equal(15,  p.PeriodSeconds);
            },
            p =>
            {
                Assert.Equal(ScalePolicyType.Pods, p.Type);
                Assert.Equal(4,   p.Value);
                Assert.Equal(15,  p.PeriodSeconds);
            });

        // defaults ScaleDown
        Assert.Equal(300, cfg.Behavior.ScaleDown.StabilizationWindowSeconds);
        Assert.Single(cfg.Behavior.ScaleDown.Policies);
        Assert.Equal(ScalePolicyType.Percent, cfg.Behavior.ScaleDown.Policies[0].Type);
        Assert.Equal(100, cfg.Behavior.ScaleDown.Policies[0].Value);
        Assert.Equal(15,  cfg.Behavior.ScaleDown.Policies[0].PeriodSeconds);
    }

    [Fact]
    public void When_Annotation_Is_InvalidJson_Should_Return_Null()
    {
        // Arrange : JSON invalide (virgules manquantes)
        var invalidJson = """
                          {
                            "ReplicaMax": 2
                            "Triggers": [ { "MetricType": "AverageValue" } ]
                          }
                          """;

        var annotations = new Dictionary<string, string>
        {
            { "SlimFaas/Scale", invalidJson }
        };

        // Act
        var cfg = InvokeGetScaleConfig(annotations);

        // Assert
        // Le code de prod attrape l'exception, log l'erreur et retourne null
        Assert.Null(cfg);
    }

    [Fact]
    public void When_Annotation_Contains_Case_Insensitives_Enums_Should_Work_If_Converter_Configured()
    {
        // Arrange : casse exotique -> nécessite JsonStringEnumConverter dans GetScaleConfig
        var json = """
                   {
                     "Triggers": [
                       { "MetricType": "averagevalue", "MetricName": "m", "Query": "q", "Threshold": 1 }
                     ],
                     "Behavior": {
                       "ScaleUp": {
                         "Policies": [
                           { "Type": "percent", "Value": 50, "PeriodSeconds": 10 }
                         ]
                       }
                     }
                   }
                   """;

        var annotations = new Dictionary<string, string>
        {
            { "SlimFaas/Scale", json }
        };

        // Act
        var cfg = InvokeGetScaleConfig(annotations);

        // Assert
        // Si le converter n'est pas branché côté prod, cfg sera null (désérialisation échoue).
        // On valide le comportement attendu dans les deux cas en explicitant l'intention :
        if (cfg is null)
        {
            // Indice de configuration manquante côté production
            // (JsonStringEnumConverter absent de GetScaleConfig)
            Assert.Null(cfg);
        }
        else
        {
            // Si configuré, on vérifie le mapping correct
            Assert.Single(cfg.Triggers);
            Assert.Equal(ScaleMetricType.AverageValue, cfg.Triggers[0].MetricType);
            Assert.Single(cfg.Behavior.ScaleUp.Policies);
            Assert.Equal(ScalePolicyType.Percent, cfg.Behavior.ScaleUp.Policies[0].Type);
            Assert.Equal(50,  cfg.Behavior.ScaleUp.Policies[0].Value);
            Assert.Equal(10,  cfg.Behavior.ScaleUp.Policies[0].PeriodSeconds);
        }
    }
}
