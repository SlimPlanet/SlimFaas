using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class FunctionMetadataParserTests
{
    [Fact]
    public void Parse_MapsTheKubernetesAnnotationContract()
    {
        var annotations = new Dictionary<string, string>
        {
            [FunctionAnnotationNames.Function] = "true",
            [FunctionAnnotationNames.ReplicasMin] = "1",
            [FunctionAnnotationNames.ReplicasAtStart] = "2",
            [FunctionAnnotationNames.TimeoutSecondBeforeSetReplicasMin] = "45",
            [FunctionAnnotationNames.NumberParallelRequest] = "20",
            [FunctionAnnotationNames.NumberParallelRequestPerPod] = "4",
            [FunctionAnnotationNames.DependsOn] = "one,two",
            [FunctionAnnotationNames.SubscribeEvents] = "Private:event-one,event-two",
            [FunctionAnnotationNames.PathsStartWithVisibility] = "Private:/admin,/public",
            [FunctionAnnotationNames.DefaultVisibility] = "Private",
            [FunctionAnnotationNames.DefaultTrust] = "Untrusted",
            [FunctionAnnotationNames.Configuration] =
                """{"DefaultSync":{"HttpTimeout":42,"TimeoutRetries":[1],"HttpStatusRetries":[503]}}""",
            [FunctionAnnotationNames.Schedule] =
                """{"TimeZoneID":"UTC","Default":{"WakeUp":["08:00"],"ScaleDownTimeout":[]}}""",
            [FunctionAnnotationNames.Scale] =
                """
                {
                  "ReplicaMax": 7,
                  "Triggers": [{
                    "MetricType": "AverageValue",
                    "MetricName": "requests",
                    "Query": "sum(requests)",
                    "Threshold": 10
                  }]
                }
                """
        };

        FunctionMetadata metadata = FunctionMetadataParser.Parse(annotations, "function");

        Assert.Equal(1, metadata.ReplicasMin);
        Assert.Equal(2, metadata.ReplicasAtStart);
        Assert.Equal(45, metadata.TimeoutSecondBeforeSetReplicasMin);
        Assert.Equal(20, metadata.NumberParallelRequest);
        Assert.Equal(4, metadata.NumberParallelRequestPerPod);
        Assert.Equal(["one", "two"], metadata.DependsOn);
        Assert.Equal(FunctionVisibility.Private, metadata.Visibility);
        Assert.Equal(FunctionTrust.Untrusted, metadata.Trust);
        Assert.Equal(42, metadata.Configuration.DefaultSync.HttpTimeout);
        Assert.Equal("UTC", metadata.Schedule?.TimeZoneID);
        Assert.Equal(7, metadata.Scale?.ReplicaMax);
        Assert.Equal(ScaleMetricType.AverageValue, Assert.Single(metadata.Scale!.Triggers).MetricType);
        Assert.Equal(FunctionVisibility.Private, metadata.SubscribeEvents[0].Visibility);
        Assert.Equal(FunctionVisibility.Private, metadata.SubscribeEvents[1].Visibility);
        Assert.Equal(FunctionVisibility.Private, metadata.PathsStartWithVisibility[0].Visibility);
        Assert.Equal(FunctionVisibility.Public, metadata.PathsStartWithVisibility[1].Visibility);
    }

    [Fact]
    public void Parse_AppliesDefaultsToOmittedConfigurationSections()
    {
        FunctionMetadata metadata = FunctionMetadataParser.Parse(
            new Dictionary<string, string>
            {
                [FunctionAnnotationNames.Function] = "true",
                [FunctionAnnotationNames.Configuration] =
                    """{"DefaultPublish":{"HttpTimeout":15,"TimeoutRetries":[1,2,4],"HttpStatusRetries":[500,502,503]}}"""
            },
            "function");

        Assert.Equal(120, metadata.Configuration.DefaultSync.HttpTimeout);
        Assert.Equal(120, metadata.Configuration.DefaultAsync.HttpTimeout);
        Assert.Equal([2, 4, 8], metadata.Configuration.DefaultAsync.TimeoutRetries);
        Assert.Equal([500, 502, 503], metadata.Configuration.DefaultAsync.HttpStatusRetries);
        Assert.Equal(15, metadata.Configuration.DefaultPublish.HttpTimeout);
        Assert.Equal([1, 2, 4], metadata.Configuration.DefaultPublish.TimeoutRetries);
        Assert.Equal([500, 502, 503], metadata.Configuration.DefaultPublish.HttpStatusRetries);
    }

    [Fact]
    public void Parse_ReplacesNullConfigurationSectionsWithDefaults()
    {
        FunctionMetadata metadata = FunctionMetadataParser.Parse(
            new Dictionary<string, string>
            {
                [FunctionAnnotationNames.Function] = "true",
                [FunctionAnnotationNames.Configuration] =
                    """{"DefaultSync":null,"DefaultAsync":null,"DefaultPublish":null}"""
            },
            "function");

        Assert.Equal(120, metadata.Configuration.DefaultSync.HttpTimeout);
        Assert.Equal(120, metadata.Configuration.DefaultAsync.HttpTimeout);
        Assert.Equal(120, metadata.Configuration.DefaultPublish.HttpTimeout);
    }

    [Fact]
    public void Parse_LeavesScaleNullWhenAnnotationIsAbsent()
    {
        FunctionMetadata metadata = FunctionMetadataParser.Parse(
            new Dictionary<string, string>
            {
                [FunctionAnnotationNames.Function] = "true",
                [FunctionAnnotationNames.ReplicasMin] = "0",
                [FunctionAnnotationNames.ReplicasAtStart] = "0"
            },
            "function");

        Assert.Null(metadata.Scale);
        Assert.NotNull(metadata.Schedule);
    }

    [Fact]
    public void Parse_NormalizesNamedExternalSourcesAndTriggerReferences()
    {
        FunctionMetadata metadata = FunctionMetadataParser.Parse(
            new Dictionary<string, string>
            {
                [FunctionAnnotationNames.Function] = "true",
                [FunctionAnnotationNames.Scale] =
                    """
                    {
                      "Sources": [{"Name":" redis ","Url":" https://metrics.example.test/openmetrics?tenant=one "}],
                      "Triggers": [{"MetricName":"queue","Query":"queue_depth","Threshold":1,"Source":" redis "}]
                    }
                    """
            },
            "function");

        ScaleConfig scale = Assert.IsType<ScaleConfig>(metadata.Scale);
        ScaleSource source = Assert.Single(scale.Sources);
        Assert.Equal("redis", source.Name);
        Assert.Equal("https://metrics.example.test/openmetrics?tenant=one", source.Url);
        Assert.Equal("redis", Assert.Single(scale.Triggers).Source);
    }

    [Theory]
    [InlineData("ftp://metrics.example.test/metrics")]
    [InlineData("/metrics")]
    [InlineData("https://user:secret@metrics.example.test/metrics")]
    [InlineData("https://metrics.example.test/metrics#private")]
    public void NormalizeScaleConfig_RejectsInvalidExternalSourceUrls(string url)
    {
        var scale = new ScaleConfig
        {
            Sources = [new ScaleSource("source", url)]
        };

        Assert.Throws<FormatException>(() => FunctionMetadataParser.NormalizeScaleConfig(scale));
    }

    [Fact]
    public void NormalizeScaleConfig_RejectsDuplicateSourceNamesCaseSensitively()
    {
        var duplicate = new ScaleConfig
        {
            Sources =
            [
                new ScaleSource("redis", "http://redis-one/metrics"),
                new ScaleSource("redis", "http://redis-two/metrics")
            ]
        };
        var distinctCase = duplicate with
        {
            Sources =
            [
                new ScaleSource("redis", "http://redis-one/metrics"),
                new ScaleSource("Redis", "http://redis-two/metrics")
            ]
        };

        Assert.Throws<FormatException>(() => FunctionMetadataParser.NormalizeScaleConfig(duplicate));
        Assert.Equal(2, FunctionMetadataParser.NormalizeScaleConfig(distinctCase).Sources.Count);
    }

    [Fact]
    public void NormalizeScaleConfig_RejectsUnknownTriggerSource()
    {
        var scale = new ScaleConfig
        {
            Sources = [new ScaleSource("known", "http://metrics/metrics")],
            Triggers = [new ScaleTrigger(Query: "queue_depth", Threshold: 1, Source: "unknown")]
        };

        Assert.Throws<FormatException>(() => FunctionMetadataParser.NormalizeScaleConfig(scale));
    }
}
