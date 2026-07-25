using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlimFaas.Kubernetes;

/// <summary>
/// An immutable compiled PromQL query ready for repeated evaluation without re-parsing.
/// </summary>
public sealed class CompiledPromQlQuery
{
    internal ValueNode Root { get; }

    /// <summary>
    /// The exact set of metric names referenced by selectors in this query.
    /// Populated by the compiler from the AST; never derived by regex.
    /// </summary>
    public IReadOnlySet<string> ReferencedMetricNames { get; }

    internal CompiledPromQlQuery(ValueNode root, IReadOnlySet<string> referencedMetricNames)
    {
        Root = root;
        ReferencedMetricNames = referencedMetricNames;
    }
}

/// <summary>
/// Compiles PromQL query strings into immutable <see cref="CompiledPromQlQuery"/> plans.
/// </summary>
public static class PromQlQueryCompiler
{
    /// <summary>
    /// Parses and compiles <paramref name="query"/>.
    /// </summary>
    /// <exception cref="FormatException">The query is syntactically invalid.</exception>
    public static CompiledPromQlQuery Compile(string query)
    {
        var root = PromQlParser.Parse(query);
        var names = new HashSet<string>(StringComparer.Ordinal);
        root.CollectMetricNames(names);
        return new CompiledPromQlQuery(root, names);
    }

    /// <summary>
    /// Validates <paramref name="query"/> without throwing.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the query is valid; an error message otherwise.
    /// </returns>
    public static string? Validate(string query)
    {
        try
        {
            Compile(query);
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }
}

// Recursive-descent parser that produces a ValueNode AST.
internal sealed class PromQlParser
{
    private readonly string _s;
    private int _pos;

    private PromQlParser(string s) => _s = s;

    public static ValueNode Parse(string s)
    {
        var p = new PromQlParser(s);
        var node = p.ParseExpr();
        p.SkipWs();
        if (!p.Eof) throw new FormatException("Unexpected trailing characters in query");
        return node;
    }

    private bool Eof => _pos >= _s.Length;
    private char Cur => Eof ? '\0' : _s[_pos];

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
                left = new BinaryNode(left, "+", ParseMul());
            else if (Accept("-"))
                left = new BinaryNode(left, "-", ParseMul());
            else
                break;
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
                left = new BinaryNode(left, "*", ParseUnary());
            else if (Accept("/"))
                left = new BinaryNode(left, "/", ParseUnary());
            else
                break;
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
            return new NumberNode(ParseNumber());

        var ident = ParseIdent();

        if (string.Equals(ident, "histogram_quantile", StringComparison.OrdinalIgnoreCase))
        {
            Expect("(");
            var phi = ParseNumber();
            Expect(",");
            var inner = ParseExpr();
            Expect(")");
            return new HistogramQuantileNode(phi, inner);
        }

        if (string.Equals(ident, "max_over_time", StringComparison.OrdinalIgnoreCase))
        {
            Expect("(");
            var (sel, win) = ParseSelectorWithRange();
            Expect(")");
            return new MaxOverTimeNode(sel, win);
        }

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

            // Special: sum by (le) (rate(...)) → RatePerBucketNode grouped by that label.
            if (byLabel is not null && _s.AsSpan(_pos).StartsWith("rate", StringComparison.Ordinal))
            {
                Accept("rate");
                Expect("(");
                var (sel, win) = ParseSelectorWithRange();
                Expect(")");
                Expect(")"); // close sum(
                return new SumNode(new RatePerBucketNode(sel, win, byLabel), byLabel);
            }

            var inner = ParseExpr();
            Expect(")");
            return new SumNode(inner, byLabel);
        }

        if (string.Equals(ident, "rate", StringComparison.OrdinalIgnoreCase))
        {
            Expect("(");
            var (sel, win) = ParseSelectorWithRange();
            Expect(")");
            // Histogram bucket metric: group by "le" automatically.
            if (sel.MetricName.EndsWith("_bucket", StringComparison.Ordinal))
                return new RatePerBucketNode(sel, win, "le");
            return new RateNode(sel, win);
        }

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
            var args = ParseArgList();
            Expect(")");

            if (args.Count > 1)
            {
                if (byLabel is not null)
                    throw new FormatException("min by (...) with multiple arguments is not supported");
                return new VariadicMinNode(args);
            }

            return new MinNode(args[0], byLabel);
        }

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
            var args = ParseArgList();
            Expect(")");

            if (args.Count > 1)
            {
                if (byLabel is not null)
                    throw new FormatException("max by (...) with multiple arguments is not supported");
                return new VariadicMaxNode(args);
            }

            return new MaxNode(args[0], byLabel);
        }

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

            // Special: avg by (le) (rate(...)) → preserves bucket dict for histogram_quantile.
            if (byLabel is not null && _s.AsSpan(_pos).StartsWith("rate", StringComparison.Ordinal))
            {
                Accept("rate");
                Expect("(");
                var (sel, win) = ParseSelectorWithRange();
                Expect(")");
                Expect(")"); // close avg(
                return new AvgNode(new RatePerBucketNode(sel, win, byLabel), byLabel);
            }

            // Special: avg(rate(...)) without "by" → mean of per-series rates.
            if (byLabel is null && _s.AsSpan(_pos).StartsWith("rate", StringComparison.Ordinal))
            {
                Accept("rate");
                Expect("(");
                var (sel, win) = ParseSelectorWithRange();
                Expect(")");
                Expect(")"); // close avg(
                return new AvgRateNode(sel, win);
            }

            var inner = ParseExpr();
            Expect(")");
            return new AvgNode(inner, byLabel);
        }

        // Bare metric name (possibly with label selector) used as an instant vector.
        var selector = ParseOptionalSelectorAfterKnownName(ident);
        return new SelectorSumNode(selector, window: null);
    }

    // Parses a comma-separated argument list, stopping before the closing ')'.
    private List<ValueNode> ParseArgList()
    {
        var args = new List<ValueNode>();
        do
        {
            args.Add(ParseExpr());
            SkipWs();
        }
        while (Accept(","));
        return args;
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
        if (!Accept("{")) return sel;

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

        return sel;
    }

    private (MetricSelector selector, TimeSpan window) ParseSelectorWithRange()
    {
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
        var closed = false;
        while (!Eof)
        {
            var c = Cur; _pos++;
            if (c == '"') { closed = true; break; }
            if (c == '\\' && !Eof) { sb.Append(Cur); _pos++; }
            else sb.Append(c);
        }
        if (!closed) throw new FormatException("Unterminated quoted string");
        return sb.ToString();
    }

    private TimeSpan ParseDuration()
    {
        SkipWs();
        int start = _pos;
        while (!Eof && char.IsDigit(Cur)) _pos++;
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
