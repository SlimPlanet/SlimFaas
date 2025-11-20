using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlimFaas.Kubernetes;

// Mini-évaluateur PromQL ciblé pour les cas d'usage fournis.
// Supporte : rate(), sum(), sum by(label)(), histogram_quantile(), arithmétique, sélecteurs {a="x",b=~"re"} et fenêtres [1m].
// Les résultats retournent un scalaire (double) pour les requêtes d'exemple.
// Pour d'autres usages, on peut étendre facilement (TODO).

public sealed class PromQlMiniEvaluator
{
    public delegate IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> SnapshotProvider();

    private readonly SnapshotProvider _snapshotProvider;

    public PromQlMiniEvaluator(SnapshotProvider snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
    }

    // Évalue la requête au temps "now" (Unix seconds, par défaut = dernier timestamp dispo)
    public double Evaluate(string query, long? nowUnixSeconds = null)
    {
        var snapshot = _snapshotProvider();
        Console.WriteLine($"Snapshot.Count {snapshot.Count}");
        if (snapshot.Count == 0)
            return double.NaN;

        long now = nowUnixSeconds ?? snapshot.Keys.Max();

        var ast = Parser.Parse(query);
        var ctx = new EvalContext(snapshot, now);
        var res = ast.Eval(ctx);
        var scalar = res.AsScalar();
        if (double.IsNaN(scalar) || double.IsInfinity(scalar))
            return 0.0;

        return scalar;
    }

    // --------- Eval model ---------
    private sealed class EvalContext
    {
        public readonly IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> Store;
        public readonly long Now;

        public EvalContext(IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> store, long now)
        {
            Store = store;
            Now = now;
        }

        // Renvoie toutes les séries (metricKey => SortedList<timestamp, value>) qui matchent le sélecteur.
        public Dictionary<string, SortedList<long, double>> SelectSeries(MetricSelector selector, TimeSpan? window = null)
        {
            long fromTs = window.HasValue ? Now - (long)window.Value.TotalSeconds : long.MinValue;

            // seriesKey -> (timestamp -> value)
            var series = new Dictionary<string, SortedList<long, double>>(StringComparer.Ordinal);

            foreach (var (ts, depMap) in Store)
            {
                if (ts < fromTs) continue;

                // depMap : deployment -> podMap
                foreach (var (_, podMap) in depMap)
                {
                    // podMap : podIp -> metrics
                    foreach (var (podIp, metrics) in podMap)
                    {
                        // metrics : metricKey -> double
                        foreach (var (metricKey, value) in metrics)
                        {
                            if (!TryParseMetricKey(metricKey, out var name, out var labels))
                                continue;

                            if (!selector.Match(name, labels))
                                continue;

                            // Clé de série = metric{labels triés} + podIp pour éviter l’écrasement inter-pods
                            var baseKey = BuildSeriesKey(name, labels);
                            var seriesKey = $"{baseKey}|pod={podIp}";

                            if (!series.TryGetValue(seriesKey, out var list))
                            {
                                list = new SortedList<long, double>();
                                series[seriesKey] = list;
                            }

                            // Si plusieurs valeurs au même ts pour ce pod, garder la dernière
                            if (!list.ContainsKey(ts))
                                list.Add(ts, value);
                            else
                                list[ts] = value;
                        }
                    }
                }
            }

            return series;
        }


        private static bool TryParseMetricKey(string key, out string name, out Dictionary<string, string> labels)
        {
            name = key;
            labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var i = key.IndexOf('{');
            if (i < 0) return true;

            var j = key.LastIndexOf('}');
            if (j < i) return false;

            name = key[..i];
            var content = key.Substring(i + 1, j - i - 1).Trim();
            if (string.IsNullOrEmpty(content)) return true;

            foreach (var pair in SplitLabels(content))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var k = pair[..eq].Trim();
                var v = pair[(eq + 1)..].Trim();
                // valeur potentiellement entre guillemets
                if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                    v = v.Substring(1, v.Length - 2);
                labels[k] = v;
            }
            return true;

