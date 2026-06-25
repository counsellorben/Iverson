using FluentAssertions;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Schema;

public class SchemaBuilderTests
{
    [Fact]
    public void BuildDescriptor_InfersTableNameFromTypeName()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.Dimension.Returns(768);
        embedding.ModelId.Returns("nomic-embed-text");

        var typeDesc = new TypeDescriptor
        {
            TypeName   = "Article",
            Properties = { new PropertyDescriptor { Name = "Id", ClrType = ClrType.ClrGuid, IsKey = true } },
            Relations  = { }
        };

        var descriptor = SchemaBuilder.BuildDescriptor(typeDesc, embedding);

        descriptor.TableName.Should().Be("articles");
        descriptor.KeyColumn.Name.Should().Be("Id");
    }
}
