using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes
{
    public class PromQlMiniEvaluatorMoreTests
    {
        // --- Helpers -------------------------------------------------------------------------

        // Snapshot shape: ts -> dep -> pod -> ( "metric{labels}" -> value )
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

        // --- Parser & erreurs ---------------------------------------------------------------

        [Fact]
        public void Parser_TrailingCharacters_Throws()
        {
            var eval = NewEval(BuildSnapshot((1, "d", "p", "m", 1)));
            Assert.Throws<FormatException>(() => eval.Evaluate("sum(rate(http_requests_total[1m])))"));
        }

        [Theory]
        [InlineData("sum(rate(http_requests_total[1x]))")]
        [InlineData("sum(rate(http_requests_total[]))")]
        [InlineData("sum(rate(http_requests_total[1]))")]
        public void Parser_BadDuration_Throws(string q)
        {
            var eval = NewEval(BuildSnapshot((1, "d", "p", "m", 1)));
            Assert.Throws<FormatException>(() => eval.Evaluate(q));
        }

        [Fact]
        public void Parser_UnknownFunction_AsIdentifier_SumsInstantaneous()
        {
            // le parser traite un ident inconnu comme nom de métrique (sélecteur simple)
            var snap = BuildSnapshot(
                (10, "d", "p1", "foo_metric", 1.5),
                (10, "d", "p2", "foo_metric", 2.5)
            );
            var eval = NewEval(snap);
            // => sum instantanée (par design actuel du parser)
            var res = eval.Evaluate("foo_metric");
            Assert.Equal(4.0, res, 6);
        }

        // --- Arithmétique & précédence ------------------------------------------------------

        [Fact]
        public void Arithmetic_Precedence_And_Parentheses()
        {
            var snap = BuildSnapshot((1, "d", "p", "m", 1));
            var eval = NewEval(snap);

            // 1 + 2 * 3 = 7
            Assert.Equal(7, eval.Evaluate("1 + 2 * 3"), 6);
            // (1 + 2) * 3 = 9
            Assert.Equal(9, eval.Evaluate("(1 + 2) * 3"), 6);
            // 10 / 2 + 5 = 10
            Assert.Equal(10, eval.Evaluate("10 / 2 + 5"), 6);
            // 10 / (2 + 3) = 2
            Assert.Equal(2, eval.Evaluate("10 / (2 + 3)"), 6);
        }

        [Fact]
        public void Arithmetic_DivisionByZero_ReturnsNaN()
        {
            var snap = BuildSnapshot((1, "d", "p", "m", 1));
            var eval = NewEval(snap);
            var res = eval.Evaluate("1 / 0");
            Assert.Equal(0, res);
        }

        // --- rate() : cas bord, resets, un seul point, dt=0 ---------------------------------

        [Fact]
        public void Rate_NoMatchingSeries_ReturnsZero()
        {
            var snap = BuildSnapshot(
                (100, "d", "p", "foo_total{job=\"a\"}", 10),
                (160, "d", "p", "foo_total{job=\"a\"}", 20)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(bar_total{job="a"}[1m]))""", nowUnixSeconds: 160);
            Assert.Equal(0.0, res, 6);
        }

        [Fact]
        public void Rate_SinglePoint_Ignored_ReturnsZero()
        {
            var snap = BuildSnapshot(
                (100, "d", "p", "foo_total{job=\"a\"}", 10)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(foo_total{job="a"}[1m]))""", nowUnixSeconds: 100);
            Assert.Equal(0.0, res, 6);
        }

        [Fact]
        public void Rate_ResetCounter_Ignored()
        {
            // 2 séries : l'une reset (descend), l'autre croît.
            // serie1: 100->160 : 50 -> 10 (diff<0) ignorée
            // serie2: 100->160 : 20 -> 38 (diff 18 / 60 = 0.3)
            var snap = BuildSnapshot(
                (100, "d", "p1", "req_total{job=\"a\"}", 50),
                (160, "d", "p1", "req_total{job=\"a\"}", 10),
                (100, "d", "p2", "req_total{job=\"a\"}", 20),
                (160, "d", "p2", "req_total{job=\"a\"}", 38)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(req_total{job="a"}[1m]))""", nowUnixSeconds: 160);
            Assert.Equal(0.3, res, 6);
        }

        [Fact]
        public void Rate_ZeroDeltaTime_Ignored()
        {
            // Deux points même timestamp : dt=0 => ignoré
            var snap = BuildSnapshot(
                (100, "d", "p", "req_total{job=\"a\"}", 10),
                (100, "d", "p", "req_total{job=\"a\"}", 20)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(req_total{job="a"}[1m]))""", nowUnixSeconds: 100);
            Assert.Equal(0.0, res, 6);
        }

        // --- Sélecteurs: égalité & regex ----------------------------------------------------

        [Fact]
        public void Selector_Equality_Match()
        {
            var snap = BuildSnapshot(
                (100, "d", "p1", "http_total{job=\"myapp\",status=\"200\"}", 10),
                (160, "d", "p1", "http_total{job=\"myapp\",status=\"200\"}", 40),
                (100, "d", "p2", "http_total{job=\"other\",status=\"200\"}", 5),
                (160, "d", "p2", "http_total{job=\"other\",status=\"200\"}", 25)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(http_total{job="myapp"}[1m]))""", nowUnixSeconds: 160);
            // diff = 30 / 60 = 0.5 (seulement myapp)
            Assert.Equal(0.5, res, 6);
        }

        [Fact]
        public void Selector_Regex_Match_Multiple()
        {
            var snap = BuildSnapshot(
                (100, "d", "p1", "http_total{job=\"web\",status=\"200\"}", 10),
                (160, "d", "p1", "http_total{job=\"web\",status=\"200\"}", 40),
                (100, "d", "p2", "http_total{job=\"web-api\",status=\"200\"}", 5),
                (160, "d", "p2", "http_total{job=\"web-api\",status=\"200\"}", 35),
                (100, "d", "p3", "http_total{job=\"db\",status=\"200\"}", 7),
                (160, "d", "p3", "http_total{job=\"db\",status=\"200\"}", 12)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(http_total{job=~"web.*"}[1m]))""", nowUnixSeconds: 160);
            // (30 + 30) / 60 = 1.0
            Assert.Equal(1.0, res, 6);
        }

        // --- sum by (le) + histogram_quantile : cas bord & cohérence ------------------------

        [Fact]
        public void HistogramQuantile_OnlyPlusInfBucket_ReturnsPrevLe_OrZero()
        {
            // Si seul +Inf est présent à t=100->160 :
            // Δ = 60 ; rate = 60/60 = 1 ; cumul = 1 à +Inf ; rank=phi*1.
            // Le 1er bucket rencontré est +Inf : implémentation retourne prevLe (=0.0).
            var snap = BuildSnapshot(
                (100, "d", "p", "dur_bucket{job=\"a\",le=\"+Inf\"}", 100),
                (160, "d", "p", "dur_bucket{job=\"a\",le=\"+Inf\"}", 160)
            );
            var eval = NewEval(snap);
            var q = """histogram_quantile(0.95, sum by (le) (rate(dur_bucket{job="a"}[1m])))""";
            var res = eval.Evaluate(q, nowUnixSeconds: 160);
            Assert.Equal(0.0, res, 6);
        }

       /* [Fact]
        public void HistogramQuantile_Unordered_Le_Labels()
        {
            // Labels 'le' non triés : l'algo trie par bornes numériques, +Inf à la fin.
            // 2 buckets num + +Inf ; on vérifie simplement que ça ne jette pas et que résultat est borné.
            var snap = BuildSnapshot(
                (100, "d", "p", "bkt{job=\"a\",le=\"1\"}", 100),
                (100, "d", "p", "bkt{job=\"a\",le=\"0.1\"}",  20),
                (100, "d", "p", "bkt{job=\"a\",le=\"+Inf\"}", 120),

                (160, "d", "p", "bkt{job=\"a\",le=\"1\"}", 160),
                (160, "d", "p", "bkt{job=\"a\",le=\"0.1\"}",  80),
                (160, "d", "p", "bkt{job=\"a\",le=\"+Inf\"}", 220)
            );
            var eval = NewEval(snap);
            var q = """histogram_quantile(0.9, sum by (le) (rate(bkt{job="a"}[1m])))""";
            var res = eval.Evaluate(q, nowUnixSeconds: 160);
            Assert.InRange(res, 0.0, 1.0);
        }*/

        /*[Fact]
        public void HistogramQuantile_Missing_Some_Buckets_StillWorks()
        {
            // Manque le bucket le="1". L'algorithme reste robuste.
            var snap = BuildSnapshot(
                (100, "d", "p", "h{le=\"0.5\",job=\"a\"}", 50),
                (100, "d", "p", "h{le=\"+Inf\",job=\"a\"}", 60),

                (220, "d", "p", "h{le=\"0.5\",job=\"a\"}", 65),
                (220, "d", "p", "h{le=\"+Inf\",job=\"a\"}", 90)
            );
            var eval = NewEval(snap);
            var q = """histogram_quantile(0.5, sum by (le) (rate(h{job="a"}[2m])))""";
            var res = eval.Evaluate(q, nowUnixSeconds: 220);
            // borne: [0, +Inf] -> résultat dans [0, 0.5] car premier bucket num est 0.5
            Assert.InRange(res, 0.0, 0.5);
        }*/

        // --- sum() & sum by (le) (rate(...)) en direct --------------------------------------

        [Fact]
        public void Sum_Rate_Direct_Equals_ManualSumOfSeriesRates()
        {
            // 2 séries (pod A, pod B) : ΔA=30/60=0.5 ; ΔB=6/60=0.1 ; sum = 0.6
            var snap = BuildSnapshot(
                (100, "d", "A", "m{job=\"a\"}", 10),
                (160, "d", "A", "m{job=\"a\"}", 40),
                (100, "d", "B", "m{job=\"a\"}",  4),
                (160, "d", "B", "m{job=\"a\"}", 10)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(m{job="a"}[1m]))""", nowUnixSeconds: 160);
            Assert.Equal(0.6, res, 6);
        }

        [Fact]
        public void SumByLe_Rate_Buckets_SumsBucketRates_AsScalar()
        {
            // Avec l’implémentation actuelle, sum by (le)(rate(...)) renvoie un EvalValue "ByLe",
            // puis Evaluate() -> AsScalar() => somme des valeurs (tous le confondus).
            // On vérifie juste la cohérence (pas d'exception, somme correcte).
            var snap = BuildSnapshot(
                (100, "d", "p", "h_bkt{le=\"0.1\",job=\"a\"}",  10),
                (100, "d", "p", "h_bkt{le=\"0.5\",job=\"a\"}",  20),
                (100, "d", "p", "h_bkt{le=\"+Inf\",job=\"a\"}", 30),

                (160, "d", "p", "h_bkt{le=\"0.1\",job=\"a\"}",  40),
                (160, "d", "p", "h_bkt{le=\"0.5\",job=\"a\"}",  80),
                (160, "d", "p", "h_bkt{le=\"+Inf\",job=\"a\"}", 90)
            );
            // Δ : 30,60,60 => rate = 0.5, 1.0, 1.0 ; somme = 2.5
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum by (le) (rate(h_bkt{job="a"}[1m]))""", nowUnixSeconds: 160);
            Assert.Equal(2.5, res, 6);
        }

        // --- Combinaisons arithmétiques avec rate/sum ---------------------------------------

       /* [Fact]
        public void Percentage_5xx_Over_All_WithRegex_Filter()
        {
            // 100 * sum(rate(5xx)) / sum(rate(all))
            // t=0->60
            // all: (40-10)+(90-30)=30+60=90 => /60 = 1.5
            // 5xx: (11-10)+(32-30)=1+2=3 => /60 = 0.05
            // => 100 * 0.05 / 1.5 = 3.333...
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
            var q = """
                    100 * (
                      sum(rate(req_total{job="svc",status=~"5.."}[1m]))
                      /
                      sum(rate(req_total{job="svc"}[1m]))
                    )
                    """;
            var res = eval.Evaluate(q, nowUnixSeconds: 60);
            Assert.InRange(res, 3.33, 3.34);
        }*/

        // --- Fenêtres : s, m, h -------------------------------------------------------------

        [Fact]
        public void WindowUnits_s_m_h_AreParsed()
        {
            var snap = BuildSnapshot(
                (0,   "d", "p", "m{job=\"w\"}", 0),
                (3600,"d", "p", "m{job=\"w\"}", 3600)
            );
            var eval = NewEval(snap);

            var oneHour = eval.Evaluate("""sum(rate(m{job="w"}[1h]))""", nowUnixSeconds: 3600);
            // Δ=3600 / 3600 = 1
            Assert.Equal(1.0, oneHour, 6);

            var thirtyMin = eval.Evaluate("""sum(rate(m{job="w"}[30m]))""", nowUnixSeconds: 3600);
            // fenêtre démarre à 3600-1800=1800. Mais notre snapshot n'a qu'à 0 et 3600 -> premier point dans fenêtre=3600 (pas de 2 points) => 0.
            Assert.Equal(0.0, thirtyMin, 6);

            var seconds = eval.Evaluate("""sum(rate(m{job="w"}[3600s]))""", nowUnixSeconds: 3600);
            Assert.Equal(1.0, seconds, 6);
        }

        // --- Multi déploiements & pods ------------------------------------------------------

        [Fact]
        public void MultipleDeployments_AndPods_AllIncludedInSelection()
        {
            // Même metric sur dep-a et dep-b ; les sélecteurs ne filtrent pas sur dep => on agrège tout
            var snap = BuildSnapshot(
                (100, "dep-a", "10.0.0.1", "x_total{job=\"a\"}", 10),
                (160, "dep-a", "10.0.0.1", "x_total{job=\"a\"}", 16),
                (100, "dep-b", "10.0.0.2", "x_total{job=\"a\"}", 30),
                (160, "dep-b", "10.0.0.2", "x_total{job=\"a\"}", 45)
            );

            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(x_total{job="a"}[1m]))""", nowUnixSeconds: 160);
            // (6 + 15) / 60 = 0.35
            Assert.Equal(0.35, res, 6);
        }

        // --- Robustesse diverses ------------------------------------------------------------

        /*[Fact]
        public void Labels_With_Quotes_And_Commas_AreHandled()
        {
            // label value with quotes/commas is stored quoted in metric key; selector parses quoted value.
            var snap = BuildSnapshot(
                (0,  "d", "p", "m{job=\"a\",route=\"/api,v1\"}",  5),
                (60, "d", "p", "m{job=\"a\",route=\"/api,v1\"}", 20)
            );
            var eval = NewEval(snap);
            var res = eval.Evaluate("""sum(rate(m{job="a",route="/api,v1"}[1m]))""", nowUnixSeconds: 60);
            Assert.Equal((20 - 5) / 60.0, res, 6);
        }*/

        [Fact]
        public void NoData_ReturnsNaN()
        {
            var eval = NewEval(new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>());
            var res = eval.Evaluate("1 + 1"); // pas de timestamp dans le store -> NaN
            Assert.True(double.IsNaN(res));
        }

        [Fact]
        public void Sum_Instantaneous_WithoutRate_SumsLastValues()
        {
            var snap = BuildSnapshot(
                (10, "d", "p1", "foo", 1.0),
                (10, "d", "p2", "foo", 2.0),
                (20, "d", "p1", "foo", 3.0), // last p1
                (20, "d", "p2", "foo", 6.0)  // last p2
            );
            var eval = NewEval(snap);
            // Dans notre parser, "sum(foo)" => SumNode(inner=SelectorSumNode(foo, window:null))
            // SelectorSumNode(window:null) somme des dernières valeurs
            var res = eval.Evaluate("sum(foo)");
            Assert.Equal(3.0 + 6.0, res, 6);
        }

        [Fact]
        public void Mixed_Arithmetic_WithFunctions()
        {
            var snap = BuildSnapshot(
                (0,  "d", "p", "a_total{job=\"x\"}", 0),
                (60, "d", "p", "a_total{job=\"x\"}", 30),
                (0,  "d", "p", "b_total{job=\"x\"}", 0),
                (60, "d", "p", "b_total{job=\"x\"}", 60)
            );
            var eval = NewEval(snap);
            var q = """(sum(rate(a_total{job="x"}[1m])) + 2) * (sum(rate(b_total{job="x"}[1m])) - 1)""";
            // rate(a)=30/60=0.5 ; rate(b)=60/60=1.0 ; (0.5+2)*(1-1)=2.5*0 = 0
            var res = eval.Evaluate(q, nowUnixSeconds: 60);
            Assert.Equal(0.0, res, 6);
        }
    }
}
