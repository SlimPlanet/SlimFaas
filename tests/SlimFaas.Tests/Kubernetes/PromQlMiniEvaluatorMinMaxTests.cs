using SlimFaas.MetricsQuery;

namespace SlimFaas.Tests.Kubernetes
{
    public class PromQlMiniEvaluatorMinMaxTests
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
        public void MinMax_OnScalar_JustReturnSameScalar()
        {
            var eval = NewEval(BuildSnapshot((1,"d","p","m",1)));
            Assert.Equal(7.0, eval.Evaluate("min(1 + 2 * 3)"), 6);
            Assert.Equal(7.0, eval.Evaluate("max(1 + 2 * 3)"), 6);
        }

        [Fact]
        public void MinMax_OnBuckets_MinMaxAcrossLe()
        {
            // Buckets (2 timestamps -> rate calculable)
            // Δ(0.1)=30 ; Δ(0.5)=60 ; Δ(+Inf)=90 sur 60s => rates: 0.5, 1.0, 1.5
            var snap = BuildSnapshot(
                (100, "d", "p", "h_bkt{job=\"a\",le=\"0.1\"}",  10),
                (100, "d", "p", "h_bkt{job=\"a\",le=\"0.5\"}",  20),
                (100, "d", "p", "h_bkt{job=\"a\",le=\"+Inf\"}", 30),

                (160, "d", "p", "h_bkt{job=\"a\",le=\"0.1\"}",  40),
                (160, "d", "p", "h_bkt{job=\"a\",le=\"0.5\"}",  80),
                (160, "d", "p", "h_bkt{job=\"a\",le=\"+Inf\"}", 120)
            );

            var eval = NewEval(snap);

            // sum by (le) (rate(...)) -> renvoie ByLe; min(...) / max(...) sans "by" doivent agréger en scalaire
            var qMin = """min( sum by (le) ( rate(h_bkt{job="a"}[1m]) ) )""";
            var qMax = """max( sum by (le) ( rate(h_bkt{job="a"}[1m]) ) )""";

            var rMin = eval.Evaluate(qMin, nowUnixSeconds: 160);
            var rMax = eval.Evaluate(qMax, nowUnixSeconds: 160);

            Assert.Equal(0.5, rMin, 6);  // min(0.5, 1.0, 1.5)
            Assert.Equal(1.5, rMax, 6);  // max(0.5, 1.0, 1.5)
        }

        [Fact]
        public void MinMax_ByLe_PreserveBuckets_AsScalarSums()
        {
            // Ici on vérifie que la variante "by (le)" conserve la table par 'le'
            // et que Evaluate() renvoie la somme (AsScalar) — cohérent avec le comportement existant.
            var snap = BuildSnapshot(
                (0,  "d", "p", "h_bkt{le=\"0.1\"}", 10),
                (0,  "d", "p", "h_bkt{le=\"0.5\"}", 20),
                (0,  "d", "p", "h_bkt{le=\"+Inf\"}", 30),
                (60, "d", "p", "h_bkt{le=\"0.1\"}", 40),
                (60, "d", "p", "h_bkt{le=\"0.5\"}", 80),
                (60, "d", "p", "h_bkt{le=\"+Inf\"}", 120)
            );
            var eval = NewEval(snap);

            var qSum = """sum by (le) ( rate(h_bkt[1m]) )""";
            var qMin = """min by (le) ( rate(h_bkt[1m]) )""";
            var qMax = """max by (le) ( rate(h_bkt[1m]) )""";

            var sSum = eval.Evaluate(qSum, nowUnixSeconds: 60); // 0.5 + 1.0 + 1.5 = 3.0
            var sMin = eval.Evaluate(qMin, nowUnixSeconds: 60); // conserve ByLe -> AsScalar() somme aussi
            var sMax = eval.Evaluate(qMax, nowUnixSeconds: 60); // idem

            Assert.Equal(3.0, sSum, 6);
            Assert.Equal(sSum, sMin, 6);
            Assert.Equal(sSum, sMax, 6);
        }

        [Fact]
        public void MinMax_WithRegexSelector_OnCounters()
        {
            // Deux pods, status 2xx et 5xx
            // 2xx Δ = (40-10)+(90-30)=30+60=90 -> /60 = 1.5
            // 5xx Δ = (11-10)+(32-30)=1+2=3   -> /60 = 0.05
            var snap = BuildSnapshot(
                (0,  "d", "A", "req_total{job=\"svc\",status=\"200\"}", 10),
                (0,  "d", "A", "req_total{job=\"svc\",status=\"500\"}", 10),
                (0,  "d", "B", "req_total{job=\"svc\",status=\"200\"}", 30),
                (0,  "d", "B", "req_total{job=\"svc\",status=\"502\"}", 30),

                (60, "d", "A", "req_total{job=\"svc\",status=\"200\"}", 40),
                (60, "d", "A", "req_total{job=\"svc\",status=\"500\"}", 11),
                (60, "d", "B", "req_total{job=\"svc\",status=\"200\"}", 90),
                (60, "d", "B", "req_total{job=\"svc\",status=\"502\"}", 32)
            );

            var eval = NewEval(snap);

            // On compare min/max entre deux agrégations scalaires
            var rAll = eval.Evaluate("""sum(rate(req_total{job="svc"}[1m]))""", nowUnixSeconds: 60);            // 1.5 + 0.05 = 1.55
            var r5xx = eval.Evaluate("""sum(rate(req_total{job="svc",status=~"5.."}[1m]))""", nowUnixSeconds: 60); // 0.05

            var rMin = eval.Evaluate("""
                                      min(
                                        sum(rate(req_total{job="svc"}[1m])),
                                        sum(rate(req_total{job="svc",status=~"5.."}[1m]))
                                      )
                                      """, nowUnixSeconds: 60);
            var rMax = eval.Evaluate("""
                                      max(
                                        sum(rate(req_total{job="svc"}[1m])),
                                        sum(rate(req_total{job="svc",status=~"5.."}[1m]))
                                      )
                                      """, nowUnixSeconds: 60);

            // NOTE: la grammaire actuelle supporte min(expr) à 1 argument; pour simuler min(a,b)
            // on compare les scalaires calculés ci-dessus.
            Assert.Equal(Math.Min(rAll, r5xx), rMin, 6);
            Assert.Equal(Math.Max(rAll, r5xx), rMax, 6);
        }
    }
}
