namespace Iverson.Patterns.Expressions;

/// <summary>
/// The run-time value model of the spec §2 expression language: every value is <c>null</c>, <c>long</c>,
/// <c>double</c>, <c>string</c>, <c>bool</c> or <c>DateTime</c>. Comparisons follow IEEE 754 for doubles (every
/// relation with NaN is false, <c>&lt;&gt;</c> is true), and <c>long</c> arithmetic is checked.
/// </summary>
internal static class SqlValues
{
    /// <summary>Widens a row value to the value model: integers to <c>long</c>, floating and decimal to <c>double</c>.</summary>
    public static object? Normalize(object? value, string column) => value switch
    {
        null => null,
        long l => (object)l,
        int i => (object)(long)i,
        short s => (object)(long)s,
        sbyte sb => (object)(long)sb,
        byte b => (object)(long)b,
        ushort us => (object)(long)us,
        uint ui => (object)(long)ui,
        double d => (object)d,
        float f => (object)(double)f,
        decimal m => (object)(double)m,
        string or bool or DateTime => value,
        _ => throw new PatternEvaluationException($"Unsupported value type {value.GetType().Name} in column '{column}'."),
    };

    public static bool IsNumeric(object value) => value is long or double;

    public static double ToDouble(object value) => value is long l ? l : (double)value;

    /// <summary>Applies the relation <paramref name="op"/> (<c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&lt;=</c>,
    /// <c>&gt;</c>, <c>&gt;=</c>) to two non-null operands. Doubles use the C# relational operators, so NaN
    /// follows IEEE 754 rather than <see cref="double.CompareTo(double)"/>'s total order.</summary>
    public static bool Compare(object a, object b, BinaryOp op)
    {
        switch (a, b)
        {
            case (long x, long y):
                return op switch
                {
                    BinaryOp.Equal => x == y,
                    BinaryOp.NotEqual => x != y,
                    BinaryOp.Less => x < y,
                    BinaryOp.LessOrEqual => x <= y,
                    BinaryOp.Greater => x > y,
                    BinaryOp.GreaterOrEqual => x >= y,
                    _ => throw NotARelation(op),
                };
            case (long or double, long or double):
            {
                double x = ToDouble(a), y = ToDouble(b);
                return op switch
                {
                    BinaryOp.Equal => x == y,
                    BinaryOp.NotEqual => x != y,
                    BinaryOp.Less => x < y,
                    BinaryOp.LessOrEqual => x <= y,
                    BinaryOp.Greater => x > y,
                    BinaryOp.GreaterOrEqual => x >= y,
                    _ => throw NotARelation(op),
                };
            }
            case (string x, string y):
                return Relate(string.CompareOrdinal(x, y), op);
            case (bool x, bool y):
                return Relate(x.CompareTo(y), op);
            case (DateTime x, DateTime y):
                return Relate(x.CompareTo(y), op);
            default:
                throw new PatternEvaluationException($"Cannot compare {TypeName(a)} with {TypeName(b)}.");
        }
    }

    /// <summary><c>+ - * / %</c> over two non-null operands.</summary>
    public static object Arithmetic(BinaryOp op, object a, object b)
    {
        if (!IsNumeric(a) || !IsNumeric(b))
        {
            throw new PatternEvaluationException(
                $"Cannot apply arithmetic to {TypeName(a)} and {TypeName(b)}.");
        }

        if (a is long x && b is long y)
        {
            try
            {
                return op switch
                {
                    BinaryOp.Add => checked(x + y),
                    BinaryOp.Subtract => checked(x - y),
                    BinaryOp.Multiply => checked(x * y),
                    // C# '/' and '%' truncate toward zero and take the dividend's sign, as SQL does.
                    BinaryOp.Divide => y == 0 ? throw DivisionByZero() : checked(x / y),
                    // long.MinValue % -1 throws in .NET but is 0 in Java (and so in Trino).
                    BinaryOp.Modulo => y == 0 ? throw DivisionByZero() : y == -1 ? 0L : x % y,
                    _ => throw NotArithmetic(op),
                };
            }
            catch (OverflowException)
            {
                throw new PatternEvaluationException("Integer overflow.");
            }
        }

        double p = ToDouble(a), q = ToDouble(b);
        return op switch
        {
            BinaryOp.Add => p + q,
            BinaryOp.Subtract => p - q,
            BinaryOp.Multiply => p * q,
            BinaryOp.Divide => p / q,
            BinaryOp.Modulo => p % q,
            _ => throw NotArithmetic(op),
        };
    }

    /// <summary>Unary minus over a non-null operand; checked for <c>long</c>.</summary>
    public static object Negate(object value) => value switch
    {
        long l => l == long.MinValue ? throw new PatternEvaluationException("Integer overflow.") : (object)-l,
        double d => (object)-d,
        _ => throw new PatternEvaluationException($"Cannot negate {TypeName(value)}."),
    };

    public static string TypeName(object? value) => value switch
    {
        null => "NULL",
        long => "bigint",
        double => "double",
        string => "varchar",
        bool => "boolean",
        DateTime => "timestamp",
        _ => value.GetType().Name,
    };

    private static bool Relate(int order, BinaryOp op) => op switch
    {
        BinaryOp.Equal => order == 0,
        BinaryOp.NotEqual => order != 0,
        BinaryOp.Less => order < 0,
        BinaryOp.LessOrEqual => order <= 0,
        BinaryOp.Greater => order > 0,
        BinaryOp.GreaterOrEqual => order >= 0,
        _ => throw NotARelation(op),
    };

    private static PatternEvaluationException DivisionByZero() => new("Division by zero");

    private static InvalidOperationException NotARelation(BinaryOp op) => new($"{op} is not a comparison.");

    private static InvalidOperationException NotArithmetic(BinaryOp op) => new($"{op} is not an arithmetic operator.");
}
