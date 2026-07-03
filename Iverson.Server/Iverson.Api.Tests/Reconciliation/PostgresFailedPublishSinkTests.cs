using FluentAssertions;
using Iverson.Api.Reconciliation;
using Iverson.Sql;
using NSubstitute;
using Xunit;

namespace Iverson.Api.Tests.Reconciliation;

public class PostgresFailedPublishSinkTests
{
    [Fact]
    public async Task RecordAsync_InsertsRowIntoReconciliationQueueTable()
    {
        var sql = Substitute.For<IPostgresRepository>();
        sql.ExecuteAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(1);
        var sut = new PostgresFailedPublishSink(sql);

        await sut.RecordAsync("Author", "author-1", "broker unavailable");

        object? capturedParams = null;
        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains(ReconciliationSchema.TableName) && s.Contains("INSERT INTO")),
            Arg.Do<object?>(p => capturedParams = p));

        capturedParams.Should().NotBeNull();
        var dynamicParams = (dynamic)capturedParams!;
        ((string)dynamicParams.TypeName).Should().Be("Author");
        ((string)dynamicParams.EntityKey).Should().Be("author-1");
        ((string)dynamicParams.LastError).Should().Be("broker unavailable");
        Guid.TryParse((string)dynamicParams.Id, out _).Should().BeTrue();
    }
}
