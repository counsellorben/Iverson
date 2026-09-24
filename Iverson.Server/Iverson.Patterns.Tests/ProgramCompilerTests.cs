using System.Diagnostics;
using FluentAssertions;
using Iverson.Patterns.Compilation;
using Iverson.Patterns.Syntax;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class ProgramCompilerTests
{
    internal static Instruction[] CompileText(string pattern, int maxInstructions = 5000)
    {
        var parsed = PatternParser.Parse(pattern, []);
        var labels = parsed.Variables.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        return ProgramCompiler.Compile(parsed.Root, labels, maxInstructions);
    }

    internal static string Format(Instruction[] program, IReadOnlyList<string> labelNames) =>
        string.Join(" | ", program.Select(i => i.Kind switch
        {
            InstructionKind.MatchLabel => "label " + labelNames[i.First],
            InstructionKind.Split => $"split {i.First} {i.Second}",
            InstructionKind.Jump => $"jump {i.First}",
            InstructionKind.ExclusionStart => "exclusion-start",
            InstructionKind.ExclusionEnd => "exclusion-end",
            InstructionKind.MatchStart => "start",
            InstructionKind.MatchEnd => "end",
            InstructionKind.Done => "done",
            _ => throw new ArgumentOutOfRangeException(nameof(program)),
        }));

    [Theory]
    [InlineData("A B C", "label A | label B | label C | done")]
    [InlineData("A | B | C", "split 1 3 | label A | jump 7 | split 4 6 | label B | jump 7 | label C | done")]
    [InlineData("(A B)+", "label A | label B | split 0 3 | done")]
    [InlineData("()", "done")]
    [InlineData("A*", "split 1 3 | label A | split 1 3 | done")]
    [InlineData("A*?", "split 3 1 | label A | split 3 1 | done")]
    [InlineData("A+", "label A | split 0 2 | done")]
    [InlineData("A+?", "label A | split 2 0 | done")]
    [InlineData("A?", "split 1 2 | label A | done")]
    [InlineData("A??", "split 2 1 | label A | done")]
    [InlineData("A{2}", "label A | label A | done")]
    [InlineData("A{2,}", "label A | label A | split 1 3 | done")]
    [InlineData("A{,2}", "split 1 4 | label A | split 3 4 | label A | done")]
    [InlineData("A{1,3}", "label A | split 2 5 | label A | split 4 5 | label A | done")]
    [InlineData("A{1,3}?", "label A | split 5 2 | label A | split 5 4 | label A | done")]
    [InlineData("A{,}", "split 1 3 | label A | split 1 3 | done")]
    [InlineData("PERMUTE(A, B, C)",
        "split 1 5 | label A | label B | label C | jump 28 | split 6 10 | label A | label C | label B | jump 28 | " +
        "split 11 15 | label B | label A | label C | jump 28 | split 16 20 | label B | label C | label A | jump 28 | " +
        "split 21 25 | label C | label A | label B | jump 28 | label C | label B | label A | done")]
    [InlineData("A {- B -} C", "label A | exclusion-start | label B | exclusion-end | label C | done")]
    [InlineData("{- A {- B -} -}", "exclusion-start | label A | label B | exclusion-end | done")]
    [InlineData("^ A $", "start | label A | end | done")]
    [InlineData("(() | A)", "split 2 1 | label A | done")]
    [InlineData("(A | ())", "split 1 2 | label A | done")]
    [InlineData("A () B", "label A | label B | done")]
    [InlineData("PERMUTE(A, ())", "label A | done")]
    [InlineData("(A | B)* X", "split 1 6 | split 2 4 | label A | jump 5 | label B | split 1 6 | label X | done")]
    [InlineData("(A+)+ B", "label A | split 0 2 | split 0 3 | label B | done")]
    public void Compiles_to_the_Trino_483_program(string pattern, string expected)
    {
        var parsed = PatternParser.Parse(pattern, []);
        Format(CompileText(pattern), parsed.Variables).Should().Be(expected);
    }

    [Fact]
    public void A_program_of_exactly_the_cap_compiles()
    {
        CompileText("A{4999}", maxInstructions: 5000).Should().HaveCount(5000);
        CompileText("A{0,2499} B", maxInstructions: 5000).Should().HaveCount(5000);
    }

    [Theory]
    [InlineData("A{5000}")]
    [InlineData("PERMUTE(A, B, C, D, E, F, G, H)")]
    [InlineData("PERMUTE(A, B, C, D, E, F, G, H, I, J, K, L)")]
    [InlineData("A{100000}")]
    [InlineData("(A{0,100}){0,100}")]
    public void A_program_over_the_cap_is_rejected_promptly(string pattern)
    {
        var watch = Stopwatch.StartNew();
        var act = () => CompileText(pattern, maxInstructions: 5000);

        act.Should().Throw<PatternValidationException>().WithMessage("*MaxProgramInstructions*");
        // PERMUTE of 12 is 6,706,022,399 instructions uncapped; a rejection after at most the cap takes
        // milliseconds (CDR-7 P84: 2–11 ms).
        watch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }
}
