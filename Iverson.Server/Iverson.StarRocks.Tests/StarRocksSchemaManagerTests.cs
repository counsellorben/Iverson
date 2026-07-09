using FluentAssertions;
using Xunit;

namespace Iverson.StarRocks.Tests;

public class StarRocksSchemaManagerTests
{
    [Fact]
    public void BuildCreateTableDdl_EmitsPrimaryKey()
    {
        var schema = new StarRocksTableSchema(
            "articles",
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Title", "STRING", false)]);

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema);

        ddl.Should().Contain("PRIMARY KEY(`Id`)");
        ddl.Should().NotContain("UNIQUE KEY");
        ddl.Should().Contain("CREATE TABLE IF NOT EXISTS `articles`");
        ddl.Should().Contain("`Id` VARCHAR(36) NOT NULL");
        ddl.Should().Contain("`Title` STRING NOT NULL");
    }

    [Fact]
    public void BuildCreateTableDdl_NullableColumn_OmitsNotNull()
    {
        var schema = new StarRocksTableSchema(
            "authors",
            new StarRocksColumnSchema("Id",  "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Bio", "STRING",     true)]);

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema);

        ddl.Should().Contain("`Bio` STRING\n");
        ddl.Should().NotContain("`Bio` STRING NOT NULL");
    }

    [Fact]
    public void BuildCreateTableDdl_EmitsOrderBy_WhenSortKeyIsPopulated()
    {
        var schema = new StarRocksTableSchema(
            "articles",
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [
                new StarRocksColumnSchema("Category",    "STRING",   false),
                new StarRocksColumnSchema("PublishedAt", "DATETIME", false),
            ])
        {
            SortKey = ["Category", "PublishedAt"]
        };

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema);

        ddl.Should().Contain("ORDER BY (`Category`, `PublishedAt`)");
        ddl.Should().Contain("PRIMARY KEY(`Id`)");
    }

    [Fact]
    public void BuildCreateTableDdl_OmitsOrderBy_WhenNoSortKey()
    {
        var schema = new StarRocksTableSchema(
            "authors",
            new StarRocksColumnSchema("Id", "VARCHAR(36)", false),
            [new StarRocksColumnSchema("Name", "STRING", false)]);

        var ddl = StarRocksSchemaManager.BuildCreateTableDdl(schema);

        ddl.Should().NotContain("ORDER BY");
    }

    [Fact]
    public void StarRocksSchemaManager_ImplementsIStarRocksSchemaManager()
    {
        typeof(StarRocksSchemaManager).Should().Implement<IStarRocksSchemaManager>();
    }
}
