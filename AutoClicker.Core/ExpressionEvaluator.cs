using System.Diagnostics;
using System.Globalization;
using System.Text;
using static AutoClicker.Native;

namespace AutoClicker;

/// <summary>
/// A value a control-flow expression can produce: a number or a string. Numbers never
/// round-trip through a string internally, so a SetVar'd counter stays numeric and keeps
/// working in arithmetic; strings only exist when a string literal, concatenation, or a
/// string-valued SetVar produced them.
/// </summary>
internal readonly struct VarValue
{
    private readonly double _number;
    private readonly string? _text;

    private VarValue(double number) { _number = number; _text = null; }
    private VarValue(string text) { _number = 0; _text = text; }

    public bool IsString => _text is not null;
    public bool IsNumber => _text is null;

    public double Number => _text is null
        ? _number
        : throw new InvalidOperationException("The value is a string, not a number.");

    /// <summary>The string form of either kind; numbers format with the invariant culture.</summary>
    public string Text => _text ?? _number.ToString(CultureInfo.InvariantCulture);

    public static VarValue FromNumber(double number) => new(number);
    public static VarValue FromString(string text) => new(text ?? "");

    public override string ToString() => Text;
}

/// <summary>Thrown for any expression syntax or evaluation error, with a clear message.</summary>
internal sealed class ExpressionException : Exception
{
    public ExpressionException(string message) : base(message) { }
}

/// <summary>
/// A small, safe recursive-descent evaluator for control-flow conditions and SetVar values.
/// No method calls on objects, no member access, no assignment — the grammar only supports
/// what a macro needs:
/// <code>
///   expression : or
///   or         : and ( '||' and )*
///   and        : not ( '&amp;&amp;' not )*
///   not        : '!' not | comparison
///   comparison : additive ( ('=='|'!='|'&lt;'|'&lt;='|'&gt;'|'&gt;=') additive )*
///   additive   : multiplicative ( ('+'|'-') multiplicative )*
///   multiplicative : unary ( ('*'|'/'|'%') unary )*
///   unary      : '-' unary | '+' unary | primary
///   primary    : NUMBER | STRING | IDENT | IDENT '(' args ')' | '(' expression ')'
/// </code>
/// AND/OR/NOT (case-insensitive) are accepted as aliases for &amp;&amp;/||/!. Built-in
/// functions: min, max, abs. Booleans are numbers (1/0) so they compose with arithmetic,
/// but arithmetic only runs on numbers; a non-numeric string in a numeric context is an
/// error naming the offending value.
/// </summary>
internal static class ExpressionEvaluator
{
    private enum TokKind { Number, String, Ident, Op, LParen, RParen, Comma }

    private readonly struct Token
    {
        public TokKind Kind { get; }
        public string Text { get; }
        public double Number { get; }
        public Token(TokKind kind, string text, double number) { Kind = kind; Text = text; Number = number; }
    }

    /// <summary>Evaluates <paramref name="expression"/> against the given variables.</summary>
    /// <exception cref="ExpressionException">Syntax or evaluation error (names the offender).</exception>
    public static VarValue Evaluate(string expression, IReadOnlyDictionary<string, VarValue>? variables = null)
    {
        variables ??= new Dictionary<string, VarValue>(StringComparer.OrdinalIgnoreCase);

        List<Token> tokens = Tokenize(expression);
        int pos = 0;
        VarValue result = ParseOr(tokens, ref pos, variables);
        if (pos < tokens.Count)
            throw new ExpressionException($"Unexpected token '{tokens[pos].Text}'.");
        return result;
    }

    /// <summary>Truthiness for AND/OR/NOT: 0 and "" are false, everything else true.</summary>
    public static bool IsTruthy(VarValue value) => value.IsNumber ? value.Number != 0 : value.Text.Length > 0;

