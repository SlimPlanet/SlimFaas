using SlimFaas.MetricsQuery;

namespace SlimFaas.Tests.Kubernetes
{
    public class PromQlMiniEvaluatorTests
    {
        // Helpers pour construire un snapshot: ts → dep → pod → metrics
        private static IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> BuildSnapshot(params (long ts, string dep, string pod, string metricKey, double value)[] points)
        {
            var store = new Dictionary<long, Dictionary<string, Dictionary<string, Dictionary<string, double>>>>();

            foreach (var p in points)
            {
                if (!store.TryGetValue(p.ts, out var depMap))
                    store[p.ts] = depMap = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(StringComparer.Ordinal);

                if (!depMap.TryGetValue(p.dep, out var podMap))
                    depMap[p.dep] = podMap = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

                if (!podMap.TryGetValue(p.pod, out var metrics))
                    podMap[p.pod] = metrics = new Dictionary<string, double>(StringComparer.Ordinal);

                metrics[p.metricKey] = p.value;
            }

            // lift vers IReadOnly…
            return store.ToDictionary(
                t => t.Key,
                t => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>)t.Value.ToDictionary(
                    d => d.Key,
                    d => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>)d.Value.ToDictionary(
                        p => p.Key,
                        p => (IReadOnlyDictionary<string, double>)p.Value.ToDictionary(m => m.Key, m => m.Value)
                    )
                )
            );
        }

        private static PromQlMiniEvaluator NewEval(IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> snapshot)
            => new PromQlMiniEvaluator(() => snapshot);

        [Fact]
        public void Sum_Rate_SimpleCounter()
        {
            // Série: http_server_requests_seconds_count{job="myapp"} sur 2 pods
            // t=100: pod1=10, pod2=30
            // t=160: pod1=16, pod2=45  -> Δ= (6+15)=21 / 60s = 0.35
            var snap = BuildSnapshot(
                (100L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\"}", 10.0),
                (100L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\"}", 30.0),
                (160L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\"}", 16.0),
                (160L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\"}", 45.0)
            );

            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(http_server_requests_seconds_count{job="myapp"}[1m]))""", nowUnixSeconds: 160);

            Assert.Equal(0.35, res, 6);
        }

        [Fact]
        public void Histogram_Quantile_95th_From_Buckets()
        {
            // _bucket cumulées (deux pods).
            // t=100:
            //   pod1: le=0.1:100, 0.5:200, 1:250, +Inf:300
            //   pod2: le=0.1: 80, 0.5:160, 1:260, +Inf:320
            // t=220:
            //   pod1:         160,     260,    310,      360   (Δ: 60,60,60,60)
            //   pod2:         140,     220,    290,      350   (Δ: 60,60,30,30)
            // rate par bucket (sum) sur 120s -> (Δ total)/120
            // le=0.1: (60+60)/120=1.0
            // le=0.5: (60+60)/120=1.0
            // le=1.0: (60+30)/120=0.75
            // +Inf:   (60+30)/120=0.75
            // total cumulatif final = +Inf = 0.75
            // φ=0.95 -> rank= 0.7125. Buckets cumulés:
            //   0.1: 1.0
            //   0.5: 2.0
            //   1.0: 2.75
            // => déjà >= rank dès 0.1 -> interpolation entre (0.0..0.1) (avec prevLe=0)
            // On retourne une valeur entre 0 et 0.1 ; notre implémentation renverra ~0.07125
            var snap = BuildSnapshot(
                (100L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.1\"}", 100.0),
                (100L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.5\"}", 200.0),
                (100L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"1\"}",   250.0),
                (100L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"+Inf\"}", 300.0),

                (100L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.1\"}",  80.0),
                (100L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.5\"}", 160.0),
                (100L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"1\"}",   260.0),
                (100L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"+Inf\"}",320.0),

                (220L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.1\"}", 160.0),
                (220L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.5\"}", 260.0),
                (220L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"1\"}",   310.0),
                (220L, "dep-a", "10.0.0.1", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"+Inf\"}",360.0),

                (220L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.1\"}", 140.0),
                (220L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"0.5\"}", 220.0),
                (220L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"1\"}",   290.0),
                (220L, "dep-a", "10.0.0.2", "http_server_request_duration_seconds_bucket{job=\"myapp\",le=\"+Inf\"}",350.0)
            );

            var eval = NewEval(snap);
            var q = """
                    histogram_quantile(
                      0.95,
                      sum by (le) (rate(http_server_request_duration_seconds_bucket{job="myapp"}[2m]))
                    )
                    """;
            var res = eval.Evaluate(q, nowUnixSeconds: 220);

            Assert.InRange(res, 0.06, 0.11); // ~0.071… selon l’interpolation réalisée
        }

        [Fact]
        public void Error_Rate_Percentage_5xx()
        {
            // 100 * sum(rate(5xx)) / sum(rate(all))
            // t=100 -> t=220 (120s)
            // pod1: all: 100->160 (Δ60), 5xx: 10->34 (Δ24)
            // pod2: all: 200->260 (Δ60), 5xx:  0-> 6 (Δ 6)
            // rate(all) = (60+60)/120 = 1.0
            // rate(5xx) = (24+6)/120  = 0.25
            // => 100 * 0.25 / 1.0 = 25 (%)
            var snap = BuildSnapshot(
                (100L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\",status=\"200\"}",  90.0),
                (100L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\",status=\"500\"}",  10.0),
                (100L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\",status=\"200\"}", 200.0),
                (100L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\",status=\"502\"}",   0.0),

                (220L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\",status=\"200\"}", 126.0),
                (220L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\",status=\"500\"}",  34.0),
                (220L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\",status=\"200\"}", 240.0),
                (220L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\",status=\"502\"}",   6.0)
            );

            var eval = NewEval(snap);

            var query = """
                        100 * (
                          sum(rate(http_server_requests_seconds_count{job="myapp",status=~"5.."}[2m]))
                          /
                          sum(rate(http_server_requests_seconds_count{job="myapp"}[2m]))
                        )
                        """;

            var res = eval.Evaluate(query, nowUnixSeconds: 220);
            Assert.InRange(res, 25, 30);
        }
    }
}
