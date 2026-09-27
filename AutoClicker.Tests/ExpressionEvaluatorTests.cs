using Xunit;

namespace AutoClicker.Tests;

/// <summary>
/// The expression grammar behind IfElse conditions and SetVar values. Exercises the
/// recursive-descent parser directly — arithmetic, precedence, parens, comparisons,
/// boolean connectives, strings, variables, functions and the error cases a typo in a
/// condition can produce.
/// </summary>
public class ExpressionEvaluatorTests
{
    private static double Num(string expr, Dictionary<string, VarValue>? vars = null) =>
        ExpressionEvaluator.Evaluate(expr, vars).Number;

    private static string Str(string expr, Dictionary<string, VarValue>? vars = null) =>
        ExpressionEvaluator.Evaluate(expr, vars).Text;

    // ---- Arithmetic and precedence ----

    [Fact]
    public void Multiplicative_binds_tighter_than_additive() => Assert.Equal(7, Num("1 + 2 * 3"));

    [Fact]
    public void Parentheses_override_precedence() => Assert.Equal(9, Num("(1 + 2) * 3"));

    [Theory]
    [InlineData("10 / 4", 2.5)]
    [InlineData("10 % 3", 1)]
    [InlineData("2 + 3 - 1", 4)]
    [InlineData("-5 + 10", 5)]
    [InlineData("2 * -3", -6)]
    [InlineData("1.5 + 0.5", 2)]
    public void Basic_arithmetic(string expr, double expected) => Assert.Equal(expected, Num(expr));

    [Fact]
    public void Comparison_binds_looser_than_addition() => Assert.Equal(1, Num("1 + 2 == 3"));

    // ---- Comparisons ----

    [Theory]
    [InlineData("5 < 10", 1)]
    [InlineData("10 <= 10", 1)]
    [InlineData("5 > 10", 0)]
    [InlineData("5 >= 5", 1)]
    [InlineData("5 == 5", 1)]
    [InlineData("5 != 5", 0)]
    [InlineData("\"a\" == \"a\"", 1)]
    [InlineData("\"a\" == \"b\"", 0)]
    [InlineData("\"a\" != \"b\"", 1)]
    public void Comparisons(string expr, double expected) => Assert.Equal(expected, Num(expr));

    [Fact]
    public void Numeric_strings_coerce_in_relational_comparisons() => Assert.Equal(1, Num("\"5\" < 10"));

    // ---- Boolean connectives ----

    [Theory]
    [InlineData("1 && 1", 1)]
    [InlineData("1 && 0", 0)]
    [InlineData("0 || 1", 1)]
    [InlineData("0 || 0", 0)]
    [InlineData("!0", 1)]
    [InlineData("!1", 0)]
    [InlineData("1 < 2 && 3 < 4", 1)]
    [InlineData("1 < 2 && 3 > 4", 0)]
    [InlineData("1 > 2 || 3 < 4", 1)]
    public void Boolean_connectives(string expr, double expected) => Assert.Equal(expected, Num(expr));

    [Theory]
    [InlineData("1 AND 1", 1)]
    [InlineData("1 AND 0", 0)]
    [InlineData("0 OR 1", 1)]
    [InlineData("NOT 0", 1)]
    public void Word_aliases_for_connectives(string expr, double expected) => Assert.Equal(expected, Num(expr));

    // ---- Strings ----

    [Fact]
    public void String_literal_is_itself() => Assert.Equal("foo", Str("\"foo\""));

    [Fact]
    public void String_concatenation() => Assert.Equal("ab", Str("\"a\" + \"b\""));

    [Fact]
    public void Number_formats_into_string_concatenation() => Assert.Equal("a1", Str("\"a\" + 1"));

    // ---- Variables ----

    [Fact]
    public void Variables_resolve_from_the_dictionary()
    {
        var vars = new Dictionary<string, VarValue> { ["counter"] = VarValue.FromNumber(5) };
        Assert.Equal(5, Num("counter", vars));
        Assert.Equal(6, Num("counter + 1", vars));
    }

    [Fact]
    public void String_variables_resolve()
    {
        var vars = new Dictionary<string, VarValue> { ["name"] = VarValue.FromString("x") };
        Assert.Equal("x", Str("name", vars));
        Assert.Equal(1, Num("name == \"x\"", vars));
    }

    [Fact]
    public void Variable_lookup_is_case_insensitive()
    {
        var vars = new Dictionary<string, VarValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["counter"] = VarValue.FromNumber(5),
        };
        Assert.Equal(5, Num("Counter", vars));
    }

    // ---- Functions ----

    [Theory]
    [InlineData("min(3, 5)", 3)]
    [InlineData("max(3, 5)", 5)]
    [InlineData("abs(-4)", 4)]
    [InlineData("min(1 + 2, max(4, 5))", 3)]
    [InlineData("abs(2 - 5)", 3)]
    public void Functions(string expr, double expected) => Assert.Equal(expected, Num(expr));

    // ---- Error cases ----

    [Fact]
    public void Division_by_zero_throws_a_clear_exception()
    {
        var ex = Assert.Throws<ExpressionException>(() => Num("1 / 0"));
        Assert.Contains("zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Modulo_by_zero_throws_a_clear_exception()
    {
        var ex = Assert.Throws<ExpressionException>(() => Num("5 % 0"));
        Assert.Contains("zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unclosed_parenthesis_throws()
    {
        Assert.Throws<ExpressionException>(() => Num("(1 + 2"));
    }

    [Fact]
    public void Unknown_identifier_throws_naming_it()
    {
        var ex = Assert.Throws<ExpressionException>(() => Num("nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Unknown_function_throws_naming_it()
    {
        var ex = Assert.Throws<ExpressionException>(() => Num("nope(1)"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Non_numeric_string_in_arithmetic_throws()
    {
        var ex = Assert.Throws<ExpressionException>(() => Num("\"a\" - 1"));
        Assert.Contains("not a number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_numeric_strings_cannot_order_compare()
    {
        Assert.Throws<ExpressionException>(() => Num("\"a\" < \"b\""));
    }
}