    // ---- Tokenizer ----

    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || (c == '.' && i + 1 < expression.Length && char.IsDigit(expression[i + 1])))
            {
                int start = i;
                while (i < expression.Length)
                {
                    char d = expression[i];
                    bool exponentSign = (d == '+' || d == '-') && i > start
                        && (expression[i - 1] == 'e' || expression[i - 1] == 'E');
                    if (char.IsDigit(d) || d == '.' || d == 'e' || d == 'E' || exponentSign) i++;
                    else break;
                }
                string text = expression[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                    throw new ExpressionException($"'{text}' is not a valid number.");
                tokens.Add(new Token(TokKind.Number, text, num));
                continue;
            }

            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < expression.Length && expression[i] != '"')
                {
                    if (expression[i] == '\\' && i + 1 < expression.Length
                        && (expression[i + 1] == '"' || expression[i + 1] == '\\'))
                    {
                        sb.Append(expression[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(expression[i]);
                    i++;
                }
                if (i >= expression.Length)
                    throw new ExpressionException("Unterminated string literal.");
                i++; // closing quote
                tokens.Add(new Token(TokKind.String, sb.ToString(), 0));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_')) i++;
                string word = expression[start..i];
                if (word.Equals("and", StringComparison.OrdinalIgnoreCase)) tokens.Add(new Token(TokKind.Op, "&&", 0));
                else if (word.Equals("or", StringComparison.OrdinalIgnoreCase)) tokens.Add(new Token(TokKind.Op, "||", 0));
                else if (word.Equals("not", StringComparison.OrdinalIgnoreCase)) tokens.Add(new Token(TokKind.Op, "!", 0));
                else tokens.Add(new Token(TokKind.Ident, word, 0));
                continue;
            }

            if (i + 1 < expression.Length)
            {
                string two = expression.Substring(i, 2);
                if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=")
                {
                    tokens.Add(new Token(TokKind.Op, two, 0));
                    i += 2;
                    continue;
                }
            }

            switch (c)
            {
                case '+' or '-' or '*' or '/' or '%' or '<' or '>' or '!':
                    tokens.Add(new Token(TokKind.Op, c.ToString(), 0));
                    i++;
                    continue;
                case '(':
                    tokens.Add(new Token(TokKind.LParen, "(", 0));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokKind.RParen, ")", 0));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokKind.Comma, ",", 0));
                    i++;
                    continue;
                default:
                    throw new ExpressionException($"Unexpected character '{c}' in expression.");
            }
        }
        return tokens;
    }

    // ---- Recursive descent ----

    private static VarValue ParseOr(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        VarValue left = ParseAnd(t, ref p, vars);
        while (MatchOp(t, ref p, "||"))
        {
            // The right operand is parsed BEFORE the boolean combine: the C# || here must not
            // short-circuit the PARSE, or a falsy-left expression like `0 || foo()` would leave
            // `foo()` unconsumed and trip the "unexpected token" check. Both sides are always
            // evaluated; the result is still the ordinary logical OR of their truthiness.
            VarValue right = ParseAnd(t, ref p, vars);
            left = Num(IsTruthy(left) || IsTruthy(right) ? 1 : 0);
        }
        return left;
    }

    private static VarValue ParseAnd(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        VarValue left = ParseNot(t, ref p, vars);
        while (MatchOp(t, ref p, "&&"))
        {
            // Same rule as ParseOr: parse the right side unconditionally, so `0 && foo()` still
            // consumes `foo()` and the result stays the logical AND of the two truthinesses.
            VarValue right = ParseNot(t, ref p, vars);
            left = Num(IsTruthy(left) && IsTruthy(right) ? 1 : 0);
        }
        return left;
    }

    private static VarValue ParseNot(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        if (MatchOp(t, ref p, "!"))
            return Num(IsTruthy(ParseNot(t, ref p, vars)) ? 0 : 1);
        return ParseComparison(t, ref p, vars);
    }

    private static VarValue ParseComparison(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        VarValue left = ParseAdditive(t, ref p, vars);
        while (p < t.Count && t[p].Kind == TokKind.Op && t[p].Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            string op = t[p].Text;
            p++;
            left = Compare(left, op, ParseAdditive(t, ref p, vars));
        }
        return left;
    }

    private static VarValue ParseAdditive(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        VarValue left = ParseMultiplicative(t, ref p, vars);
        while (p < t.Count && t[p].Kind == TokKind.Op && t[p].Text is "+" or "-")
        {
            string op = t[p].Text;
            p++;
            VarValue right = ParseMultiplicative(t, ref p, vars);
            left = op == "+" ? Add(left, right) : Num(ToNumber(left) - ToNumber(right));
        }
        return left;
    }

    private static VarValue ParseMultiplicative(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        VarValue left = ParseUnary(t, ref p, vars);
        while (p < t.Count && t[p].Kind == TokKind.Op && t[p].Text is "*" or "/" or "%")
        {
            string op = t[p].Text;
            p++;
            double a = ToNumber(left);
            double b = ToNumber(ParseUnary(t, ref p, vars));
            left = op switch
            {
                "*" => Num(a * b),
                "/" => b == 0 ? throw new ExpressionException("Division by zero.") : Num(a / b),
                _ => b == 0 ? throw new ExpressionException("Modulo by zero.") : Num(a % b),
            };
        }
        return left;
    }

    private static VarValue ParseUnary(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        if (MatchOp(t, ref p, "-")) return Num(-ToNumber(ParseUnary(t, ref p, vars)));
        if (MatchOp(t, ref p, "+")) return ParseUnary(t, ref p, vars);
        return ParsePrimary(t, ref p, vars);
    }

    private static VarValue ParsePrimary(List<Token> t, ref int p, IReadOnlyDictionary<string, VarValue> vars)
    {
        if (p >= t.Count)
            throw new ExpressionException("Unexpected end of expression.");

        Token tok = t[p];
        switch (tok.Kind)
        {
            case TokKind.Number:
                p++;
                return VarValue.FromNumber(tok.Number);

            case TokKind.String:
                p++;
                return VarValue.FromString(tok.Text);

            case TokKind.LParen:
                p++;
                VarValue inner = ParseOr(t, ref p, vars);
                Expect(t, ref p, TokKind.RParen, "')' (unclosed parenthesis)");
                return inner;

            case TokKind.Ident:
                p++;
                string name = tok.Text;
                if (p < t.Count && t[p].Kind == TokKind.LParen)
                {
                    p++;
                    var args = new List<VarValue>();
                    if (p < t.Count && t[p].Kind == TokKind.RParen)
                    {
                        p++;
                    }
                    else
                    {
                        while (true)
                        {
                            args.Add(ParseOr(t, ref p, vars));
                            if (p < t.Count && t[p].Kind == TokKind.Comma) { p++; continue; }
                            break;
                        }
                        Expect(t, ref p, TokKind.RParen, "')' (unclosed parenthesis)");
                    }
                    return CallFunction(name, args);
                }

                if (vars.TryGetValue(name, out VarValue value)) return value;
                throw new ExpressionException($"Unknown variable or function '{name}'.");

            default:
                throw new ExpressionException($"Unexpected token '{tok.Text}'.");
        }
    }

    // ---- Operators and functions ----

    /// <summary>+ is numeric addition only when both sides are numbers; otherwise concatenation.</summary>
    private static VarValue Add(VarValue a, VarValue b) =>
        a.IsNumber && b.IsNumber ? Num(a.Number + b.Number) : VarValue.FromString(a.Text + b.Text);

    private static VarValue Compare(VarValue a, string op, VarValue b)
    {
        switch (op)
        {
            case "==": return Num(ValueEquals(a, b) ? 1 : 0);
            case "!=": return Num(ValueEquals(a, b) ? 0 : 1);
        }

        // Relational comparisons need numbers; a numeric string is coerced so "5" < 10 works.
        if (a.IsNumber && b.IsNumber)
            return Num(Relate(a.Number, op, b.Number) ? 1 : 0);
        if (TryNumber(a, out double na) && TryNumber(b, out double nb))
            return Num(Relate(na, op, nb) ? 1 : 0);

        throw new ExpressionException($"Cannot compare '{a.Text}' with '{b.Text}' using '{op}' — only numbers compare that way.");
    }

    private static bool Relate(double a, string op, double b) => op switch
    {
        "<" => a < b,
        "<=" => a <= b,
        ">" => a > b,
        _ => a >= b,
    };

    private static bool ValueEquals(VarValue a, VarValue b)
    {
        if (a.IsNumber && b.IsNumber) return a.Number == b.Number;
        if (a.IsString && b.IsString) return string.Equals(a.Text, b.Text, StringComparison.Ordinal);
        // Mixed: coerce the string side when it is numeric, otherwise a number never equals text.
        if (a.IsNumber && TryNumber(b, out double nb)) return a.Number == nb;
        if (a.IsString && TryNumber(a, out double na) && b.IsNumber) return na == b.Number;
        return false;
    }

    private static VarValue CallFunction(string name, List<VarValue> args)
    {
        switch (name.ToLowerInvariant())
        {
            case "min":
                RequireArgs(name, args, 2);
                return Num(Math.Min(ToNumber(args[0]), ToNumber(args[1])));
            case "max":
                RequireArgs(name, args, 2);
                return Num(Math.Max(ToNumber(args[0]), ToNumber(args[1])));
            case "abs":
                RequireArgs(name, args, 1);
                return Num(Math.Abs(ToNumber(args[0])));
            case "window_exists":
                RequireArgs(name, args, 1);
                return Num(WindowExists(RequireString(name, args[0])) ? 1 : 0);
            case "process_running":
                RequireArgs(name, args, 1);
                return Num(ProcessRunning(RequireString(name, args[0])) ? 1 : 0);
            default:
                throw new ExpressionException($"Unknown variable or function '{name}'.");
        }
    }

    /// <summary>Requires a string argument; a number argument (e.g. <c>window_exists(5)</c>) is an error.</summary>
    private static string RequireString(string name, VarValue value)
    {
        if (value.IsString) return value.Text;
        throw new ExpressionException($"{name}() expects a string argument, got '{value.Text}'.");
    }

    /// <summary>
    /// True when any top-level visible window has a class name equal to, or a title
    /// containing, <paramref name="substring"/> (case-insensitive). Evaluated live on every
    /// call, on the worker thread — both EnumWindows and the title/class reads are thread-safe.
    /// </summary>
    private static bool WindowExists(string substring)
    {
        if (substring.Length == 0) return false;
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (string.Equals(ClassNameOf(hwnd), substring, StringComparison.OrdinalIgnoreCase)
                || TitleOf(hwnd).Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                return false; // stop enumerating — nothing will beat an exact hit
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// True when a process with the given name is running. The name is accepted with or
    /// without the ".exe" suffix, case-insensitively; an invalid name is simply "not running".
    /// </summary>
    private static bool ProcessRunning(string name)
    {
        if (name.Length == 0) return false;
        string stem = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        try { return Process.GetProcessesByName(stem).Length > 0; }
        catch (ArgumentException) { return false; }
    }

    private static void RequireArgs(string name, List<VarValue> args, int count)
    {
        if (args.Count != count)
            throw new ExpressionException($"{name}() expects {count} argument(s), got {args.Count}.");
    }

    /// <summary>A number passes through; a numeric string coerces; anything else throws.</summary>
    private static double ToNumber(VarValue value)
    {
        if (value.IsNumber) return value.Number;
        if (TryNumber(value, out double d)) return d;
        throw new ExpressionException($"'{value.Text}' is not a number.");
    }

    private static bool TryNumber(VarValue value, out double number)
    {
        if (value.IsNumber) { number = value.Number; return true; }
        return double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool MatchOp(List<Token> t, ref int p, string op)
    {
        if (p < t.Count && t[p].Kind == TokKind.Op && t[p].Text == op) { p++; return true; }
        return false;
    }

    private static void Expect(List<Token> t, ref int p, TokKind kind, string what)
    {
        if (p < t.Count && t[p].Kind == kind) { p++; return; }
        throw new ExpressionException(p >= t.Count
            ? $"Unexpected end of expression — expected {what}."
            : $"Expected {what}, found '{t[p].Text}'.");
    }

    private static VarValue Num(double d) => VarValue.FromNumber(d);
}
