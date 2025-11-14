using System;
using System.Collections.Generic;
using SlimFaas.MetricsQuery;
using Xunit;

namespace SlimFaas.Tests.MetricsQuery
{
    public class PromQlMiniEvaluatorTests
    {
            private const string AvgLatencyQuery =
                "sum(rate(http_request_duration_seconds_sum{code=\"200\",method=\"POST\",endpoint=\"/fibonacci\"}[1m])) " +
                "/ sum(rate(http_request_duration_seconds_count{code=\"200\",method=\"POST\",endpoint=\"/fibonacci\"}[1m]))";

            [Fact]
            public void Evaluate_ShouldReturnNaN_WhenNoSamples()
            {
                // Arrange : snapshot vide
                var snapshot = new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>();

                var evaluator = new PromQlMiniEvaluator(() => snapshot);

                // Act
                double result = evaluator.Evaluate(AvgLatencyQuery, nowUnixSeconds: null);

                // Assert
                Assert.True(double.IsNaN(result));
            }

            [Fact]
            public void Evaluate_ShouldReturnNaN_WhenDenominatorIsZero()
            {
                // Arrange :
                // On met des valeurs sur _sum mais aucune sur _count dans la fenêtre.
                var snapshot =
                    new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>();

                const string deployment = "slimfaas";
                const string podIp = "10.0.0.1";
                const string sumMetricName =
                    "http_request_duration_seconds_sum{code=\"200\",method=\"POST\",endpoint=\"/fibonacci\"}";

                // t = 100s et 160s mais uniquement pour _sum
                {
                    var metricsAtT1 = new Dictionary<string, double>
                    {
                        [sumMetricName] = 1.0
                    };

                    var podMetricsAtT1 = new Dictionary<string, IReadOnlyDictionary<string, double>>
                    {
                        [podIp] = metricsAtT1
                    };

                    var deploymentAtT1 = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
                    {
                        [deployment] = podMetricsAtT1
                    };

                    snapshot[100] = deploymentAtT1;
                }

                {
                    var metricsAtT2 = new Dictionary<string, double>
                    {
                        [sumMetricName] = 4.0
                    };

                    var podMetricsAtT2 = new Dictionary<string, IReadOnlyDictionary<string, double>>
                    {
                        [podIp] = metricsAtT2
                    };

                    var deploymentAtT2 = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
                    {
                        [deployment] = podMetricsAtT2
                    };

                    snapshot[160] = deploymentAtT2;
                }

                var evaluator = new PromQlMiniEvaluator(() => snapshot);

                // Act : on place "now" à 160 pour inclure [100,160] dans la fenêtre [1m]
                double result = evaluator.Evaluate(AvgLatencyQuery, nowUnixSeconds: 160);

                // Assert : denom = 0 => NaN
                Assert.True(double.IsNaN(result));
            }

            [Fact]
            public void Evaluate_ShouldReturnAverageLatency_WhenSamplesPresent()
            {
                // Arrange :
                // t = 100s : sum = 1, count = 1
                // t = 160s : sum = 4, count = 4
                //
                // rate(sum)   = (4 - 1) / 60 = 3/60
                // rate(count) = (4 - 1) / 60 = 3/60
                // moyenne     = rate(sum) / rate(count) = 1.0

                var snapshot = BuildSnapshotForAvgLatencyTest();
                var evaluator = new PromQlMiniEvaluator(() => snapshot);

                // Act :
                // 1) soit on laisse nowUnixSeconds = null -> Evaluate prendra max(ts) = 160
                // 2) soit on force explicitement 160
                double result = evaluator.Evaluate(AvgLatencyQuery, nowUnixSeconds: 160);

                // Assert
                Assert.Equal(1.0, result, precision: 6);
            }

            private static IReadOnlyDictionary<long,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string,
                        IReadOnlyDictionary<string, double>>>> BuildSnapshotForAvgLatencyTest()
            {
                var snapshot =
                    new Dictionary<long,
                        IReadOnlyDictionary<string,
                            IReadOnlyDictionary<string,
                                IReadOnlyDictionary<string, double>>>>();

                const string deployment = "slimfaas";
                const string podIp = "10.0.0.1";
                const string sumMetricName =
                    "http_request_duration_seconds_sum{code=\"200\",method=\"POST\",endpoint=\"/fibonacci\"}";
                const string countMetricName =
                    "http_request_duration_seconds_count{code=\"200\",method=\"POST\",endpoint=\"/fibonacci\"}";

                // t = 100s
                {
                    var metricsAtT1 = new Dictionary<string, double>
                    {
                        [sumMetricName] = 1.0,
                        [countMetricName] = 1.0
                    };

                    var podMetricsAtT1 = new Dictionary<string, IReadOnlyDictionary<string, double>>
                    {
                        [podIp] = metricsAtT1
                    };

                    var deploymentAtT1 = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
                    {
                        [deployment] = podMetricsAtT1
                    };

                    snapshot[100] = deploymentAtT1;
                }

                // t = 160s
                {
                    var metricsAtT2 = new Dictionary<string, double>
                    {
                        [sumMetricName] = 4.0,
                        [countMetricName] = 4.0
                    };

                    var podMetricsAtT2 = new Dictionary<string, IReadOnlyDictionary<string, double>>
                    {
                        [podIp] = metricsAtT2
                    };

                    var deploymentAtT2 = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
                    {
                        [deployment] = podMetricsAtT2
                    };

                    snapshot[160] = deploymentAtT2;
                }

                return snapshot;
            }
    }
}
