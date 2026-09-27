using FluentAssertions;
using Iverson.Patterns.Syntax;
using Xunit;

namespace Iverson.Patterns.Tests;

public sealed class PatternParserTests
{
    private static ParsedPattern Parse(string pattern, params SubsetDefinition[] subsets) =>
        PatternParser.Parse(pattern, subsets);

    [Theory]
    [InlineData("A B+ C+", "(A B{1,} C{1,})")]
    [InlineData("a | b c", "(A | (B C))")]
    [InlineData("A (B C)", "(A (B C))")]
    [InlineData("(A)", "A")]
    [InlineData("()", "()")]
    [InlineData("^ A* $", "(^ A{0,} $)")]
    [InlineData("A*?", "A{0,}?")]
    [InlineData("A+?", "A{1,}?")]
    [InlineData("A?", "A{0,1}")]
    [InlineData("A??", "A{0,1}?")]
    [InlineData("A{2}", "A{2,2}")]
    [InlineData("A{2,}", "A{2,}")]
    [InlineData("A{,2}", "A{0,2}")]
    [InlineData("A{ 1 , 3 }?", "A{1,3}?")]
    [InlineData("A{,}", "A{0,}")]
    [InlineData("PERMUTE(a, b | c)", "PERMUTE(A, (B | C))")]
    [InlineData("permute(A)", "(PERMUTE A)")]
    [InlineData("PERMUTE()", "(PERMUTE ())")]
    [InlineData("A {- B -} C", "(A {- B -} C)")]
    [InlineData("{- A {- B -} -}", "{- (A {- B -}) -}")]
    [InlineData("(() | A)", "(() | A)")]
    [InlineData("^* | B", "(^{0,} | B)")]
    [InlineData("(A+)+ B", "(A{1,}{1,} B)")]
    [InlineData("A B | C D | E", "((A B) | (C D) | E)")]
    public void Parses_to_the_canonical_tree(string pattern, string expected) =>
        Parse(pattern).Root.ToString().Should().Be(expected);

    [Fact]
    public void Variables_are_upper_cased_deduplicated_and_in_first_appearance_order() =>
        Parse("b a+ | B c PERMUTE(c, d)").Variables.Should().Equal("B", "A", "C", "D");

    [Fact]
    public void Permute_is_a_keyword_only_before_a_parenthesis() =>
        Parse("PERMUTE LAST").Variables.Should().Equal("PERMUTE", "LAST");

    [Fact]
    public void Subsets_resolve_case_insensitively_to_upper_case()
    {
        var parsed = Parse("A B C", new SubsetDefinition("u", ["a", "C"]));

        parsed.Subsets.Should().ContainKey("U");
        parsed.Subsets["U"].Should().Equal("A", "C");
    }

    [Fact]
    public void HasExclusion_reports_an_exclusion_anywhere()
    {
        Parse("A B").HasExclusion.Should().BeFalse();
        Parse("A (B | {- C -})").HasExclusion.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A |")]
    [InlineData("| A")]
    [InlineData("A**")]
    [InlineData("A+*")]
    [InlineData("A{3,2}")]
    [InlineData("A{0}")]
    [InlineData("A{,0}")]
    [InlineData("A{0,0}")]
    [InlineData("A{")]
    [InlineData("A{x}")]
    [InlineData("A{99999999999}")]
    [InlineData("(A")]
    [InlineData("A)")]
    [InlineData("{- A")]
    [InlineData("PERMUTE(A,)")]
    [InlineData("A ; B")]
    [InlineData("A.B")]
    public void Rejects_malformed_patterns(string pattern)
    {
        var act = () => Parse(pattern);
        act.Should().Throw<PatternValidationException>();
    }

    [Fact]
    public void Rejects_a_subset_element_that_is_not_a_primary_variable()
    {
        var act = () => Parse("A B", new SubsetDefinition("U", ["A", "C"]));
        act.Should().Throw<PatternValidationException>().WithMessage("*C*");
    }

    [Fact]
    public void Rejects_a_subset_named_like_a_primary_variable_in_any_case()
    {
        var act = () => Parse("A B", new SubsetDefinition("a", ["B"]));
        act.Should().Throw<PatternValidationException>().WithMessage("*A*");
    }

    [Fact]
    public void Rejects_duplicate_subset_names_in_any_case()
    {
        var act = () => Parse("A B", new SubsetDefinition("U", ["A"]), new SubsetDefinition("u", ["B"]));
        act.Should().Throw<PatternValidationException>().WithMessage("*U*");
    }

    [Fact]
    public void Rejects_a_subset_with_no_members()
    {
        var act = () => Parse("A B", new SubsetDefinition("U", []));
        act.Should().Throw<PatternValidationException>();
    }
}
