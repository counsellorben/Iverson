using FluentAssertions;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Xunit;

namespace Iverson.Sql.Tests;

public sealed class PostgresRepositoryTests
{
    // ─── Interface contract tests (mocked) ───────────────────────────────────

    [Fact]
    public async Task QueryAsync_ReturnsExpectedResults()
    {
        var repo = Substitute.For<IPostgresQueryExecutor>();
        var expected = new[] { new { Id = 1, Name = "Allen" } };
        repo.QueryAsync<object>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(expected);

        var result = await repo.QueryAsync<object>("SELECT * FROM players");

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsRowCount()
    {
        var repo = Substitute.For<IPostgresQueryExecutor>();
        repo.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(3);

        var result = await repo.ExecuteAsync("UPDATE players SET active = true");

        result.Should().Be(3);
    }

    [Fact]
    public async Task QuerySingleOrDefaultAsync_ReturnsNull_WhenNotFound()
    {
        var repo = Substitute.For<IPostgresQueryExecutor>();
        repo.QuerySingleOrDefaultAsync<object>(Arg.Any<string>(), Arg.Any<object?>())
            .ReturnsNull();

        var result = await repo.QuerySingleOrDefaultAsync<object>("SELECT * FROM players WHERE id = @id", new { id = 999 });

        result.Should().BeNull();
    }

    [Fact]
    public async Task ApplySchemaAsync_IsCalledWithCorrectTableName()
    {
        var repo = Substitute.For<IPostgresSchemaManager>();
        var schema = new TableSchema(
            "players",
            new ColumnSchema("id", "uuid", IsNullable: false),
            new List<ColumnSchema>
            {
                new("name", "text", IsNullable: false),
                new("jersey_number", "int", IsNullable: true)
            });

        await repo.ApplySchemaAsync(schema);

        await repo.Received(1).ApplySchemaAsync(Arg.Is<TableSchema>(s => s.TableName == "players"));
    }

    // ─── Schema record tests (pure value) ────────────────────────────────────

    [Fact]
    public void TableSchema_StoresTableName()
    {
        var schema = new TableSchema(
            "seasons",
            new ColumnSchema("id", "uuid", IsNullable: false),
            new List<ColumnSchema>());

        schema.TableName.Should().Be("seasons");
    }

    [Fact]
    public void ColumnSchema_StoresNullability()
    {
        var nullable = new ColumnSchema("nickname", "text", IsNullable: true);
        var notNullable = new ColumnSchema("id", "uuid", IsNullable: false);

        nullable.IsNullable.Should().BeTrue();
        notNullable.IsNullable.Should().BeFalse();
    }

    [Fact]
    public void PostgresRepository_ImplementsAllThreeRoleInterfaces()
    {
        typeof(PostgresRepository).Should().Implement<IPostgresQueryExecutor>();
        typeof(PostgresRepository).Should().Implement<IPostgresSchemaManager>();
        typeof(PostgresRepository).Should().Implement<IPostgresTransactionRunner>();
    }

}
