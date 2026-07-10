using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public sealed class PostgresSchemaManagerTests
{
    [Fact]
    public async Task ApplySchemaAsync_IsCalledWithCorrectTableName()
    {
        var manager = Substitute.For<IPostgresSchemaManager>();
        var schema = new TableSchema(
            "players",
            new ColumnSchema("id", "uuid", IsNullable: false),
            new List<ColumnSchema>
            {
                new("name", "text", IsNullable: false),
                new("jersey_number", "int", IsNullable: true)
            });

        await manager.ApplySchemaAsync(schema);

        await manager.Received(1).ApplySchemaAsync(Arg.Is<TableSchema>(s => s.TableName == "players"));
    }

    [Fact]
    public void PostgresSchemaManager_ImplementsIPostgresSchemaManager()
    {
        typeof(PostgresSchemaManager).Should().Implement<IPostgresSchemaManager>();
    }
}
