using FluentAssertions;
using Grpc.Core;
using Iverson.Client.Attributes;
using Iverson.Client.Contracts;
using NSubstitute;
using Xunit;

namespace Iverson.Client.Core.Tests;

[IversonEntity]
internal sealed class NavOmissionAuthor
{
    [IversonKey] public Guid Id { get; set; }
}

[IversonEntity]
internal sealed class NavOmissionTag
{
    [IversonKey] public Guid Id { get; set; }
}

[IversonEntity]
internal sealed class NavOmissionUserArticle
{
    [IversonKey] public Guid Id { get; set; }
}

[IversonEntity]
internal sealed class NavOmissionArticle
{
    [IversonKey] public Guid Id { get; set; }

    public Guid   AuthorId { get; set; }
    public Guid[] TagIds   { get; set; } = [];

    [ManyToOne(typeof(NavOmissionAuthor))]
    public NavOmissionAuthor? Author { get; set; }

    [ManyToMany(typeof(NavOmissionTag))]
    public List<NavOmissionTag> Tags { get; set; } = [];

    [OneToMany(typeof(NavOmissionUserArticle))]
    public List<NavOmissionUserArticle> UserArticles { get; set; } = [];
}

/// <summary>
/// Verifies the write payload carries foreign keys only — never a relation navigation
/// property, in either PascalCase (descriptor form) or camelCase (Struct/JSON form). Asserting
/// only the PascalCase form would pass whether or not the omission actually works, since
/// StructConverter serializes with a camelCase naming policy.
/// </summary>
public class EntityCoordinatorNavPropertyOmissionTests
{
    [Fact]
    public async Task PersistAsync_OmitsNavProperties_InBothCasings()
    {
        var persistence = Substitute.For<ObjectPersistenceService.ObjectPersistenceServiceClient>();
        PersistRequest? captured = null;
        persistence
            .PostAsync(
                Arg.Do<PersistRequest>(r => captured = r),
                Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(new AsyncUnaryCall<PersistResponse>(
                Task.FromResult(new PersistResponse { Success = true, Key = "k" }),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { }));

        var sut = TestCoordinatorFactory.Create<NavOmissionArticle>(persistence: persistence);

        var article = new NavOmissionArticle
        {
            Id           = Guid.NewGuid(),
            AuthorId     = Guid.NewGuid(),
            TagIds       = [Guid.NewGuid()],
            Author       = new NavOmissionAuthor { Id = Guid.NewGuid() },
            Tags         = [new NavOmissionTag { Id = Guid.NewGuid() }],
            UserArticles = [new NavOmissionUserArticle()]
        };

        await sut.PersistAsync(article);

        captured.Should().NotBeNull();
        var fields = captured!.Payload.Fields;

        fields.Should().ContainKey("authorId");
        fields.Should().ContainKey("tagIds");

        fields.Should().NotContainKey("Author");
        fields.Should().NotContainKey("author");
        fields.Should().NotContainKey("Tags");
        fields.Should().NotContainKey("tags");
        fields.Should().NotContainKey("UserArticles");
        fields.Should().NotContainKey("userArticles");
    }
}
