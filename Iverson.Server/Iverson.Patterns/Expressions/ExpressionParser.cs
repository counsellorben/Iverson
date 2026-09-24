using System.Globalization;

namespace Iverson.Patterns.Expressions;

/// <summary>
/// Parses the spec §2 <c>define</c>/<c>measures</c> grammar by recursive descent and lowers it, while parsing, to
/// Trino 483's navigation model: every column, <c>CLASSIFIER</c> or <c>SIMILARITY</c> read carries a
/// <see cref="Navigation"/> (null inside an aggregate's argument). Precedence, lowest first:
/// <code>
/// or         := and (OR and)*
/// and        := not (AND not)*
/// not        := NOT not | predicate
/// predicate  := additive [ comparison additive | IS [NOT] NULL | [NOT] IN '(' list ')'
///                        | [NOT] BETWEEN additive AND additive ]
/// additive   := multiplicative (('+' | '-') multiplicative)*
/// multiplicative := unary (('*' | '/' | '%') unary)*
/// unary      := '-' unary | '+' unary | primary
/// primary    := literal | '(' or ')' | CASE … END | [RUNNING | FINAL] call | v.col | col
/// </code>
/// One parser serves every <c>define</c> and <c>measures</c> entry of a request, accumulating
/// <see cref="ReferencedColumns"/> and the shared <see cref="SimilarityTermTable"/>.
/// </summary>
/// <param name="labels">Upper-case primary pattern variable name → its label index.</param>
/// <param name="subsets">Upper-case subset name → its members' label indexes.</param>
internal sealed class ExpressionParser(
    IReadOnlyDictionary<string, int> labels,
    IReadOnlyDictionary<string, int[]> subsets,
    SimilarityTermTable similarityTerms)
{
    /// <summary>Trino 483 reserves these words, so they never start a call after <c>RUNNING</c>/<c>FINAL</c> and
    /// are never a bare column name.</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "IN", "IS", "BETWEEN", "CASE", "WHEN", "THEN", "ELSE", "END", "NULL", "TRUE", "FALSE",
    };

    /// <summary>Qualifier (variable or subset name, case-insensitive) → its sorted, distinct label set.</summary>
    private readonly Dictionary<string, int[]> _qualifiers = BuildQualifiers(labels, subsets);

    private readonly SimilarityTermTable _similarityTerms = similarityTerms;

    /// <summary>Every column name read by any parsed expression, as first written.</summary>
    public HashSet<string> ReferencedColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Expr ParseDefine(string text)
    {
        var expr = new Parser(this, ExpressionLexer.Tokenize(text), define: true).ParseRoot();

        if (!CanBeBoolean(expr))
        {
            throw new PatternValidationException("Expression defining a label must be boolean.");
        }

        return expr;
    }

    public Expr ParseMeasure(string text) =>
        new Parser(this, ExpressionLexer.Tokenize(text), define: false).ParseRoot();

    private static Dictionary<string, int[]> BuildQualifiers(
        IReadOnlyDictionary<string, int> labels, IReadOnlyDictionary<string, int[]> subsets)
    {
        var qualifiers = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, index) in labels)
        {
            qualifiers[name] = [index];
        }

        foreach (var (name, members) in subsets)
        {
            qualifiers.TryAdd(name, members.Distinct().Order().ToArray());
        }

        return qualifiers;
    }

    /// <summary>A root that can never evaluate to a boolean. Anything else (a column, <c>COALESCE</c>, <c>CASE</c>,
    /// <c>MIN</c>/<c>MAX</c>, …) is checked at run time.</summary>
    private static bool CanBeBoolean(Expr root) => root switch
    {
        BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide or BinaryOp.Modulo } => false,
        UnaryExpr { Op: UnaryOp.Negate } => false,
        LiteralExpr { Value: long or double or string } => false,
        AggregateExpr { Kind: AggregateKind.Count or AggregateKind.Sum or AggregateKind.Avg } => false,
        FunctionExpr { Function: ScalarFunction.Round or ScalarFunction.Abs } => false,
        MatchNumberExpr or ClassifierExpr or SimilarityExpr or TimestampDiffExpr => false,
        _ => true,
    };

    /// <summary>Applies <paramref name="apply"/> to the navigation of every read in <paramref name="expr"/>.</summary>
    private static Expr Navigate(Expr expr, Func<Navigation, Navigation> apply) => expr switch
    {
        ColumnExpr { Navigation: { } navigation } column => column with { Navigation = apply(navigation) },
        ClassifierExpr { Navigation: { } navigation } classifier => classifier with { Navigation = apply(navigation) },
        SimilarityExpr { Navigation: { } navigation } similarity => similarity with { Navigation = apply(navigation) },
        UnaryExpr unary => unary with { Operand = Navigate(unary.Operand, apply) },
        BinaryExpr binary => binary with { Left = Navigate(binary.Left, apply), Right = Navigate(binary.Right, apply) },
        IsNullExpr isNull => isNull with { Operand = Navigate(isNull.Operand, apply) },
        InExpr @in => @in with
        {
            Operand = Navigate(@in.Operand, apply),
            Values = @in.Values.Select(value => Navigate(value, apply)).ToList(),
        },
        BetweenExpr between => between with
        {
            Operand = Navigate(between.Operand, apply),
            Low = Navigate(between.Low, apply),
            High = Navigate(between.High, apply),
        },
        CaseExpr @case => @case with
        {
            Operand = @case.Operand is null ? null : Navigate(@case.Operand, apply),
            Branches = @case.Branches.Select(branch => new CaseBranch(Navigate(branch.When, apply), Navigate(branch.Then, apply))).ToList(),
            Else = @case.Else is null ? null : Navigate(@case.Else, apply),
        },
        FunctionExpr function => function with { Arguments = function.Arguments.Select(argument => Navigate(argument, apply)).ToList() },
        TimestampDiffExpr diff => diff with { From = Navigate(diff.From, apply), To = Navigate(diff.To, apply) },
        // Literals and MATCH_NUMBER() carry no read; an aggregate cannot occur inside a navigation.
        _ => expr,
    };

    /// <summary>A navigation (<c>PREV</c>/<c>NEXT</c>/<c>FIRST</c>/<c>LAST</c>) or aggregate call whose argument is
    /// being parsed. Every read inside it registers its label set here.</summary>
    private sealed class CallScope(string name, bool isAggregate, bool isPhysical)
    {
        public string Name { get; } = name;

        public bool IsAggregate { get; } = isAggregate;

        /// <summary><c>PREV</c>/<c>NEXT</c>.</summary>
        public bool IsPhysical { get; } = isPhysical;

        public int[]? Labels { get; set; }

        public bool HasRead { get; set; }

        public bool ClassifierInvolved { get; set; }

        /// <summary>The lowered results of the navigations nested directly in this call's argument.</summary>
        public List<Expr> NestedNavigations { get; } = [];
    }

    private sealed class Parser(ExpressionParser owner, List<ExpressionToken> tokens, bool define)
    {
        private readonly List<CallScope> _scopes = [];
        private int _pos;

        private ExpressionToken Current => tokens[_pos];

        private ExpressionToken Peek(int offset) => tokens[Math.Min(_pos + offset, tokens.Count - 1)];

        public Expr ParseRoot()
        {
            var expr = ParseOr();

            if (Current.Kind != ExpressionTokenKind.End)
            {
                throw Unexpected();
            }

            return expr;
        }

        // --- Boolean and comparison levels ---

        private Expr ParseOr()
        {
            var left = ParseAnd();

            while (IsKeyword("OR"))
            {
                _pos++;
                left = new BinaryExpr(BinaryOp.Or, left, ParseAnd());
            }

            return left;
        }

        private Expr ParseAnd()
        {
            var left = ParseNot();

            while (IsKeyword("AND"))
            {
                _pos++;
                left = new BinaryExpr(BinaryOp.And, left, ParseNot());
            }

            return left;
        }

        private Expr ParseNot()
        {
            if (IsKeyword("NOT"))
            {
                _pos++;
                return new UnaryExpr(UnaryOp.Not, ParseNot());
            }

            return ParsePredicate();
        }

        private Expr ParsePredicate()
        {
            var operand = ParseAdditive();

            BinaryOp? comparison = Current.Kind switch
            {
                ExpressionTokenKind.Equal => BinaryOp.Equal,
                ExpressionTokenKind.NotEqual => BinaryOp.NotEqual,
                ExpressionTokenKind.Less => BinaryOp.Less,
                ExpressionTokenKind.LessOrEqual => BinaryOp.LessOrEqual,
                ExpressionTokenKind.Greater => BinaryOp.Greater,
                ExpressionTokenKind.GreaterOrEqual => BinaryOp.GreaterOrEqual,
                _ => null,
            };

            if (comparison is { } op)
            {
                _pos++;
                return new BinaryExpr(op, operand, ParseAdditive());
            }

            if (IsKeyword("IS"))
            {
                _pos++;
                var negated = TryConsumeKeyword("NOT");
                ExpectKeyword("NULL");
                return new IsNullExpr(operand, negated);
            }

            var notPosition = _pos;
            var negatedPredicate = TryConsumeKeyword("NOT");

            if (TryConsumeKeyword("IN"))
            {
                Expect(ExpressionTokenKind.LParen);
                var values = new List<Expr> { ParseOr() };
                while (Current.Kind == ExpressionTokenKind.Comma)
                {
                    _pos++;
                    values.Add(ParseOr());
                }

                Expect(ExpressionTokenKind.RParen);
                return new InExpr(operand, values, negatedPredicate);
            }

            if (TryConsumeKeyword("BETWEEN"))
            {
                var low = ParseAdditive();
                ExpectKeyword("AND");
                var high = ParseAdditive();
                return new BetweenExpr(operand, low, high, negatedPredicate);
            }

            if (negatedPredicate)
            {
                _pos = notPosition;
                throw Unexpected();
            }

            return operand;
        }

        // --- Arithmetic levels ---

        private Expr ParseAdditive()
        {
            var left = ParseMultiplicative();

            while (true)
            {
                BinaryOp op;
                if (Current.Kind == ExpressionTokenKind.Plus)
                {
                    op = BinaryOp.Add;
                }
                else if (Current.Kind == ExpressionTokenKind.Minus)
                {
                    op = BinaryOp.Subtract;
                }
                else
                {
                    return left;
                }

                _pos++;
                left = new BinaryExpr(op, left, ParseMultiplicative());
            }
        }

        private Expr ParseMultiplicative()
        {
            var left = ParseUnary();

            while (true)
            {
                BinaryOp op;
                switch (Current.Kind)
                {
                    case ExpressionTokenKind.Star:
                        op = BinaryOp.Multiply;
                        break;
                    case ExpressionTokenKind.Slash:
                        op = BinaryOp.Divide;
                        break;
                    case ExpressionTokenKind.Percent:
                        op = BinaryOp.Modulo;
                        break;
                    default:
                        return left;
                }

                _pos++;
                left = new BinaryExpr(op, left, ParseUnary());
            }
        }

        private Expr ParseUnary()
        {
            if (Current.Kind == ExpressionTokenKind.Minus)
            {
                _pos++;
                return new UnaryExpr(UnaryOp.Negate, ParseUnary());
            }

            if (Current.Kind == ExpressionTokenKind.Plus)
            {
                _pos++;
                return ParseUnary();
            }

            return ParsePrimary();
        }

        // --- Primaries ---

        private Expr ParsePrimary()
        {
            var token = Current;

            switch (token.Kind)
            {
                case ExpressionTokenKind.Integer:
                case ExpressionTokenKind.Decimal:
                case ExpressionTokenKind.String:
                    _pos++;
                    return new LiteralExpr(token.Value);
                case ExpressionTokenKind.LParen:
                {
                    _pos++;
                    var inner = ParseOr();
                    Expect(ExpressionTokenKind.RParen);
                    return inner;
                }

                case ExpressionTokenKind.Identifier:
                    return ParseIdentifierPrimary();
                default:
                    throw Unexpected();
            }
        }

        private Expr ParseIdentifierPrimary()
        {
            var token = Current;
            var word = token.Text.ToUpperInvariant();

            if (word is "RUNNING" or "FINAL" && IsSemanticsPrefix())
            {
                _pos++;
                return ParseCall(running: word == "RUNNING", prefix: word);
            }

            switch (word)
            {
                case "TRUE":
                    _pos++;
                    return new LiteralExpr(true);
                case "FALSE":
                    _pos++;
                    return new LiteralExpr(false);
                case "NULL":
                    _pos++;
                    return new LiteralExpr(null);
                case "CASE":
                    return ParseCase();
            }

            if (ReservedWords.Contains(word))
            {
                throw Unexpected();
            }

            if (Peek(1).Kind == ExpressionTokenKind.LParen)
            {
                return ParseCall(running: null, prefix: null);
            }

            if (Peek(1).Kind == ExpressionTokenKind.Dot)
            {
                _pos += 2;
                var column = ExpectIdentifier();
                return Column(column, ResolveQualifier(token.Text, column));
            }

            _pos++;
            return Column(token.Text, []);
        }

        /// <summary><c>RUNNING</c>/<c>FINAL</c> prefixes a call only when followed by a non-reserved identifier and
        /// <c>(</c> (Trino 483); otherwise it is an ordinary identifier.</summary>
        private bool IsSemanticsPrefix() =>
            Peek(1).Kind == ExpressionTokenKind.Identifier &&
            !ReservedWords.Contains(Peek(1).Text) &&
            Peek(2).Kind == ExpressionTokenKind.LParen;

        private Expr ParseCase()
        {
            _pos++; // CASE
            Expr? operand = IsKeyword("WHEN") ? null : ParseOr();
            var branches = new List<CaseBranch>();

            while (TryConsumeKeyword("WHEN"))
            {
                var when = ParseOr();
                ExpectKeyword("THEN");
                branches.Add(new CaseBranch(when, ParseOr()));
            }

            if (branches.Count == 0)
            {
                throw Unexpected();
            }

            Expr? @else = TryConsumeKeyword("ELSE") ? ParseOr() : null;
            ExpectKeyword("END");
            return new CaseExpr(operand, branches, @else);
        }

        // --- Calls ---

        /// <summary>Parses <c>name(…)</c> at <see cref="Current"/>. <paramref name="running"/> is the
        /// <c>RUNNING</c>/<c>FINAL</c> prefix's semantics, null when there is none.</summary>
        private Expr ParseCall(bool? running, string? prefix)
        {
            var name = Current.Text;
            var function = name.ToUpperInvariant();

            if (prefix is not null)
            {
                if (function is not ("FIRST" or "LAST" or "COUNT" or "SUM" or "AVG" or "MIN" or "MAX"))
                {
                    throw new PatternValidationException($"{prefix} semantics is not supported with {function}.");
                }

                if (define && running == false)
                {
                    throw new PatternValidationException("FINAL semantics is not supported in DEFINE clause.");
                }
            }

            _pos++; // the name
            Expect(ExpressionTokenKind.LParen);

            switch (function)
            {
                case "PREV":
                case "NEXT":
                case "FIRST":
                case "LAST":
                    return ParseNavigation(function, running ?? true);
                case "COUNT":
                    return ParseAggregate(function, AggregateKind.Count, running ?? true);
                case "SUM":
                    return ParseAggregate(function, AggregateKind.Sum, running ?? true);
                case "AVG":
                    return ParseAggregate(function, AggregateKind.Avg, running ?? true);
                case "MIN":
                    return ParseAggregate(function, AggregateKind.Min, running ?? true);
                case "MAX":
                    return ParseAggregate(function, AggregateKind.Max, running ?? true);
                case "CLASSIFIER":
                    return ParseClassifier();
                case "MATCH_NUMBER":
                    Expect(ExpressionTokenKind.RParen);
                    return new MatchNumberExpr();
                case "COALESCE":
                    return ParseScalar(function, ScalarFunction.Coalesce);
                case "NULLIF":
                    return ParseScalar(function, ScalarFunction.NullIf);
                case "ROUND":
                    return ParseScalar(function, ScalarFunction.Round);
                case "ABS":
                    return ParseScalar(function, ScalarFunction.Abs);
                case "TIMESTAMPDIFF":
                    return ParseTimestampDiff();
                case "SIMILARITY":
                    return ParseSimilarity();
                default:
                    throw new PatternValidationException($"Function '{name}' is not supported.");
            }
        }

        /// <summary><c>PREV</c>/<c>NEXT</c>/<c>FIRST</c>/<c>LAST</c> <c>(expr [, n])</c>, after the <c>(</c>.</summary>
        private Expr ParseNavigation(string function, bool running)
        {
            var physical = function is "PREV" or "NEXT";

            foreach (var enclosing in _scopes)
            {
                if (enclosing.IsAggregate)
                {
                    throw new PatternValidationException(
                        $"Cannot nest {function} pattern navigation function inside aggregate function {enclosing.Name}.");
                }

                if (!enclosing.IsPhysical || physical)
                {
                    throw new PatternValidationException(
                        $"Cannot nest {function} pattern navigation function inside {enclosing.Name}.");
                }
            }

            var scope = new CallScope(function, isAggregate: false, isPhysical: physical);
            _scopes.Add(scope);
            var argument = ParseOr();
            var offset = physical ? 1 : 0;

            if (Current.Kind == ExpressionTokenKind.Comma)
            {
                _pos++;
                offset = ParseOffset(function);
            }

            Expect(ExpressionTokenKind.RParen);
            _scopes.RemoveAt(_scopes.Count - 1);

            if (!scope.HasRead)
            {
                throw new PatternValidationException(
                    $"Pattern navigation function '{function}' must contain at least one column reference or CLASSIFIER().");
            }

            if (scope.NestedNavigations.Count > 0 &&
                (scope.NestedNavigations.Count > 1 || !ReferenceEquals(scope.NestedNavigations[0], argument)))
            {
                throw new PatternValidationException(
                    $"Immediate nesting is required for pattern navigation functions: {function} must take the nested FIRST or LAST call as its whole first argument.");
            }

            var last = function == "LAST";
            var lowered = physical
                ? Navigate(argument, navigation => navigation.WithPhysicalOffset(function == "PREV" ? -offset : offset))
                : Navigate(argument, navigation => navigation with { Last = last, Running = running, LogicalOffset = offset });

            if (_scopes.Count > 0)
            {
                _scopes[^1].NestedNavigations.Add(lowered);
            }

            return lowered;
        }

        /// <summary>The optional second argument of a navigation: an unsigned integer literal.</summary>
        private int ParseOffset(string function)
        {
            if (Current.Kind == ExpressionTokenKind.Minus && Peek(1).Kind == ExpressionTokenKind.Integer)
            {
                throw new PatternValidationException(
                    $"{function} pattern navigation function requires a non-negative number as the second argument.");
            }

            if (Current.Kind != ExpressionTokenKind.Integer || Peek(1).Kind != ExpressionTokenKind.RParen)
            {
                throw new PatternValidationException(
                    $"{function} pattern navigation function requires a number as the second argument.");
            }

            var value = (long)Current.Value!;

            if (value > int.MaxValue)
            {
                throw new PatternValidationException(
                    $"{function} pattern navigation function offset {value.ToString(CultureInfo.InvariantCulture)} exceeds {int.MaxValue.ToString(CultureInfo.InvariantCulture)}.");
            }

            _pos++;
            return (int)value;
        }

        /// <summary><c>COUNT(*)</c>, <c>COUNT(v.*)</c> or <c>COUNT|SUM|AVG|MIN|MAX(expr)</c>, after the <c>(</c>.</summary>
        private Expr ParseAggregate(string function, AggregateKind kind, bool running)
        {
            if (_scopes.Count > 0)
            {
                var enclosing = _scopes[^1];
                throw new PatternValidationException(enclosing.IsAggregate
                    ? $"Cannot nest aggregate function {function} inside aggregate function {enclosing.Name}."
                    : $"Cannot nest aggregate function {function} inside pattern navigation function {enclosing.Name}.");
            }

            if (kind == AggregateKind.Count)
            {
                if (Current.Kind == ExpressionTokenKind.Star && Peek(1).Kind == ExpressionTokenKind.RParen)
                {
                    _pos += 2;
                    return new AggregateExpr(kind, [], null, running, ClassifierInvolved: false);
                }

                if (Current.Kind == ExpressionTokenKind.Identifier &&
                    Peek(1).Kind == ExpressionTokenKind.Dot &&
                    Peek(2).Kind == ExpressionTokenKind.Star &&
                    Peek(3).Kind == ExpressionTokenKind.RParen)
                {
                    var qualified = ResolveQualifier(Current.Text, "*");
                    _pos += 4;
                    return new AggregateExpr(kind, qualified, null, running, ClassifierInvolved: false);
                }
            }

            var scope = new CallScope(function, isAggregate: true, isPhysical: false);
            _scopes.Add(scope);
            var argument = ParseOr();
            Expect(ExpressionTokenKind.RParen);
            _scopes.RemoveAt(_scopes.Count - 1);

            return new AggregateExpr(kind, scope.Labels ?? [], argument, running, scope.ClassifierInvolved);
        }

        /// <summary><c>CLASSIFIER()</c> or <c>CLASSIFIER(v)</c>, after the <c>(</c>.</summary>
        private Expr ParseClassifier()
        {
            int[] labelSet = [];

            if (Current.Kind == ExpressionTokenKind.Identifier)
            {
                var name = Current.Text;
                if (!owner._qualifiers.TryGetValue(name, out var qualified))
                {
                    throw new PatternValidationException(
                        $"'{name}' is not a primary pattern variable or subset name.");
                }

                labelSet = qualified;
                _pos++;
            }

            Expect(ExpressionTokenKind.RParen);
            RegisterRead(labelSet, classifier: true);
            return new ClassifierExpr(ReadNavigation(labelSet));
        }

        /// <summary><c>COALESCE</c> (≥ 1 argument), <c>NULLIF</c> (2), <c>ROUND</c> (1, or 2 with an integer-literal
        /// second argument), <c>ABS</c> (1), after the <c>(</c>.</summary>
        private Expr ParseScalar(string function, ScalarFunction kind)
        {
            var arguments = new List<Expr>();

            if (Current.Kind != ExpressionTokenKind.RParen)
            {
                arguments.Add(ParseOr());
                while (Current.Kind == ExpressionTokenKind.Comma)
                {
                    _pos++;
                    arguments.Add(ParseOr());
                }
            }

            Expect(ExpressionTokenKind.RParen);

            var valid = kind switch
            {
                ScalarFunction.Coalesce => arguments.Count >= 1,
                ScalarFunction.NullIf => arguments.Count == 2,
                ScalarFunction.Round => arguments.Count == 1 ||
                                        (arguments.Count == 2 && arguments[1] is LiteralExpr { Value: long }),
                _ => arguments.Count == 1,
            };

            if (!valid)
            {
                var expected = kind switch
                {
                    ScalarFunction.Coalesce => "at least one argument",
                    ScalarFunction.NullIf => "exactly two arguments",
                    ScalarFunction.Round => "one argument, or two with an integer literal as the second",
                    _ => "exactly one argument",
                };
                throw new PatternValidationException($"{function} requires {expected}.");
            }

            return new FunctionExpr(kind, arguments);
        }

        /// <summary><c>TIMESTAMPDIFF(unit, a, b)</c>, after the <c>(</c>.</summary>
        private Expr ParseTimestampDiff()
        {
            var unitText = Current.Kind == ExpressionTokenKind.Identifier ? Current.Text : null;
            TimeUnit? unit = unitText?.ToUpperInvariant() switch
            {
                "SECOND" => TimeUnit.Second,
                "MINUTE" => TimeUnit.Minute,
                "HOUR" => TimeUnit.Hour,
                "DAY" => TimeUnit.Day,
                _ => null,
            };

            if (unit is null)
            {
                throw new PatternValidationException(
                    $"TIMESTAMPDIFF unit '{Current.Text}' is not supported; use SECOND, MINUTE, HOUR or DAY.");
            }

            _pos++;
            Expect(ExpressionTokenKind.Comma);
            var from = ParseOr();
            Expect(ExpressionTokenKind.Comma);
            var to = ParseOr();
            Expect(ExpressionTokenKind.RParen);
            return new TimestampDiffExpr(unit.Value, from, to);
        }

        /// <summary><c>SIMILARITY(colref, 'text')</c>, after the <c>(</c>.</summary>
        private Expr ParseSimilarity()
        {
            string column;
            int[] labelSet;

            if (IsColumnName(Current) && Peek(1).Kind == ExpressionTokenKind.Dot &&
                IsColumnName(Peek(2)) && Peek(3).Kind == ExpressionTokenKind.Comma)
            {
                column = Peek(2).Text;
                labelSet = ResolveQualifier(Current.Text, column);
                _pos += 3;
            }
            else if (IsColumnName(Current) && Peek(1).Kind == ExpressionTokenKind.Comma)
            {
                column = Current.Text;
                labelSet = [];
                _pos++;
            }
            else
            {
                throw new PatternValidationException(
                    "SIMILARITY requires a column reference as its first argument.");
            }

            _pos++; // the comma

            if (Current.Kind != ExpressionTokenKind.String || ((string)Current.Value!).Length == 0 ||
                Peek(1).Kind != ExpressionTokenKind.RParen)
            {
                throw new PatternValidationException(
                    "SIMILARITY requires a non-empty string literal as its second argument.");
            }

            var text = (string)Current.Value!;
            _pos += 2;

            owner.ReferencedColumns.Add(column);
            RegisterRead(labelSet, classifier: false);
            return new SimilarityExpr(owner._similarityTerms.GetOrAdd(column, text), ReadNavigation(labelSet));
        }

        // --- Reads ---

        private ColumnExpr Column(string column, int[] labelSet)
        {
            owner.ReferencedColumns.Add(column);
            RegisterRead(labelSet, classifier: false);
            return new ColumnExpr(column, ReadNavigation(labelSet));
        }

        /// <summary>Records a read's label set in every enclosing call, which must all see a single label set.</summary>
        private void RegisterRead(int[] labelSet, bool classifier)
        {
            foreach (var scope in _scopes)
            {
                if (scope.Labels is null)
                {
                    scope.Labels = labelSet;
                }
                else if (!scope.Labels.AsSpan().SequenceEqual(labelSet))
                {
                    throw new PatternValidationException(
                        $"All labels and classifiers inside the call to '{scope.Name}' must match.");
                }

                scope.HasRead = true;
                scope.ClassifierInvolved |= classifier;
            }
        }

        /// <summary>A read's navigation before any enclosing navigation call adjusts it: <c>RUNNING LAST(v, 0)</c>,
        /// or null inside an aggregate's argument (the aggregated row).</summary>
        private Navigation? ReadNavigation(int[] labelSet) =>
            _scopes.Exists(scope => scope.IsAggregate)
                ? null
                : new Navigation(labelSet, Last: true, Running: true, LogicalOffset: 0, PhysicalOffset: 0);

        private int[] ResolveQualifier(string qualifier, string column) =>
            owner._qualifiers.TryGetValue(qualifier, out var labelSet)
                ? labelSet
                : throw new PatternValidationException($"Column '{qualifier}.{column}' cannot be resolved.");

        // --- Token helpers ---

        private static bool IsColumnName(ExpressionToken token) =>
            token.Kind == ExpressionTokenKind.Identifier && !ReservedWords.Contains(token.Text);

        private bool IsKeyword(string keyword) =>
            Current.Kind == ExpressionTokenKind.Identifier &&
            string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

        private bool TryConsumeKeyword(string keyword)
        {
            if (!IsKeyword(keyword))
            {
                return false;
            }

            _pos++;
            return true;
        }

        private void ExpectKeyword(string keyword)
        {
            if (!TryConsumeKeyword(keyword))
            {
                throw Unexpected();
            }
        }

        private void Expect(ExpressionTokenKind kind)
        {
            if (Current.Kind != kind)
            {
                throw Unexpected();
            }

            _pos++;
        }

        private string ExpectIdentifier()
        {
            if (Current.Kind != ExpressionTokenKind.Identifier)
            {
                throw Unexpected();
            }

            return tokens[_pos++].Text;
        }

        private PatternValidationException Unexpected() =>
            Current.Kind == ExpressionTokenKind.End
                ? new PatternValidationException("Unexpected end of expression.")
                : new PatternValidationException(
                    $"Unexpected token '{Current.Text}' at position {Current.Position} in expression.");
    }
}
