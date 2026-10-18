using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public sealed class ExistingTableInspectorTests
{
    [Fact]
    public async Task HasRowsOfAnotherTenant_WithANullOwner_Throws()
    {
        var inspector = new ExistingTableInspector(Substitute.For<IRecordStoreQueryExecutor>());

        await inspector.Invoking(i => i.HasRowsOfAnotherTenantAsync("widgets", "__TenantId", null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