            static IEnumerable<string> SplitLabels(string s)
            {
                // split par virgules non protégées (les valeurs PromQL sont simples ici)
                var sb = new StringBuilder();
                foreach (var c in s)
                {
                    if (c == ',')
                    {
                        yield return sb.ToString().Trim();
                        sb.Clear();
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                if (sb.Length > 0) yield return sb.ToString().Trim();
            }
        }

        private static string BuildSeriesKey(string name, Dictionary<string, string> labels)
        {
            if (labels.Count == 0) return name;
            var sb = new StringBuilder();
            sb.Append(name);
            sb.Append('{');
            bool first = true;
            foreach (var kv in labels.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(kv.Key);
                sb.Append("=\"");
                sb.Append(kv.Value.Replace("\"", "\\\""));
                sb.Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }
    }

    private abstract class ValueNode
    {
        public abstract EvalValue Eval(EvalContext ctx);
    }

    // Représente soit un scalaire, soit un map bucket "le" -> valeur (pour histogram_quantile)
    private readonly record struct EvalValue(double Scalar, Dictionary<string, double>? ByLe)
    {
        public bool IsScalar => ByLe is null;
        public double AsScalar() => IsScalar ? Scalar : (ByLe?.Values.Sum() ?? double.NaN);

        public static EvalValue FromScalar(double x) => new(x, null);
        public static EvalValue FromByLe(Dictionary<string, double> buckets) => new(double.NaN, buckets);
    }

    // --------- AST ---------
    private sealed class NumberNode(double value) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx) => EvalValue.FromScalar(value);
    }

