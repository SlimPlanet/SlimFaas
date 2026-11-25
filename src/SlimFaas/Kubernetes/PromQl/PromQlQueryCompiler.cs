using System.Globalization;
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

    private static readonly string[] ComparisonOperators = ["==", "!=", ">=", "<=", ">", "<"];

    private ValueNode ParseExpr()
    {
        var left = ParseAdd();
        while (true)
        {
            string? op = null;
            foreach (string candidate in ComparisonOperators)
            {
                if (!Accept(candidate)) continue;
                op = candidate;
                break;
            }
            if (op is null) return left;
            bool returnBool = AcceptKeyword("bool");
            var right = ParseAdd();
            if (!returnBool && left.IsScalar && right.IsScalar)
                throw new FormatException("Scalar comparisons require bool");
            left = new BinaryNode(left, op, right, returnBool);
        }
    }

    private bool AcceptKeyword(string keyword)
    {
        SkipWs();
        int end = _pos + keyword.Length;
        if (end < _s.Length && (char.IsLetterOrDigit(_s[end]) || _s[end] is '_' or ':')) return false;
        return Accept(keyword);
    }

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
        if (Accept("-")) return new BinaryNode(new NumberNode(-1), "*", ParseUnary());
        if (Accept("+")) return ParseUnary();
        if (Accept("("))
        {
            var e = ParseExpr();
            Expect(")");
            return e;
        }

        if (char.IsDigit(Cur) || Cur == '.')
            return new NumberNode(ParseNumber());

        var ident = ParseIdent();

        if (string.Equals(ident, "histogram_quantile", StringComparison.OrdinalIgnoreCase) && Accept("("))
        {
            var phi = ParseNumber();
            Expect(",");
            var inner = ParseExpr();
            Expect(")");
            return new HistogramQuantileNode(phi, inner);
        }

        var function = ident.ToLowerInvariant() switch
        {
            "rate" => SampleFunction.Rate,
            "increase" => SampleFunction.Increase,
            "irate" => SampleFunction.InstantRate,
            "max_over_time" => SampleFunction.Max,
            "min_over_time" => SampleFunction.Min,
            "avg_over_time" => SampleFunction.Average,
            "sum_over_time" => SampleFunction.Sum,
            "count_over_time" => SampleFunction.Count,
            "last_over_time" => SampleFunction.Last,
            _ => SampleFunction.Instant
        };
        if (function != SampleFunction.Instant && Accept("("))
        {
            var (selector, window) = ParseSelectorWithRange();
            Expect(")");
            return new SelectorNode(selector, function, window);
        }

        Aggregation? aggregation = ident.ToLowerInvariant() switch
        {
            "sum" => Aggregation.Sum,
            "min" => Aggregation.Min,
            "max" => Aggregation.Max,
            "avg" => Aggregation.Average,
            "count" => Aggregation.Count,
            _ => null
        };
        if (aggregation.HasValue)
        {
            var by = ParseGrouping();
            if (by is not null || Cur == '(')
            {
                Expect("(");
                var args = ParseArgList();
                Expect(")");
                by ??= ParseGrouping();
                if (args.Count == 1) return new AggregateNode(args[0], aggregation.Value, by ?? []);
                if (by is null && aggregation is Aggregation.Min or Aggregation.Max)
                    return new VariadicExtremaNode(args, aggregation == Aggregation.Min);
                throw new FormatException("Aggregation requires one argument");
            }
        }
        return new SelectorNode(ParseOptionalSelectorAfterKnownName(ident), SampleFunction.Instant, 0);
    }

    private string[]? ParseGrouping()
    {
        if (!AcceptKeyword("by")) return null;
        Expect("(");
        if (Accept(")")) return [];
        var labels = new List<string>();
        do { labels.Add(ParseIdent()); } while (Accept(","));
        Expect(")");
        return labels.ToArray();
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
        if (!Eof && Cur is 'e' or 'E')
        {
            _pos++;
            if (!Eof && Cur is '+' or '-') _pos++;
            while (!Eof && char.IsDigit(Cur)) _pos++;
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
            string op = Accept("=~") ? "=~" : Accept("!~") ? "!~" : Accept("!=") ? "!=" : "=";
            if (op == "=") Expect("=");
            SkipWs();
            if (!MetricSelector.TryReadQuoted(_s.AsSpan(_pos), out var value, out int length))
                throw new FormatException("Invalid quoted label value");
            _pos += length;

            Regex? pattern = null;
            if (op is "=~" or "!~")
            {
                try
                {
                    pattern = new Regex($@"\A(?:{value})\z", RegexOptions.CultureInvariant | RegexOptions.Singleline |
                        RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    throw new FormatException("Invalid or unsupported label regular expression", ex);
                }
            }
            sel.Matchers.Add((label, value, pattern, op is "!=" or "!~"));

            SkipWs();
            if (Accept(",")) continue;
            Expect("}");
            break;
        }

        return sel;
    }

    private (MetricSelector selector, long window) ParseSelectorWithRange()
    {
        var metricName = ParseIdent();
        var sel = ParseOptionalSelectorAfterKnownName(metricName);
        SkipWs();
        Expect("[");
        var win = ParseDuration();
        Expect("]");
        return (sel, win);
    }

    private long ParseDuration()
    {
        SkipWs();
        int start = _pos;
        while (!Eof && char.IsDigit(Cur)) _pos++;
        if (start == _pos) throw new FormatException("Duration number expected");
        var numStr = _s[start.._pos];
        if (!int.TryParse(numStr, out var n) || n == 0) throw new FormatException("Invalid duration");

        SkipWs();
        if (Eof) throw new FormatException("Duration unit expected");
        var unit = Cur; _pos++;

        return unit switch
        {
            's' => n,
            'm' => 60L * n,
            'h' => 3600L * n,
            _ => throw new FormatException("Unsupported duration unit (use s|m|h)")
        };
    }
}
