using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public sealed class PostgresSchemaManagerTests
{
    [Fact]
    public async Task ApplySchemaAsync_IsCalledWithCorrectTableName()
    {
        var manager = Substitute.For<IRecordStoreSchemaManager>();
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
        typeof(PostgresSchemaManager).Should().Implement<IRecordStoreSchemaManager>();
    }

    [Fact]
    public async Task ApplySchemaAsync_TableNameContainingADoubleQuote_IsRefusedBeforeConnecting()
    {
        // Nothing listens on port 1, so reaching the connection would fail with a connection error
        // instead. An ArgumentException means the name was refused before any connection was made.
        var manager = new PostgresSchemaManager(
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1",
            NullLogger<PostgresSchemaManager>.Instance);
        var schema = new TableSchema(
            "play\"ers",
            new ColumnSchema("id", "uuid", IsNullable: false),
            new List<ColumnSchema> { new("name", "text", IsNullable: false) });

        var act = () => manager.ApplySchemaAsync(schema);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
