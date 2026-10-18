using FluentAssertions;
using Iverson.StarRocks;
using Xunit;

namespace Iverson.StarRocks.Tests;

/// <summary>
/// CSR round 12 §2.3. The StarRocks upsert writes a payload entry only when its key is exactly a
/// column name. StarRocks resolves column names case-insensitively, so a re-cased key such as
/// <c>OWNERID</c> would otherwise be written into the <c>OwnerId</c> column. No server is needed.
/// </summary>
public class EngagementUpsertEntriesTests
{
    private static readonly EngagementTableSchema Schema = new(
        "notes",
        new EngagementColumnSchema("Id", "VARCHAR(36)", false),
        [
            new EngagementColumnSchema("Title",   "STRING", true),
            new EngagementColumnSchema("OwnerId", "STRING", true),
            new EngagementColumnSchema("url",     "STRING", true),
        ]);

    [Fact]
    public void KeysThatAreExactlyColumnNames_AreSelected()
    {
        var entries = EngagementRepository.UpsertEntries(Schema,
            """{"Id":"k1","Title":"t","OwnerId":"o","url":"/a"}""");

        entries.Select(e => e.Key).Should().BeEquivalentTo(["Id", "Title", "OwnerId", "url"]);
    }

    [Fact]
    public void AKeyThatMatchesAColumnOnlyIgnoringCase_IsNotSelected()
    {
        var entries = EngagementRepository.UpsertEntries(Schema,
            """{"Id":"k1","OWNERID":"victim","id":"k2","Url":"/b"}""");

        entries.Select(e => e.Key).Should().Equal("Id");
    }

    [Fact]
    public void AnObjectValuedEntry_IsNotSelected()
    {
        var entries = EngagementRepository.UpsertEntries(Schema,
            """{"Id":"k1","Title":{"nested":1}}""");

        entries.Select(e => e.Key).Should().Equal("Id");
    }
}
