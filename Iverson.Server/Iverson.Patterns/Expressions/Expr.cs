namespace Iverson.Patterns.Expressions;

internal enum BinaryOp { Add, Subtract, Multiply, Divide, Modulo, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual, And, Or }
internal enum UnaryOp { Negate, Not }
internal enum AggregateKind { Count, Sum, Avg, Min, Max }
internal enum ScalarFunction { Coalesce, NullIf, Round, Abs }
internal enum TimeUnit { Second, Minute, Hour, Day }

internal abstract record Expr;

/// <summary><see cref="Value"/> is <c>long</c>, <c>double</c>, <c>string</c>, <c>bool</c> or <c>null</c>.</summary>
internal sealed record LiteralExpr(object? Value) : Expr;

/// <summary>A column read. <see cref="Navigation"/> is null inside an aggregate argument: the aggregated row.</summary>
internal sealed record ColumnExpr(string Column, Navigation? Navigation) : Expr;

internal sealed record ClassifierExpr(Navigation? Navigation) : Expr;

internal sealed record MatchNumberExpr : Expr;

/// <summary><see cref="Term"/> indexes <see cref="SimilarityTermTable.Terms"/>.</summary>
internal sealed record SimilarityExpr(int Term, Navigation? Navigation) : Expr;

/// <summary><see cref="Argument"/> null = <c>COUNT(*)</c> / <c>COUNT(v.*)</c>. <see cref="Labels"/> sorted, empty =
/// universal. <see cref="ClassifierInvolved"/> = the argument calls <c>CLASSIFIER()</c>.</summary>
internal sealed record AggregateExpr(AggregateKind Kind, int[] Labels, Expr? Argument, bool Running, bool ClassifierInvolved) : Expr;

internal sealed record UnaryExpr(UnaryOp Op, Expr Operand) : Expr;
internal sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) : Expr;
internal sealed record IsNullExpr(Expr Operand, bool Negated) : Expr;
internal sealed record InExpr(Expr Operand, IReadOnlyList<Expr> Values, bool Negated) : Expr;
internal sealed record BetweenExpr(Expr Operand, Expr Low, Expr High, bool Negated) : Expr;
internal sealed record CaseBranch(Expr When, Expr Then);
/// <summary><see cref="Operand"/> non-null = simple CASE; null = searched CASE.</summary>
internal sealed record CaseExpr(Expr? Operand, IReadOnlyList<CaseBranch> Branches, Expr? Else) : Expr;
internal sealed record FunctionExpr(ScalarFunction Function, IReadOnlyList<Expr> Arguments) : Expr;
internal sealed record TimestampDiffExpr(TimeUnit Unit, Expr From, Expr To) : Expr;
