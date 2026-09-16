using System.Globalization;

namespace HalconWorkflow.Nodes.Flow;

/// <summary>
/// Safe recursive-descent expression evaluator for flow.script. No compilation, no dynamic code — 
/// pure AST evaluation. Supports numeric/string/bool literals, identifiers (resolved per call), 
/// arithmetic ( + - * / % ^ ), comparisons, logic (&& || !) and ternary (?:).
/// 脚本节点的安全递归下降表达式求值器：不编译、不执行动态代码，纯 AST 求值
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly string _source;
    private int _pos;
    private readonly Func<string, object?>? _resolve;

    public ExpressionEvaluator(string source, Func<string, object?>? resolve = null)
    {
        _source = source;
        _resolve = resolve;
    }

    /// <summary>Evaluates the whole expression. · 求值整个表达式</summary>
    public object? Evaluate() => ParseTernary();

    private object? ParseTernary()
    {
        var cond = ParseOr();
        if (!Match('?')) return cond;
        var truthy = ParseTernary();
        Expect(':');
        var falsy = ParseTernary();
        return ValueCoercion.ToBool(cond) ? truthy : falsy;
    }

    private object? ParseOr()
    {
        var left = ParseAnd();
        while (MatchWord("||"))
        {
            var right = ParseAnd();
            left = ValueCoercion.ToBool(left) || ValueCoercion.ToBool(right);
        }
        return left;
    }

    private object? ParseAnd()
    {
        var left = ParseEquality();
        while (MatchWord("&&"))
        {
            var right = ParseEquality();
            left = ValueCoercion.ToBool(left) && ValueCoercion.ToBool(right);
        }
        return left;
    }

    private object? ParseEquality()
    {
        var left = ParseComparison();
        while (true)
        {
            if (MatchWord("==")) left = NullEquals(left, ParseComparison());
            else if (MatchWord("!=")) left = !NullEquals(left, ParseComparison());
            else break;
        }
        return left;
    }

    private object? ParseComparison()
    {
        var left = ParseAdditive();
        while (true)
        {
            // Two-char operators first so '>=' is not consumed as '>' then '='. · 两字符运算符优先，避免 '>=' 被消费成 '>' 再遇 '='
            if (MatchWord("<="))
            {
                var right = ParseAdditive();
                left = Compare(left, right) <= 0;
            }
            else if (MatchWord(">="))
            {
                var right = ParseAdditive();
                left = Compare(left, right) >= 0;
            }
            else if (Match('<'))
            {
                var right = ParseAdditive();
                left = Compare(left, right) < 0;
            }
            else if (Match('>'))
            {
                var right = ParseAdditive();
                left = Compare(left, right) > 0;
            }
            else break;
        }
        return left;
    }

    private object? ParseAdditive()
    {
        var left = ParseTerm();
        while (true)
        {
            if (Match('+'))
            {
                var r = ParseTerm();
                left = IsString(left) || IsString(r) ? Concat(left, r) : Add(left, r);
            }
            else if (Match('-'))
            {
                var r = ParseTerm();
                left = left is long a1 && r is long b1 ? (object)(a1 - b1) : Numeric(left) - Numeric(r);
            }
            else break;
        }
        return left;
    }

    private object? ParseTerm()
    {
        var left = ParseUnary();
        while (true)
        {
            if (Match('*'))
            {
                var r = ParseUnary();
                left = left is long a1 && r is long b1 ? (object)(a1 * b1) : Numeric(left) * Numeric(r);
            }
            else if (Match('%'))
            {
                var r = ParseUnary();
                left = left is long a2 && r is long b2 ? (object)(a2 % b2) : Numeric(left) % Numeric(r);
            }
            else if (Match('/'))
            {
                var r = ParseUnary();
                left = Numeric(left) / Numeric(r);
            }
            else if (Match('^'))
            {
                var r = ParseUnary();
                left = Math.Pow(Numeric(left), Numeric(r));
            }
            else break;
        }
        return left;
    }

    private object? ParseUnary()
    {
        if (Match('-')) return -Numeric(ParseUnary());
        if (Match('!')) return !ValueCoercion.ToBool(ParseUnary());
        return ParsePrimary();
    }

    private object? ParsePrimary()
    {
        SkipWs();
        if (Match('('))
        {
            var v = ParseTernary();
            Expect(')');
            return v;
        }
        if (MatchQuoted(out var str)) return str;
        if (MatchNumber(out var num)) return num;
        if (MatchWord("true")) return true;
        if (MatchWord("false")) return false;
        if (MatchWord("null")) return null;

        var ident = ReadIdentifier();
        if (ident.Length == 0) throw new FormatException($"Unexpected token near '{Peek()}' in script '{_source}'.");
        return _resolve?.Invoke(ident);
    }

    private static object? ToScalar(object? v) => v switch
    {
        null => null,
        bool b => b,
        double d => d,
        long l => l,
        _ => ValueCoercion.ToText(v)
    };

    /// <summary>Numeric-tolerant equality: 3.0 == 3, null == null. · 数值宽容等值：3.0==3、null==null</summary>
    private static bool NullEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        var an = IsNumeric(a);
        var bn = IsNumeric(b);
        if (an && bn) return Numeric(a) == Numeric(b);
        if (an || bn) return false;
        if (IsString(a) || IsString(b)) return string.Equals(ValueCoercion.ToText(a), ValueCoercion.ToText(b), StringComparison.Ordinal);
        return a.Equals(b);
    }

    private static bool IsNumeric(object? v) => v is long or double;
    private static bool IsString(object? v) => v is string;

    private static object Concat(object? a, object? b) => ValueCoercion.ToText(a) + ValueCoercion.ToText(b);

    private static object Add(object? a, object? b)
        => a is long la && b is long lb ? (object)(la + lb) : Numeric(a) + Numeric(b);

    private static int Compare(object? a, object? b)
    {
        if (a is bool || b is bool) return (ValueCoercion.ToBool(a) ? 1 : 0).CompareTo(ValueCoercion.ToBool(b) ? 1 : 0);
        if (a is string || b is string) return string.Compare(ValueCoercion.ToText(a), ValueCoercion.ToText(b), StringComparison.Ordinal);
        return Numeric(a).CompareTo(Numeric(b));
    }

    private static double Numeric(object? v) => ValueCoercion.ToDouble(v);

    private void Expect(char c)
    {
        if (!Match(c)) throw new FormatException($"Expected '{c}' at position {_pos} in script '{_source}'.");
    }

    private bool Match(char c)
    {
        SkipWs();
        if (_pos < _source.Length && _source[_pos] == c)
        {
            _pos++;
            return true;
        }
        return false;
    }

    private bool MatchWord(string w)
    {
        SkipWs();
        if (_pos + w.Length > _source.Length) return false;
        if (!string.Equals(_source.Substring(_pos, w.Length), w, StringComparison.Ordinal)) return false;
        _pos += w.Length;
        return true;
    }

    private bool MatchNumber(out object value)
    {
        SkipWs();
        var start = _pos;
        if (_pos < _source.Length && (_source[_pos] == '-' || _source[_pos] == '+')) _pos++;
        var digits = 0;
        var dots = 0;
        var exp = 0;
        while (_pos < _source.Length)
        {
            var c = _source[_pos];
            if (char.IsAsciiDigit(c)) { digits++; _pos++; }
            else if (c == '.' && dots == 0) { dots++; _pos++; }
            else if ((c is 'e' or 'E') && digits > 0) { exp++; _pos++; if (_pos < _source.Length && (_source[_pos] is '+' or '-')) _pos++; }
            else break;
        }
        if (digits == 0)
        {
            _pos = start;
            value = 0;
            return false;
        }
var text = _source[start.._pos];
        value = (dots > 0 || exp > 0)
            ? (object)double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);
        return true;
    }

    private bool MatchQuoted(out string value)
    {
        SkipWs();
        if (_pos >= _source.Length || _source[_pos] != '"')
        {
            value = "";
            return false;
        }
        _pos++;
        var sb = new System.Text.StringBuilder();
        while (_pos < _source.Length)
        {
            var c = _source[_pos++];
            if (c == '"') { value = sb.ToString(); return true; }
            if (c == '\\' && _pos < _source.Length)
            {
                var n = _source[_pos++];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '"' => '"', _ => n });
            }
            else sb.Append(c);
        }
        throw new FormatException($"Unterminated string in script '{_source}'.");
    }

    private string ReadIdentifier()
    {
        SkipWs();
        var start = _pos;
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] is '_' or '.'))
            _pos++;
        return _source[start.._pos];
    }

    private char Peek()
    {
        SkipWs();
        return _pos < _source.Length ? _source[_pos] : '\0';
    }

    private void SkipWs()
    {
        while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos])) _pos++;
    }
}