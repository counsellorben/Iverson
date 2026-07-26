using FluentAssertions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksSchemaManagerTests
{
    [Fact]
    public void BuildCreateTableDdl_EmitsPrimaryKey()
    {
        var schema = new EngagementTableSchema(
            "articles",
            new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Title", "STRING", false)]);

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema, $"`{schema.TableName}`");

        ddl.Should().Contain("PRIMARY KEY(`Id`)");
        ddl.Should().NotContain("UNIQUE KEY");
        ddl.Should().Contain("CREATE TABLE IF NOT EXISTS `articles`");
        ddl.Should().Contain("`Id` VARCHAR(36) NOT NULL");
        ddl.Should().Contain("`Title` STRING NOT NULL");
    }

    [Fact]
    public void BuildCreateTableDdl_NullableColumn_OmitsNotNull()
    {
        var schema = new EngagementTableSchema(
            "authors",
            new EngagementColumnSchema("Id",  "VARCHAR(36)", false),
            [new EngagementColumnSchema("Bio", "STRING",     true)]);

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema, $"`{schema.TableName}`");

        ddl.Should().Contain("`Bio` STRING\n");
        ddl.Should().NotContain("`Bio` STRING NOT NULL");
    }

    [Fact]
    public void BuildCreateTableDdl_EmitsOrderBy_WhenSortKeyIsPopulated()
    {
        var schema = new EngagementTableSchema(
            "articles",
            new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [
                new EngagementColumnSchema("Category",    "STRING",   false),
                new EngagementColumnSchema("PublishedAt", "DATETIME", false),
            ])
        {
            SortKey = ["Category", "PublishedAt"]
        };

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema, $"`{schema.TableName}`");

        ddl.Should().Contain("ORDER BY (`Category`, `PublishedAt`)");
        ddl.Should().Contain("PRIMARY KEY(`Id`)");
    }

    [Fact]
    public void BuildCreateTableDdl_OmitsOrderBy_WhenNoSortKey()
    {
        var schema = new EngagementTableSchema(
            "authors",
            new EngagementColumnSchema("Id", "VARCHAR(36)", false),
            [new EngagementColumnSchema("Name", "STRING", false)]);

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema, $"`{schema.TableName}`");

        ddl.Should().NotContain("ORDER BY");
    }
}
