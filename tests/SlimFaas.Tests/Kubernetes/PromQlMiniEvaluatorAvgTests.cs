using SlimFaas.MetricsQuery;

namespace SlimFaas.Tests.Kubernetes
{
    public class PromQlMiniEvaluatorAvgTests
    {
        private static IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> BuildSnapshot(
            params (long ts, string dep, string pod, string key, double value)[] points)
        {
            var store = new Dictionary<long, Dictionary<string, Dictionary<string, Dictionary<string, double>>>>();

            foreach (var p in points)
            {
                if (!store.TryGetValue(p.ts, out var depMap))
                    depMap = store[p.ts] = new(StringComparer.Ordinal);

                if (!depMap.TryGetValue(p.dep, out var podMap))
                    podMap = depMap[p.dep] = new(StringComparer.Ordinal);

                if (!podMap.TryGetValue(p.pod, out var metrics))
                    metrics = podMap[p.pod] = new(StringComparer.Ordinal);

                metrics[p.key] = p.value;
            }

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
        public void Avg_OnScalar_ReturnsSameScalar()
        {
            var eval = NewEval(BuildSnapshot((1,"d","p","m",1)));
            Assert.Equal(3.0, eval.Evaluate("avg(1 + 2)"), 6);
        }

        [Fact]
        public void Avg_Rate_Per_Series_Mean()
        {
            // Deux pods, même metric
            // pod1: 10 -> 16 (Δ6/60=0.1)
            // pod2: 30 -> 45 (Δ15/60=0.25)
            // avg = (0.1 + 0.25) / 2 = 0.175
            var snap = BuildSnapshot(
                (100L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\"}", 10.0),
                (100L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\"}", 30.0),
                (160L, "dep-a", "10.0.0.1", "http_server_requests_seconds_count{job=\"myapp\"}", 16.0),
                (160L, "dep-a", "10.0.0.2", "http_server_requests_seconds_count{job=\"myapp\"}", 45.0)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""avg(rate(http_server_requests_seconds_count{job="myapp"}[1m]))""", nowUnixSeconds: 160);
            Assert.Equal(0.175, res, 6);
        }

        [Fact]
        public void Avg_Rate_Ignores_Reset_And_SinglePoint()
        {
            // p1: reset (diff<0) -> ignoré
            // p2: un seul point -> ignoré
            // p3: 20->38 (Δ18/60=0.3) => avg sur 1 série = 0.3
            var snap = BuildSnapshot(
                (100, "d", "p1", "req_total{job=\"a\"}", 50),
                (160, "d", "p1", "req_total{job=\"a\"}", 10),

                (160, "d", "p2", "req_total{job=\"a\"}",  5),

                (100, "d", "p3", "req_total{job=\"a\"}", 20),
                (160, "d", "p3", "req_total{job=\"a\"}", 38)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""avg(rate(req_total{job="a"}[1m]))""", nowUnixSeconds: 160);
            Assert.Equal(0.3, res, 6);
        }

        [Fact]
        public void Avg_Over_ByLe_Computes_Mean_Of_Values()
        {
            // Buckets sur 60s : Δ(0.1)=30 -> 0.5 ; Δ(0.5)=60 -> 1.0 ; Δ(+Inf)=90 -> 1.5
            // avg (sur les valeurs 0.5, 1.0, 1.5) = (3.0 / 3) = 1.0
            var snap = BuildSnapshot(
                (0,  "d", "p", "h_bkt{job=\"a\",le=\"0.1\"}", 10),
                (0,  "d", "p", "h_bkt{job=\"a\",le=\"0.5\"}", 20),
                (0,  "d", "p", "h_bkt{job=\"a\",le=\"+Inf\"}", 30),

                (60, "d", "p", "h_bkt{job=\"a\",le=\"0.1\"}", 40),
                (60, "d", "p", "h_bkt{job=\"a\",le=\"0.5\"}", 80),
                (60, "d", "p", "h_bkt{job=\"a\",le=\"+Inf\"}", 120)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""avg( sum by (le) ( rate(h_bkt{job="a"}[1m]) ) )""", nowUnixSeconds: 60);
            Assert.Equal(1.0, res, 6);
        }

        [Fact]
        public void Avg_ByLe_Preserves_Dictionary_AsScalarSum()
        {
            // Dans notre mini-implémentation, avg by (le) (...) conserve le dictionnaire {le->valeur}.
            // Evaluate() -> AsScalar() => somme (cohérent avec sum/min/max by (le) déjà fournis).
            var snap = BuildSnapshot(
                (0,  "d", "p", "h_bkt{le=\"0.1\"}", 10),
                (0,  "d", "p", "h_bkt{le=\"0.5\"}", 20),
                (0,  "d", "p", "h_bkt{le=\"+Inf\"}", 30),
                (60, "d", "p", "h_bkt{le=\"0.1\"}", 40),
                (60, "d", "p", "h_bkt{le=\"0.5\"}", 80),
                (60, "d", "p", "h_bkt{le=\"+Inf\"}", 120)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""avg by (le) ( rate(h_bkt[1m]) )""", nowUnixSeconds: 60);
            // somme des rates: 0.5 + 1.0 + 1.5 = 3.0 (AsScalar)
            Assert.Equal(3.0, res, 6);
        }
    }
}
