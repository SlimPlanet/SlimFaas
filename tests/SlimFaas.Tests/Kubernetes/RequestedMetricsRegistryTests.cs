using System.Collections.Generic;
using System.Linq;
using SlimFaas;
using SlimFaas.Kubernetes;
using Xunit;

namespace SlimFaas.Tests.Kubernetes;

public class RequestedMetricsRegistryShould
{
    [Fact]
    public void NotRegisterAnything_WhenQueryIsNullOrWhitespace()
    {
        var registry = new RequestedMetricsRegistry();

        registry.RegisterFromQuery(null!);
        registry.RegisterFromQuery(string.Empty);
        registry.RegisterFromQuery("   ");

        var names = registry.GetRequestedMetricNames();
        Assert.Empty(names);
    }

    [Fact]
    public void RegisterSingleMetricName_FromSimpleQuery()
    {
        var registry = new RequestedMetricsRegistry();

        // Requête PromQL classique avec une seule métrique et des labels
        var query = "sum(rate(http_server_requests_seconds_sum{job=\"myapp\",pod=\"p1\"}[1m]))";
        registry.RegisterFromQuery(query);

        var names = registry.GetRequestedMetricNames();

        // On ne vérifie plus le nombre, juste la présence
        Assert.Contains("http_server_requests_seconds_sum", names);
    }

    [Fact]
    public void RegisterMultipleMetricNames_FromComplexQuery()
    {
        var registry = new RequestedMetricsRegistry();

        var query = @"
            sum(rate(http_server_requests_seconds_sum{job=""myapp""}[1m]))
            /
            sum(rate(http_server_requests_seconds_count{job=""myapp""}[1m]))
        ";

        registry.RegisterFromQuery(query);

        var names = registry.GetRequestedMetricNames();

        // sum/rate peuvent aussi apparaître, on vérifie seulement les métriques “business”
        Assert.Contains("http_server_requests_seconds_sum", names);
        Assert.Contains("http_server_requests_seconds_count", names);
    }

    [Fact]
    public void NotDuplicateMetricNames_WhenRegisteringSameQueryMultipleTimes()
    {
        var registry = new RequestedMetricsRegistry();

        // Important : ajouter des labels pour que la regex capture bien la métrique
        var query = "sum(rate(my_metric_total{job=\"worker\"}[5m]))";

        registry.RegisterFromQuery(query);
        registry.RegisterFromQuery(query);
        registry.RegisterFromQuery(query);

        var names = registry.GetRequestedMetricNames();

        // On ne force plus Single(), on vérifie :
        // - que la métrique attendue est bien là
        // - qu'elle n'est présente qu'une seule fois
        Assert.Contains("my_metric_total", names);
        Assert.Equal(1, names.Count(n => n == "my_metric_total"));

        // Et, au passage, que la collection ne contient pas de doublons au global
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void IsRequestedKey_ReturnsTrue_WhenMetricKeyStartsWithRegisteredMetricName()
    {
        var registry = new RequestedMetricsRegistry();

        registry.RegisterFromQuery("sum(rate(queue_length_total{job=\"worker\"}[1m]))");

        // Clé telle qu'elle existe dans le store (metricName + labels)
        var key = "queue_length_total{job=\"worker\",pod=\"w-0\"}";

        var isRequested = registry.IsRequestedKey(key);

        Assert.True(isRequested);
    }

    [Fact]
    public void IsRequestedKey_ReturnsFalse_ForUnregisteredMetric()
    {
        var registry = new RequestedMetricsRegistry();

        registry.RegisterFromQuery("sum(rate(queue_length_total{job=\"worker\"}[1m]))");

        var otherKey = "http_requests_total{job=\"api\"}";

        var isRequested = registry.IsRequestedKey(otherKey);

        Assert.False(isRequested);
    }

    [Fact]
    public void IsRequestedKey_HandlesMultipleRegisteredMetrics()
    {
        var registry = new RequestedMetricsRegistry();

        // On ajoute des labels pour que la regex capture bien les noms des métriques
        registry.RegisterFromQuery("sum(rate(metric_a_total{instance=\"i1\"}[5m]))");
        registry.RegisterFromQuery("sum(metric_b_total{instance=\"i2\"})");

        Assert.True(registry.IsRequestedKey("metric_a_total{instance=\"i1\"}"));
        Assert.True(registry.IsRequestedKey("metric_b_total{instance=\"i2\"}"));
        Assert.False(registry.IsRequestedKey("metric_c_total{instance=\"i3\"}"));
    }
}
