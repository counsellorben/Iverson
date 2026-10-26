using NSubstitute;
using Xunit;

namespace Iverson.Sql.Tests;

public class TokenRevocationRepositoryTests
{
    // CSR round 12 §2.3: a revocation notifies the api replicas' listeners in its own statement, so the
    // notification is sent exactly when the revocation commits.
    [Fact]
    public async Task RevokeAsync_NotifiesTheAuthInvalidationChannel_InTheSameStatement()
    {
        var sql = Substitute.For<IRecordStoreQueryExecutor>();

        await new TokenRevocationRepository(sql).RevokeAsync("user-uid-1");

        await sql.Received(1).ExecuteAsync(
            Arg.Is<string>(s => s.Contains("INSERT INTO iverson_token_revocations")
                                && s.Contains("RETURNING pg_notify('iverson_auth_invalidation', 'token-revocation')")),
            Arg.Any<object?>());
    }
}
