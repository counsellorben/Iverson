namespace Iverson.Patterns;

/// <summary>A pattern, subset, <c>define</c> or <c>measures</c> entry is invalid (spec §1–§2). Maps to <c>InvalidArgument</c>.</summary>
public sealed class PatternValidationException(string message) : Exception(message);

/// <summary>A run-time evaluation error: integer division by zero, overflow, a type mismatch, an illegal
/// <c>AFTER MATCH SKIP</c> (spec §1–§2). Maps to <c>InvalidArgument</c>.</summary>
public sealed class PatternEvaluationException(string message) : Exception(message);

/// <summary>A §5 budget was exceeded. <see cref="BudgetName"/> is the limit's name, e.g. <c>MaxSteps</c>.</summary>
public sealed class PatternBudgetExceededException(string budgetName)
    : Exception($"Pattern budget exceeded: {budgetName}.")
{
    public string BudgetName { get; } = budgetName;
}
