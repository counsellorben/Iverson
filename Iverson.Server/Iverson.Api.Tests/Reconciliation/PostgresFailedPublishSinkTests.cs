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

        // NSubstitute's Arg.Do callback does not fire when used inside a Received()
        // verification for this Task<int>-returning method (observed on this project's
        // NSubstitute version) — pull the recorded call's arguments directly instead.
        var call = sql.ReceivedCalls().Should().ContainSingle().Subject;
        var sqlText = (string)call.GetArguments()[0]!;
        sqlText.Should().Contain(ReconciliationSchema.TableName);
        sqlText.Should().Contain("INSERT INTO");

        var dynamicParams = (dynamic)call.GetArguments()[1]!;
        ((string)dynamicParams.TypeName).Should().Be("Author");
        ((string)dynamicParams.EntityKey).Should().Be("author-1");
        ((string)dynamicParams.LastError).Should().Be("broker unavailable");
        Guid.TryParse((string)dynamicParams.Id, out _).Should().BeTrue();
    }
}
