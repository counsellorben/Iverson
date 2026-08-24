using FluentAssertions;
using Iverson.Api.Schema;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class DocumentTemplateParserTests
{
    [Fact]
    public void Parse_LiteralOnly_Throws()
    {
        var act = () => DocumentTemplateParser.Parse("Just plain text with no placeholders.");

        act.Should().Throw<DocumentTemplateParseException>();
    }

    [Fact]
    public void Parse_EscapedBrace_ProducesLiteralSegment()
    {
        // "{{" alone is only an escaped brace, not a placeholder — still literal-only.
        var act = () => DocumentTemplateParser.Parse("Price: {{100}} dollars");

        act.Should().Throw<DocumentTemplateParseException>();
    }

    [Fact]
    public void Parse_EscapedBraceBeforeScalar_RendersLiteralBraceThenScalar()
    {
        var template = DocumentTemplateParser.Parse("{{Not a placeholder {Title}");

        template.Segments.Should().Equal(
            new DocumentSegment(DocumentSegmentKind.Literal, Text: "{Not a placeholder "),
            new DocumentSegment(DocumentSegmentKind.Scalar, PropertyName: "Title"));
    }

    [Fact]
    public void Parse_Scalar_ProducesScalarSegment()
    {
        var template = DocumentTemplateParser.Parse("Title: {Title}");

        template.Segments.Should().Equal(
            new DocumentSegment(DocumentSegmentKind.Literal, Text: "Title: "),
            new DocumentSegment(DocumentSegmentKind.Scalar, PropertyName: "Title"));
    }

    [Fact]
    public void Parse_OneHop_ProducesOneHopSegment()
    {
        var template = DocumentTemplateParser.Parse("Author: {Author.Name}");

        template.Segments.Should().Equal(
            new DocumentSegment(DocumentSegmentKind.Literal, Text: "Author: "),
            new DocumentSegment(DocumentSegmentKind.OneHop, RelationName: "Author", PropertyName: "Name"));
    }

    [Fact]
    public void Parse_Block_ProducesBlockSegmentWithInnerScalars()
    {
        var template = DocumentTemplateParser.Parse("{#Tags}- {Name}\n{/Tags}");

        template.Segments.Should().HaveCount(1);
        var block = template.Segments[0];
        block.Kind.Should().Be(DocumentSegmentKind.Block);
        block.RelationName.Should().Be("Tags");
        block.Inner.Should().Equal(
            new DocumentSegment(DocumentSegmentKind.Literal, Text: "- "),
            new DocumentSegment(DocumentSegmentKind.Scalar, PropertyName: "Name"),
            new DocumentSegment(DocumentSegmentKind.Literal, Text: "\n"));
    }

    [Fact]
    public void Parse_UnclosedBlock_Throws()
    {
        var act = () => DocumentTemplateParser.Parse("{#Tags}- {Name}");

        act.Should().Throw<DocumentTemplateParseException>()
            .Which.Placeholder.Should().Be("{#Tags}");
    }

    [Fact]
    public void Parse_NestedBlock_Throws()
    {
        var act = () => DocumentTemplateParser.Parse("{#Tags}{#Inner}{/Inner}{/Tags}");

        act.Should().Throw<DocumentTemplateParseException>()
            .Which.Placeholder.Should().Be("{#Inner}");
    }

    [Fact]
    public void Parse_MismatchedCloseTag_Throws()
    {
        var act = () => DocumentTemplateParser.Parse("{#Tags}- {Name}{/Authors}");

        act.Should().Throw<DocumentTemplateParseException>()
            .Which.Placeholder.Should().Be("{/Authors}");
    }

    [Fact]
    public void Parse_UnparseablePlaceholder_Throws()
    {
        var act = () => DocumentTemplateParser.Parse("{Not Valid}");

        act.Should().Throw<DocumentTemplateParseException>()
            .Which.Placeholder.Should().Be("{Not Valid}");
    }

    [Fact]
    public void Parse_TwoHopPlaceholder_Throws()
    {
        var act = () => DocumentTemplateParser.Parse("{Author.Company.Name}");

        act.Should().Throw<DocumentTemplateParseException>()
            .Which.Placeholder.Should().Be("{Author.Company.Name}");
    }

    [Fact]
    public void Parse_DottedPlaceholderInsideBlock_Throws()
    {
        var act = () => DocumentTemplateParser.Parse("{#Tags}{Tag.Name}{/Tags}");

        act.Should().Throw<DocumentTemplateParseException>()
            .Which.Placeholder.Should().Be("{Tag.Name}");
    }
}
