using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class FunctionMetadataParserTests
{
    [Fact]
    public void ParsePrefersCanonicalMinimumsAndWarnsOncePerFunctionAndLegacyName()
    {
        var annotations = new Dictionary<string, string>
        {
            ["SlimFaas/MinActiveReplicas"] = "2", ["SlimFaas/ReplicasAtStart"] = "7",
            ["SlimFaas/MinIdleReplicas"] = "1", ["SlimFaas/ReplicasMin"] = "5",
            ["SlimFaas/Schedule"] = """
                {"Default":{"ScaleDownTimeout":[
                  {"Time":"09:00","MinActiveReplicas":3,"ReplicasAtStart":9,"ReplicasMin":0},
                  {"Time":"19:00","ReplicasAtStart":4,"MinIdleReplicas":0,"ReplicasMin":8}]}}
                """
        };
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);
        var warnings = new ReplicaAnnotationWarnings(logger.Object);
        var metadata = FunctionMetadataParser.Parse(annotations, "billing", warnings);
        Assert.Equal(2, metadata.ReplicasAtStart);
        Assert.Equal(1, metadata.ReplicasMin);
        var windows = metadata.Schedule!.Default!.ScaleDownTimeout;
        Assert.Equal(3, windows[0].MinActiveReplicas);
        Assert.Equal(0, windows[0].MinIdleReplicas);
        Assert.Equal(4, windows[1].MinActiveReplicas);
        Assert.Equal(0, windows[1].MinIdleReplicas);
        Assert.Equal(4, metadata.LargestReplicaMinimum());

        FunctionMetadataParser.Parse(annotations, "billing", warnings);
        FunctionMetadataParser.Parse(annotations, "catalog", warnings);
        var messages = logger.Invocations.Where(i => i.Method.Name == nameof(ILogger.Log))
            .Select(i => i.Arguments[2]?.ToString()).ToArray();
        Assert.Equal(4, messages.Length);
        Assert.Equal(4, messages.Distinct().Count());
    }

    [Theory]
    [InlineData("MinActiveReplicas")]
    [InlineData("MinIdleReplicas")]
    [InlineData("ReplicasAtStart")]
    [InlineData("ReplicasMin")]
    public void RejectsNegativeReplicaMinimums(string name)
    {
        Assert.Throws<FormatException>(() => FunctionMetadataParser.Parse(
            new Dictionary<string, string> { ["SlimFaas/" + name] = "-1" }, "billing"));
        var schedule = $$$"""{"Default":{"ScaleDownTimeout":[{"Time":"12:00","{{{name}}}":-1}]}}""";
        Assert.Throws<FormatException>(() => FunctionMetadataParser.Parse(
            new Dictionary<string, string> { ["SlimFaas/Schedule"] = schedule }, "billing"));
    }

    [Fact]
    public void Parse_Stream_subscriptions_inherit_visibility_and_parse_bounded_retention()
    {
        var metadata = FunctionMetadataParser.Parse(new Dictionary<string, string>
        {
            [FunctionAnnotationNames.DefaultVisibility] = "Private",
            [FunctionAnnotationNames.SubscribeStream] = "orders,Public:notifications",
            [FunctionAnnotationNames.StreamMaxAge] = "24h",
            [FunctionAnnotationNames.StreamMaxLength] = "123"
        }, "billing");
        Assert.Equal([new SubscribeStream("orders", FunctionVisibility.Private),
            new SubscribeStream("notifications", FunctionVisibility.Public)], metadata.SubscribeStreams);
        Assert.Equal(86400, metadata.StreamMaxAgeSeconds);
        Assert.Equal(123, metadata.StreamMaxLength);
    }

    [Theory]
    [InlineData("Unknown:orders", "24h")]
    [InlineData("orders,orders", "24h")]
    [InlineData("orders", "0h")]
    [InlineData("../orders", "24h")]
    [InlineData("orders", "999999999999d")]
    public void Parse_rejects_invalid_Stream_configuration(string subscriptions, string retention) =>
        Assert.Throws<FormatException>(() => FunctionMetadataParser.Parse(new Dictionary<string, string>
        {
            [FunctionAnnotationNames.SubscribeStream] = subscriptions,
            [FunctionAnnotationNames.StreamMaxAge] = retention
        }, "billing"));

    [Fact]
    public void Parse_SeparatesVisibilityPrefixesFromPathsAndEventNames()
    {
        var metadata = FunctionMetadataParser.Parse(new Dictionary<string, string>
        {
            [FunctionAnnotationNames.DefaultVisibility] = "Private",
            [FunctionAnnotationNames.PathsStartWithVisibility] =
                "Public:/v1/configuration/dataset_versions/dynamic,Private:/v1/speech_to_text/callback",
            [FunctionAnnotationNames.SubscribeEvents] = "Private:speech_to_text_result,Public:internal_events"
        }, "function");

        Assert.Equal(
        [
            new PathVisibility("/v1/configuration/dataset_versions/dynamic", FunctionVisibility.Public),
            new PathVisibility("/v1/speech_to_text/callback", FunctionVisibility.Private)
        ], metadata.PathsStartWithVisibility);
        Assert.Equal(
        [
            new SubscribeEvent("speech_to_text_result", FunctionVisibility.Private),
            new SubscribeEvent("internal_events", FunctionVisibility.Public)
        ], metadata.SubscribeEvents);
    }

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
        Assert.Equal([500, 502, 503, 504], metadata.Configuration.DefaultAsync.HttpStatusRetries);
        Assert.Equal(15, metadata.Configuration.DefaultPublish.HttpTimeout);
        Assert.Equal([1, 2, 4], metadata.Configuration.DefaultPublish.TimeoutRetries);
        Assert.Equal([500, 502, 503], metadata.Configuration.DefaultPublish.HttpStatusRetries);
    }

    [Fact]
    public void Parse_AppliesDefaultsToPartialDeadLetterConfiguration()
    {
        FunctionMetadata metadata = FunctionMetadataParser.Parse(
            new Dictionary<string, string>
            {
                [FunctionAnnotationNames.Configuration] =
                    """{"DefaultAsync":{"HttpTimeout":30,"TimeoutRetries":[],"DeadLetterQueue":{"Enabled":true}}}"""
            }, "function");

        var policy = metadata.Configuration.DefaultAsync.DeadLetterQueue;
        Assert.True(policy.Enabled);
        Assert.Equal(604800, policy.MaxAgeSeconds);
        Assert.Equal(10000, policy.MaxLength);
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
}
