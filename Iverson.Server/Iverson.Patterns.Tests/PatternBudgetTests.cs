using FluentAssertions;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class PatternBudgetTests
{
    [Fact]
    public void Steps_up_to_the_limit_are_counted_and_the_next_one_throws()
    {
        var budget = new PatternBudget(maxActiveThreads: 10, maxSteps: 3);

        budget.Step(); budget.Step(); budget.Step();
        budget.StepsUsed.Should().Be(3);

        var act = budget.Step;
        act.Should().Throw<PatternBudgetExceededException>()
            .Which.BudgetName.Should().Be("MaxSteps");
    }

    [Fact]
    public void Active_threads_at_the_limit_pass_and_one_more_throws()
    {
        var budget = new PatternBudget(maxActiveThreads: 2, maxSteps: 100);

        budget.CheckActiveThreads(2);

        var act = () => budget.CheckActiveThreads(3);
        act.Should().Throw<PatternBudgetExceededException>()
            .Which.BudgetName.Should().Be("MaxActiveThreads");
    }
}
