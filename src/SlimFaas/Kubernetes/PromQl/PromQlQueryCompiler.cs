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
            LabelSelection? matching = ParseLabelSelection("on", "ignoring");
            var right = ParseAdd();
            if (!returnBool && left.IsScalar && right.IsScalar)
                throw new FormatException("Scalar comparisons require bool");
            left = MakeBinary(left, op, right, matching, returnBool);
        }
    }

    private bool AcceptKeyword(string keyword)
    {
        SkipWs();
        int end = _pos + keyword.Length;
        if (end < _s.Length && (char.IsLetterOrDigit(_s[end]) || _s[end] is '_' or ':')) return false;
        return Accept(keyword);
    }

    private static BinaryNode MakeBinary(ValueNode left, string op, ValueNode right,
        LabelSelection? matching, bool returnBool = false)
    {
        if (matching is not null && (left.IsScalar || right.IsScalar))
            throw new FormatException("Vector matching requires two instant vectors");
        return new BinaryNode(left, op, right, returnBool, matching);
    }

    private ValueNode ParseAdd()
    {
        var left = ParseMul();
        while (true)
        {
            string? op = Accept("+") ? "+" : Accept("-") ? "-" : null;
            if (op is null) return left;
            LabelSelection? matching = ParseLabelSelection("on", "ignoring");
            left = MakeBinary(left, op, ParseMul(), matching);
        }
    }

    private ValueNode ParseMul()
    {
        var left = ParseUnary();
        while (true)
        {
            string? op = Accept("*") ? "*" : Accept("/") ? "/" : null;
            if (op is null) return left;
            LabelSelection? matching = ParseLabelSelection("on", "ignoring");
            left = MakeBinary(left, op, ParseUnary(), matching);
        }
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

        return ParseNamedExpression(ParseIdent());
    }

    private ValueNode ParseNamedExpression(string ident)
    {
        string name = ident.ToLowerInvariant();
        Aggregation? aggregation = name switch
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
            LabelSelection? grouping = ParseLabelSelection("by", "without");
            if (grouping is not null || Cur == '(') return ParseAggregate(aggregation.Value, grouping);
        }
        if (!Accept("("))
            return new SelectorNode(ParseOptionalSelectorAfterKnownName(ident), SampleFunction.Instant, 0);

        if (name is "histogram_quantile" or "clamp_min")
        {
            ValueNode first = ParseExpr();
            Expect(",");
            ValueNode second = ParseExpr();
            Expect(")");
            return name switch
            {
                "clamp_min" when !first.IsScalar && second.IsScalar => new ClampMinNode(first, second),
                "histogram_quantile" when first.IsScalar && !second.IsScalar => new HistogramQuantileNode(first, second),
                "clamp_min" => throw new FormatException("clamp_min requires an instant vector and a scalar minimum"),
                _ => throw new FormatException("histogram_quantile requires a scalar quantile and an instant vector")
            };
        }

        SampleFunction function = name switch
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
            "present_over_time" => SampleFunction.Present,
            _ => throw new FormatException($"Unsupported function '{ident}'")
        };
        var (selector, window) = ParseSelectorWithRange();
        Expect(")");
        return new SelectorNode(selector, function, window);
    }

    private ValueNode ParseAggregate(Aggregation operation, LabelSelection? grouping)
    {
        Expect("(");
        List<ValueNode> args = ParseArgList();
        Expect(")");
        grouping ??= ParseLabelSelection("by", "without");
        if (args.Count == 1)
        {
            if (args[0].IsScalar && (operation == Aggregation.Count || grouping is not null))
                throw new FormatException("count and grouped aggregations require an instant vector");
            return new AggregateNode(args[0], operation, grouping);
        }
        if (grouping is null && operation is Aggregation.Min or Aggregation.Max)
            return new VariadicExtremaNode(args, operation == Aggregation.Min);
        throw new FormatException("Aggregation requires one argument");
    }

    private LabelSelection? ParseLabelSelection(string includeKeyword, string excludeKeyword)
    {
        bool include = AcceptKeyword(includeKeyword);
        if (!include && !AcceptKeyword(excludeKeyword)) return null;
        Expect("(");
        var names = new List<string>();
        while (!Accept(")"))
        {
            string label = ParseIdent();
            if (label == "__name__" && include)
                throw new FormatException("Grouping or matching on the metric name is unsupported");
            if (names.Contains(label)) throw new FormatException($"Duplicate label '{label}'");
            names.Add(label);
            if (!Accept(","))
            {
                Expect(")");
                break;
            }
        }
        return new LabelSelection(names.ToArray(), include);
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

            Regex? pattern = op is "=~" or "!~" ? PromQlLabelRegex.Compile(value) : null;
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
