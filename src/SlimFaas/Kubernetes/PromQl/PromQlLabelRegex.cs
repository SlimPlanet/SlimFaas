using System.Text;
using System.Text.RegularExpressions;

namespace SlimFaas.Kubernetes;

internal static class PromQlLabelRegex
{
    public static Regex Compile(string pattern)
    {
        string translated = TranslateClasses(pattern);
        try
        {
            return new Regex($@"\A(?:{translated})\z", RegexOptions.CultureInvariant | RegexOptions.Singleline |
                RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new FormatException("Invalid or unsupported label regular expression", ex);
        }
    }

    private static string TranslateClasses(string pattern)
    {
        if (!pattern.Contains('\\', StringComparison.Ordinal) && !pattern.Contains("[:", StringComparison.Ordinal)) return pattern;
        var result = new StringBuilder(pattern.Length);
        bool inClass = false;
        int index = 0;
        while (index < pattern.Length)
        {
            char current = pattern[index++];
            if (current == '\\' && index < pattern.Length)
            {
                char escape = pattern[index++];
                string? range = PerlRange(escape);
                if (range is null) result.Append('\\').Append(escape);
                else if (inClass) result.Append(range);
                else result.Append('[').Append(range).Append(']');
            }
            else if (current == '[' && inClass && index < pattern.Length && pattern[index] == ':')
            {
                int end = pattern.IndexOf(":]", index + 1, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("Unterminated POSIX character class");
                result.Append(PosixRange(pattern[(index + 1)..end]));
                index = end + 2;
            }
            else
            {
                if (current == '[') inClass = true;
                if (current == ']') inClass = false;
                result.Append(current);
            }
        }
        return result.ToString();
    }

    // RE2 Perl classes are ASCII. Spell out complements so they also work
    // inside larger bracket expressions, where another pair of brackets is invalid.
    private static string? PerlRange(char escape) => escape switch
    {
        'd' => "0-9",
        'D' => @"\x00-\x2f\x3a-\uffff",
        'w' => "0-9A-Z_a-z",
        'W' => @"\x00-\x2f\x3a-\x40\x5b-\x5e\x60\x7b-\uffff",
        's' => @"\t\n\f\r ",
        'S' => @"\x00-\x08\x0b\x0e-\x1f\x21-\uffff",
        _ => null
    };

    private static string PosixRange(string name) => name switch
    {
        "alnum" => "0-9A-Za-z",
        "alpha" => "A-Za-z",
        "ascii" => @"\x00-\x7f",
        "blank" => @"\t ",
        "cntrl" => @"\x00-\x1f\x7f",
        "digit" => "0-9",
        "graph" => @"\x21-\x7e",
        "lower" => "a-z",
        "print" => @"\x20-\x7e",
        "punct" => @"\x21-\x2f\x3a-\x40\x5b-\x60\x7b-\x7e",
        "space" => @"\x09-\x0d ",
        "upper" => "A-Z",
        "word" => "0-9A-Z_a-z",
        "xdigit" => "0-9A-Fa-f",
        _ => throw new FormatException($"Unsupported POSIX character class '{name}'")
    };
}
