using System.Numerics;
using System.Text.RegularExpressions;

namespace AdobeDownloader.Core;

public static class AdobeVersion
{
    public static int Compare(string left, string right)
    {
        static BigInteger[] Parts(string text)
        {
            if (text.Length > 128 || !Regex.IsMatch(text, @"^\d+(\.\d+)*$"))
                throw new InvalidDataException($"Unsupported Adobe version: {text}");
            return text.Split('.').Select(BigInteger.Parse).ToArray();
        }
        var a = Parts(left); var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var result = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (result != 0) return result;
        }
        return 0;
    }
}

/// <summary>A bounded parser for Adobe package expressions. Unknown variables/syntax stop planning.</summary>
public sealed class PackageConditions
{
    private static readonly Regex Token = new(@"\G\s*(\[[A-Za-z][A-Za-z0-9_]*\]|'[^']*'|""[^""]*""|&&|\|\||==|!=|>=|<=|[()!<>=]|[^\s\[\]()!<>=&|'""]+)");
    private readonly List<string> tokens = [];
    private readonly IReadOnlyDictionary<string, string> variables;
    private int index;
    private int depth;

    private PackageConditions(string expression, IReadOnlyDictionary<string, string> variables)
    {
        this.variables = variables;
        if (expression.Length > 8192) throw new InvalidDataException("Package condition is too long.");
        expression = expression.Replace("&amp;", "&").Trim();
        var offset = 0;
        while (offset < expression.Length)
        {
            var match = Token.Match(expression, offset);
            if (!match.Success || tokens.Count >= 512) throw new InvalidDataException($"Unsupported package condition: {expression}");
            tokens.Add(match.Groups[1].Value);
            offset += match.Length;
        }
    }

    public static bool Evaluate(string expression, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(expression)) return true;
        var parser = new PackageConditions(expression, variables);
        var result = parser.Or();
        if (parser.index != parser.tokens.Count) throw new InvalidDataException("Unconsumed package condition tokens.");
        return result;
    }

    private string Peek => index < tokens.Count ? tokens[index] : "";
    private bool Take(string value) { if (Peek != value) return false; index++; return true; }
    private bool Or() { var value = And(); while (Take("||")) { var right = And(); value |= right; } return value; }
    private bool And() { var value = Unary(); while (Take("&&")) { var right = Unary(); value &= right; } return value; }
    private bool Unary()
    {
        if (++depth > 64) throw new InvalidDataException("Package condition nesting exceeds the limit.");
        try
        {
            if (Take("!")) return !Unary();
            if (Take("("))
            {
                var result = Or();
                if (!Take(")")) throw new InvalidDataException("Unclosed package condition parenthesis.");
                return result;
            }
            var left = Operand();
            var op = Peek;
            if (op is not ("==" or "=" or "!=" or ">" or "<" or ">=" or "<="))
                return left.ToLowerInvariant() switch
                { "true" or "1" => true, "false" or "0" => false, _ => throw new InvalidDataException("Expected a comparison in package condition.") };
            index++;
            var right = Operand();
            if (op is "==" or "=" or "!=")
            {
                if (Regex.IsMatch(left, @"^\d+(\.\d+)*$") && Regex.IsMatch(right, @"^\d+(\.\d+)*$"))
                {
                    var sameVersion = AdobeVersion.Compare(left, right) == 0;
                    return op == "!=" ? !sameVersion : sameVersion;
                }
                var a = left.Split(',', StringSplitOptions.TrimEntries); var b = right.Split(',', StringSplitOptions.TrimEntries);
                var equal = a.Contains("ALL", StringComparer.OrdinalIgnoreCase) || b.Contains("ALL", StringComparer.OrdinalIgnoreCase) ||
                    a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();
                return op == "!=" ? !equal : equal;
            }
            var comparison = AdobeVersion.Compare(left, right);
            return op switch { ">" => comparison > 0, "<" => comparison < 0, ">=" => comparison >= 0, _ => comparison <= 0 };
        }
        finally { depth--; }
    }
    private string Operand()
    {
        var value = Peek;
        if (value.Length == 0 || value is "(" or ")" or "!" or "&&" or "||" or "==" or "=" or "!=" or ">" or "<" or ">=" or "<=")
            throw new InvalidDataException("Missing operand in package condition.");
        index++;
        if (value.StartsWith('['))
            return variables.TryGetValue(value[1..^1], out var resolved) ? resolved :
                throw new InvalidDataException($"Unsupported package condition variable: {value}");
        return value.Trim('\'', '"');
    }
}