    private sealed class BinaryNode(ValueNode left, string op, ValueNode right) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var a = left.Eval(ctx).AsScalar();
            var b = right.Eval(ctx).AsScalar();
            return op switch
            {
                "+" => EvalValue.FromScalar(a + b),
                "-" => EvalValue.FromScalar(a - b),
                "*" => EvalValue.FromScalar(a * b),
                "/" => EvalValue.FromScalar(SafeDiv(a, b)),
                _ => throw new InvalidOperationException($"Unknown operator {op}")
            };
        }
    }

    private sealed class MaxOverTimeNode(MetricSelector selector, TimeSpan window) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var series = ctx.SelectSeries(selector, window);

            double? globalMax = null;

            foreach (var sl in series.Values)
            {
                if (sl.Count == 0) continue;

                // Max des valeurs de CETTE série dans la fenêtre
                var localMax = sl.Values.Max();
                if (double.IsNaN(localMax)) continue;

                globalMax = globalMax is null
                    ? localMax
                    : Math.Max(globalMax.Value, localMax);
            }

            return EvalValue.FromScalar(globalMax ?? double.NaN);
        }
    }


    private static double SafeDiv(double a, double b)
    {
        // Si déjà NaN quelque part, on propage
        if (double.IsNaN(a) || double.IsNaN(b))
            return double.NaN;

        if (b == 0.0)
        {
            // Cas "pas de trafic" : 0 / 0 => 0
            if (a == 0.0)
                return 0.0;

            // Cas plus suspect : num != 0, denom = 0
            // À toi de choisir : +∞, 0, clamp à une valeur max…
            return double.PositiveInfinity;
        }

        return a / b;
    }

    private sealed class SumNode(ValueNode inner, string? byLabel) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var v = inner.Eval(ctx);
            if (v.IsScalar)
                return v;

            if (byLabel is null)
            {
                // sum(...) sans "by" => on renvoie un scalaire : somme de toutes les valeurs
                if (v.ByLe is null) return EvalValue.FromScalar(double.NaN);
                return EvalValue.FromScalar(v.ByLe.Values.Sum());
            }

            // ✅ sum by (le)(...) => on CONSERVE le dictionnaire par 'le'
            // (pour que min/max/avg puissent ensuite travailler dessus)
            return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));
        }

    }

    private sealed class HistogramQuantileNode(double phi, ValueNode bucketsExpr) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var v = bucketsExpr.Eval(ctx);
            if (v.IsScalar) return EvalValue.FromScalar(double.NaN);

            var buckets = v.ByLe!;
            // Convertit le -> upperBound (double)
            var points = new List<(double le, double count)>();
            foreach (var (k, c) in buckets)
            {
                if (string.Equals(k, "+Inf", StringComparison.OrdinalIgnoreCase))
                {
                    points.Add((double.PositiveInfinity, c));
                }
                else if (double.TryParse(k, NumberStyles.Float, CultureInfo.InvariantCulture, out var ub))
                {
                    points.Add((ub, c));
                }
            }
            points.Sort((a, b) => a.le.CompareTo(b.le));
            if (points.Count == 0) return EvalValue.FromScalar(double.NaN);

            // Prometheus histogram_quantile: compte cumulatif par bucket.
            // Ici "buckets" doivent déjà être des "rates" et "sum by (le)". On applique la méthode officielle.
            double rank = phi * points.Last().count;
            double prevLe = 0.0;
            double prevCount = 0.0;
            foreach (var (le, count) in points)
            {
                if (count >= rank)
                {
                    if (double.IsInfinity(le)) return EvalValue.FromScalar(prevLe); // extrapole
                    var bucketStart = prevLe;
                    var bucketEnd = le;
                    var bucketCount = count - prevCount;
                    if (bucketCount <= 0)
                        return EvalValue.FromScalar(bucketEnd); // dégradé

                    var posInBucket = (rank - prevCount) / bucketCount;
                    var q = bucketStart + (bucketEnd - bucketStart) * posInBucket;
                    return EvalValue.FromScalar(q);
                }
                prevLe = le;
                prevCount = count;
            }

            return EvalValue.FromScalar(points.Last().le);
        }
    }

    private sealed class MinNode(ValueNode inner, string? byLabel) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var v = inner.Eval(ctx);
            if (v.IsScalar) return v;

            if (byLabel is not null)
            {
                // même comportement que sum by (le) : on conserve le dictionnaire par 'le'
                return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));
            }

            if (v.ByLe is null || v.ByLe.Count == 0) return EvalValue.FromScalar(double.NaN);
            return EvalValue.FromScalar(v.ByLe.Values.Min());
        }
    }

    private sealed class MaxNode(ValueNode inner, string? byLabel) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var v = inner.Eval(ctx);
            if (v.IsScalar) return v;

            if (byLabel is not null)
            {
                // même comportement que sum by (le) : on conserve le dictionnaire par 'le'
                return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));
            }

            if (v.ByLe is null || v.ByLe.Count == 0) return EvalValue.FromScalar(double.NaN);
            return EvalValue.FromScalar(v.ByLe.Values.Max());
        }
    }


    private sealed class RateNode(MetricSelector selector, TimeSpan window) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var series = ctx.SelectSeries(selector, window);
            double sum = 0.0;

            foreach (var sl in series.Values)
            {
                if (sl.Count < 2) continue;
                var first = sl.First();
                var last = sl.Last();
                var dt = (double)(last.Key - first.Key);
                if (dt <= 0) continue;

                var diff = last.Value - first.Value;
                if (diff < 0) continue; // compteur reset -> ignore

                sum += diff / dt;
            }

            return EvalValue.FromScalar(sum);
        }
    }

    private sealed class RatePerBucketNode(MetricSelector selector, TimeSpan window, string bucketLabel) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            var series = ctx.SelectSeries(selector, window);

            // Agrège par label "le"
            var byLe = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (var (seriesKey, sl) in series)
            {
                if (!TryExtractLabel(seriesKey, bucketLabel, out var leValue))
                    continue;

                if (sl.Count < 2) continue;
                var first = sl.First();
                var last = sl.Last();
                var dt = (double)(last.Key - first.Key);
                if (dt <= 0) continue;

                var diff = last.Value - first.Value;
                if (diff < 0) continue;

                var rate = diff / dt;
                if (byLe.TryGetValue(leValue, out var acc))
                    byLe[leValue] = acc + rate;
                else
                    byLe[leValue] = rate;
            }

            return EvalValue.FromByLe(byLe);
        }

        private static bool TryExtractLabel(string seriesKey, string label, out string value)
        {
            value = "";
            var idx = seriesKey.IndexOf('{');
            if (idx < 0) return false;
            var j = seriesKey.LastIndexOf('}');
            if (j < idx) return false;
            var content = seriesKey.Substring(idx + 1, j - idx - 1);
            foreach (var part in content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var k = part[..eq];
                if (!string.Equals(k, label, StringComparison.Ordinal)) continue;
                var v = part[(eq + 1)..].Trim();
                if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                    v = v.Substring(1, v.Length - 2);
                value = v;
                return true;
            }
            return false;
        }
    }

    private sealed class SelectorSumNode(MetricSelector selector, TimeSpan? window) : ValueNode
    {
        public override EvalValue Eval(EvalContext ctx)
        {
            // sum(rate(..)) ou sum(metric instantanes) – Ici on cible uniquement les cas d'exemples : sum(rate(...))
            if (window is null)
            {
                // Somme des dernières valeurs (instantané) – non utilisé par tes exemples, mais on gère de base
                var series = ctx.SelectSeries(selector, window: null);
                double total = 0.0;
                foreach (var sl in series.Values)
                    if (sl.Count > 0) total += sl.Last().Value;
                return EvalValue.FromScalar(total);
            }
            else
            {
                // sum(rate(metric[win]))
                var rateNode = new RateNode(selector, window.Value);
                return rateNode.Eval(ctx);
            }
        }
    }

    // --------- Sélecteurs ---------
    private sealed class MetricSelector
    {
        public string MetricName { get; }
        public List<(string label, string? equal, Regex? regex)> Matchers { get; } = new();

        public MetricSelector(string name)
        {
            MetricName = name;
        }

        public bool Match(string name, Dictionary<string, string> labels)
        {
            if (!string.Equals(name, MetricName, StringComparison.Ordinal))
                return false;

            foreach (var (label, equal, regex) in Matchers)
            {
                if (!labels.TryGetValue(label, out var v))
                    return false;

                if (regex is not null)
                {
                    if (!regex.IsMatch(v)) return false;
                }
                else if (equal is not null)
                {
                    if (!string.Equals(v, equal, StringComparison.Ordinal)) return false;
                }
            }

            return true;
        }
    }

    // --------- Parser très ciblé (recursive descent) ---------
    private sealed class Parser
    {
        private readonly string _s;
        private int _pos;

        private Parser(string s) { _s = s; }

        public static ValueNode Parse(string s)
        {
            var p = new Parser(s);
            var node = p.ParseExpr();
            p.SkipWs();
            if (!p.Eof) throw new FormatException("Unexpected trailing characters in query");
            return node;
        }

        private bool Eof => _pos >= _s.Length;
        private char Cur => _s[_pos];

        private void SkipWs()
        {
            while (!Eof && char.IsWhiteSpace(Cur)) _pos++;
        }

        private bool Accept(string tok)
        {
            SkipWs();
            if (_s.AsSpan(_pos).StartsWith(tok, StringComparison.Ordinal))
            {
                _pos += tok.Length;
                return true;
            }
            return false;
        }

        private void Expect(string tok)
        {
            if (!Accept(tok)) throw new FormatException($"Expected '{tok}'");
        }

        private ValueNode ParseExpr() => ParseAdd();

        private ValueNode ParseAdd()
        {
            var left = ParseMul();
            while (true)
            {
                SkipWs();
                if (Accept("+"))
                {
                    var r = ParseMul();
                    left = new BinaryNode(left, "+", r);
                }
                else if (Accept("-"))
                {
                    var r = ParseMul();
                    left = new BinaryNode(left, "-", r);
                }
                else break;
            }
            return left;
        }

        private ValueNode ParseMul()
        {
            var left = ParseUnary();
            while (true)
            {
                SkipWs();
                if (Accept("*"))
                {
                    var r = ParseUnary();
                    left = new BinaryNode(left, "*", r);
                }
                else if (Accept("/"))
                {
                    var r = ParseUnary();
                    left = new BinaryNode(left, "/", r);
                }
                else break;
            }
            return left;
        }

        private ValueNode ParseUnary()
        {
            SkipWs();
            if (Accept("("))
            {
                var e = ParseExpr();
                Expect(")");
                return e;
            }

            if (char.IsDigit(Cur) || Cur == '.')
            {
                var num = ParseNumber();
                return new NumberNode(num);
            }

            // functions or selectors or aggregation
            var ident = ParseIdent();

            // histogram_quantile(φ, expr)
            if (string.Equals(ident, "histogram_quantile", StringComparison.OrdinalIgnoreCase))
            {
                Expect("(");
                var phi = ParseNumber();
                Expect(",");
                var inner = ParseExpr();
                Expect(")");
                return new HistogramQuantileNode(phi, inner);
            }

            // max_over_time(metric{...}[win])
            if (string.Equals(ident, "max_over_time", StringComparison.OrdinalIgnoreCase))
            {
                Expect("(");
                var (sel, win) = ParseSelectorWithRange();
                Expect(")");
                return new MaxOverTimeNode(sel, win);
            }

            // sum by (label) (expr)  |  sum(expr)
            if (string.Equals(ident, "sum", StringComparison.OrdinalIgnoreCase))
            {
                SkipWs();
                string? byLabel = null;
                if (Accept("by"))
                {
                    SkipWs();
                    Expect("(");
                    byLabel = ParseIdent();
                    Expect(")");
                }
                SkipWs();
                Expect("(");
                SkipWs();

                // ✅ Spécial : si on a "sum by (le) ( rate( ... ) )"
                // alors on force un RatePerBucketNode regroupé par ce label,
                // même si le nom de métrique ne se termine pas par "_bucket".
                if (byLabel is not null && _s.AsSpan(_pos).StartsWith("rate", StringComparison.Ordinal))
                {
                    Accept("rate");
                    Expect("(");
                    var (sel, win) = ParseSelectorWithRange();
                    Expect(")");
                    Expect(")"); // ferme sum(
                    return new SumNode(new RatePerBucketNode(sel, win, byLabel), byLabel);
                }

                // Cas général inchangé
                var inner = ParseExpr();
                Expect(")");
                return new SumNode(inner, byLabel);
            }

            // rate(metric{..}[win])
            if (string.Equals(ident, "rate", StringComparison.OrdinalIgnoreCase))
            {
                Expect("(");
                var (sel, win) = ParseSelectorWithRange();
                Expect(")");
                // Cas particulier histogram : on aura sum by (le) (rate(bucket[...]))
                if (sel.MetricName.EndsWith("_bucket", StringComparison.Ordinal))
                    return new RatePerBucketNode(sel, win, "le");
                return new RateNode(sel, win);
            }

            // min by (label) (expr)  |  min(expr [, expr2, ...])
            if (string.Equals(ident, "min", StringComparison.OrdinalIgnoreCase))
            {
                SkipWs();
                string? byLabel = null;
                if (Accept("by"))
                {
                    SkipWs();
                    Expect("(");
                    byLabel = ParseIdent();
                    Expect(")");
                }
                SkipWs();
                Expect("(");

                // Parse un ou plusieurs arguments séparés par des virgules
                var args = new List<ValueNode>();
                do
                {
                    var e = ParseExpr();
                    args.Add(e);
                    SkipWs();
                }
                while (Accept(",")); // consomme toutes les virgules intermédiaires

                Expect(")"); // ferme min(

                if (args.Count > 1)
                {
                    // Variadique autorisé seulement sans "by (...)"
                    if (byLabel is not null)
                        throw new FormatException("min by (...) with multiple arguments is not supported");
                    return new VariadicMinNode(args);
                }

                // Cas 1 arg : comportement précédent
                return new MinNode(args[0], byLabel);
            }

// max by (label) (expr)  |  max(expr [, expr2, ...])
            if (string.Equals(ident, "max", StringComparison.OrdinalIgnoreCase))
            {
                SkipWs();
                string? byLabel = null;
                if (Accept("by"))
                {
                    SkipWs();
                    Expect("(");
                    byLabel = ParseIdent();
                    Expect(")");
                }
                SkipWs();
                Expect("(");

                var args = new List<ValueNode>();
                do
                {
                    var e = ParseExpr();
                    args.Add(e);
                    SkipWs();
                }
                while (Accept(","));

                Expect(")");

                if (args.Count > 1)
                {
                    if (byLabel is not null)
                        throw new FormatException("max by (...) with multiple arguments is not supported");
                    return new VariadicMaxNode(args);
                }

                return new MaxNode(args[0], byLabel);
            }

            // avg by (label) (expr)  |  avg(expr)
// cas spécial : si on a "avg by (le) ( rate(metric{..}[win]) )"
// => on force RatePerBucketNode(..., le) puis AvgNode(byLabel) pour préserver {le -> valeur}
            if (string.Equals(ident, "avg", StringComparison.OrdinalIgnoreCase))
            {
                SkipWs();
                string? byLabel = null;
                if (Accept("by"))
                {
                    SkipWs();
                    Expect("(");
                    byLabel = ParseIdent();
                    Expect(")");
                }
                SkipWs();
                Expect("(");
                SkipWs();

                // ✅ Spécial : avg by (le) ( rate(...) )
                if (byLabel is not null && _s.AsSpan(_pos).StartsWith("rate", StringComparison.Ordinal))
                {
                    Accept("rate");
                    Expect("(");
                    var (sel, win) = ParseSelectorWithRange();
                    Expect(")");
                    Expect(")"); // ferme avg(
                    return new AvgNode(new RatePerBucketNode(sel, win, byLabel), byLabel);
                }

                // ✅ Spécial : avg(rate(...)) sans "by" => moyenne des débits par série
                if (byLabel is null && _s.AsSpan(_pos).StartsWith("rate", StringComparison.Ordinal))
                {
                    Accept("rate");
                    Expect("(");
                    var (sel, win) = ParseSelectorWithRange();
                    Expect(")");
                    Expect(")");
                    return new AvgRateNode(sel, win);
                }

                // Cas général : avg(inner)
                var inner = ParseExpr();
                Expect(")");
                return new AvgNode(inner, byLabel);
            }

            // Si on arrive ici : ident est un metricName => parse sélecteur + option range ? (pour sum(rate(...)) on ne vient pas là)
            var selector = ParseOptionalSelectorAfterKnownName(ident);
            // Par défaut, on fera sum instantané des séries (non utilisé dans tes exemples).
            return new SelectorSumNode(selector, window: null);
        }

        private sealed class VariadicMinNode : ValueNode
        {
            private readonly List<ValueNode> _args;
            public VariadicMinNode(List<ValueNode> args) => _args = args;

            public override EvalValue Eval(EvalContext ctx)
            {
                double? acc = null;
                foreach (var a in _args)
                {
                    var v = a.Eval(ctx).AsScalar();
                    if (double.IsNaN(v)) continue;
                    acc = acc is null ? v : Math.Min(acc.Value, v);
                }
                return EvalValue.FromScalar(acc ?? double.NaN);
            }
        }

        private sealed class VariadicMaxNode : ValueNode
        {
            private readonly List<ValueNode> _args;
            public VariadicMaxNode(List<ValueNode> args) => _args = args;

            public override EvalValue Eval(EvalContext ctx)
            {
                double? acc = null;
                foreach (var a in _args)
                {
                    var v = a.Eval(ctx).AsScalar();
                    if (double.IsNaN(v)) continue;
                    acc = acc is null ? v : Math.Max(acc.Value, v);
                }
                return EvalValue.FromScalar(acc ?? double.NaN);
            }
        }


        private sealed class AvgNode(ValueNode inner, string? byLabel) : ValueNode
        {
            public override EvalValue Eval(EvalContext ctx)
            {
                var v = inner.Eval(ctx);
                if (v.IsScalar) return v;

                if (byLabel is not null)
                {
                    // même logique que sum/min/max : on conserve le dictionnaire par 'le'
                    return EvalValue.FromByLe(new Dictionary<string, double>(v.ByLe!, StringComparer.Ordinal));
                }

                if (v.ByLe is null || v.ByLe.Count == 0) return EvalValue.FromScalar(double.NaN);
                return EvalValue.FromScalar(v.ByLe.Values.Average());
            }
        }

// avg(rate(metric{...}[win])) : moyenne des débits par série (pod)
        private sealed class AvgRateNode(MetricSelector selector, TimeSpan window) : ValueNode
        {
            public override EvalValue Eval(EvalContext ctx)
            {
                var series = ctx.SelectSeries(selector, window);
                double sum = 0.0;
                int count = 0;

                foreach (var sl in series.Values)
                {
                    if (sl.Count < 2) continue;
                    var first = sl.First();
                    var last = sl.Last();
                    var dt = (double)(last.Key - first.Key);
                    if (dt <= 0) continue;

                    var diff = last.Value - first.Value;
                    if (diff < 0) continue; // reset

                    sum += diff / dt;
                    count++;
                }

                if (count == 0) return EvalValue.FromScalar(0.0);
                return EvalValue.FromScalar(sum / count);
            }
        }


        private double ParseNumber()
        {
            SkipWs();
            int start = _pos;
            bool dot = false;
            if (!Eof && (Cur == '+' || Cur == '-')) _pos++;
            while (!Eof)
            {
                if (char.IsDigit(Cur)) { _pos++; continue; }
                if (Cur == '.' && !dot) { dot = true; _pos++; continue; }
                break;
            }
            var s = _s[start.._pos];
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new FormatException($"Invalid number '{s}'");
            return d;
        }

        private string ParseIdent()
        {
            SkipWs();
            int start = _pos;
            if (Eof || !(char.IsLetter(Cur) || Cur == '_' || Cur == ':'))
                throw new FormatException("Identifier expected");
            _pos++;
            while (!Eof && (char.IsLetterOrDigit(Cur) || Cur == '_' || Cur == ':')) _pos++;
            return _s[start.._pos];
        }

        private MetricSelector ParseOptionalSelectorAfterKnownName(string metricName)
        {
            var sel = new MetricSelector(metricName);
            SkipWs();
            if (Accept("{"))
            {
                while (true)
                {
                    SkipWs();
                    if (Accept("}")) break;
                    var label = ParseIdent();
                    SkipWs();
                    string op = "=";
                    if (Accept("=~")) op = "=~";
                    else Expect("=");
                    SkipWs();
                    var value = ParseQuoted();
                    if (op == "=~")
                        sel.Matchers.Add((label, null, new Regex($"^{value}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50))));
                    else
                        sel.Matchers.Add((label, value, null));
                    SkipWs();
                    if (Accept(",")) continue;
                    Expect("}");
                    break;
                }
            }
            // pas de range ici
            return sel;
        }

        private (MetricSelector selector, TimeSpan window) ParseSelectorWithRange()
        {
            // metricName { .. } [window]
            var metricName = ParseIdent();
            var sel = ParseOptionalSelectorAfterKnownName(metricName);
            SkipWs();
            Expect("[");
            var win = ParseDuration();
            Expect("]");
            return (sel, win);
        }

        private string ParseQuoted()
        {
            SkipWs();
            if (!Accept("\"")) throw new FormatException("Expected '\"'");
            var sb = new StringBuilder();
            while (!Eof)
            {
                var c = Cur; _pos++;
                if (c == '"') break;
                if (c == '\\' && !Eof)
                {
                    var n = Cur; _pos++;
                    sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private TimeSpan ParseDuration()
        {
            SkipWs();
            int start = _pos;
            while (!Eof && (char.IsDigit(Cur))) _pos++;
            if (start == _pos) throw new FormatException("Duration number expected");
            var numStr = _s[start.._pos];
            if (!int.TryParse(numStr, out var n)) throw new FormatException("Invalid duration");

            SkipWs();
            if (Eof) throw new FormatException("Duration unit expected");
            var unit = Cur; _pos++;

            return unit switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                _ => throw new FormatException("Unsupported duration unit (use s|m|h)")
            };
        }
    }
}
