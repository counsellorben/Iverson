using Dapper;
using FluentAssertions;
using Grpc.Core;
using Iverson.Api.Grpc;
using Iverson.Api.PrivilegedRequests;
using Iverson.Api.Schema;
using Iverson.Client.Contracts;
using Iverson.Embeddings;
using Iverson.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;
using static Iverson.Api.Tests.PrivilegedRequests.PrivilegedRequestTestData;

namespace Iverson.Api.Tests.PrivilegedRequests;

public sealed class PrivilegedRequestPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public string ConnectionString { get; private set; } = null!;
    public PostgresRepository Repository { get; private set; } = null!;
    public PostgresSchemaManager SchemaManager { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Repository = new PostgresRepository(ConnectionString, NullLogger<PostgresRepository>.Instance);
        SchemaManager = new PostgresSchemaManager(ConnectionString, NullLogger<PostgresSchemaManager>.Instance);

        // The worker's startup order: roles, the registry table, then the plumbing tables.
        await SchemaManager.EnsureRolesAsync();
        await new SchemaRegistryRepository(Repository).EnsureTableAsync();
        await SchemaManager.ApplySchemaAsync(PrivilegedRequestSchema.Table);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// CSR round 12 §5 against real Postgres: the request table's claim and expiry, the registry's
/// owner read and conditional write, adoption, drift, the advisory locks, and one registration
/// end to end through the queue and a running worker.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ContainerCollection.Name)]
public sealed class PrivilegedRequestPostgresIntegrationTests(PrivilegedRequestPostgresFixture fixture)
    : IClassFixture<PrivilegedRequestPostgresFixture>
{
    private PrivilegedRequestRepository Requests => new(PrivilegedRequestSchema.TableName, fixture.Repository);
    private SchemaRegistryRepository Registry => new(fixture.Repository);

    private SchemaRegistry NewRegistry() => new(Registry, NullLogger<SchemaRegistry>.Instance);

    private SchemaRegistrationApplier Applier() => new(
        Registry,
        new ExistingTableInspector(fixture.Repository),
        fixture.SchemaManager,
        Substitute.For<IDocumentRerenderQueueRepository>());

    private PrivilegedRequestExecutor NewExecutor()
    {
        var registry = NewRegistry();
        return new PrivilegedRequestExecutor(
            registry,
            Applier(),
            new Iverson.Api.Reconciliation.ReconciliationService(
                registry, Substitute.For<IEntityRepository>(), Substitute.For<IReconciliationQueueRepository>(),
                Substitute.For<Iverson.Events.IEventProducer>(), NullLogger<Iverson.Api.Reconciliation.ReconciliationService>.Instance),
            Substitute.For<IDlqRepository>(),
            Substitute.For<Iverson.Events.IEventProducer>(),
            new PostgresPrivilegedRequestLocks(fixture.ConnectionString),
            NullLogger<PrivilegedRequestExecutor>.Instance);
    }

    private Task<string?> StatusAsync(Guid id) =>
        fixture.Repository.QuerySingleOrDefaultAsync<string>(
            $"""SELECT "Status" FROM "{PrivilegedRequestSchema.TableName}" WHERE "Id" = @Id""", new { Id = id });

    private async Task<Guid> InsertAsync(TimeSpan? expiresAfter)
    {
        var id = Guid.NewGuid();
        await Requests.InsertAsync(id, PrivilegedRequestKinds.RegisterSchema, "{}", "tenant-a", expiresAfter);
        return id;
    }

    private static async Task AssertRefusedAsync(Func<Task> act, StatusCode code, string detail) =>
        (await act.Should().ThrowAsync<RpcException>())
            .Where(e => e.StatusCode == code && e.Status.Detail.Contains(detail));

    // ── The request table ────────────────────────────────────────────────────

    [Fact]
    public async Task TryClaim_TheSameRequestTwice_SucceedsOnce()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));

        (await Requests.TryClaimAsync(id)).Should().BeTrue();
        (await Requests.TryClaimAsync(id)).Should().BeFalse();
        (await StatusAsync(id)).Should().Be(PrivilegedRequestStatus.Running);
    }

    [Fact]
    public async Task ARequestTheApiExpired_CannotBeClaimed()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));

        (await Requests.ExpireIfPendingAsync(id)).Should().BeTrue();

        (await Requests.TryClaimAsync(id)).Should().BeFalse();
        (await StatusAsync(id)).Should().Be(PrivilegedRequestStatus.Expired);
    }

    [Fact]
    public async Task ARequestTheWorkerClaimed_CannotBeExpired()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));

        (await Requests.TryClaimAsync(id)).Should().BeTrue();

        (await Requests.ExpireIfPendingAsync(id)).Should().BeFalse();
        (await StatusAsync(id)).Should().Be(PrivilegedRequestStatus.Running);
    }

    [Fact]
    public async Task APendingRequestPastItsExpiry_IsNeitherListedNorClaimable_WhileOneWithNoExpiryIs()
    {
        var expired = await InsertAsync(TimeSpan.Zero);
        var neverExpires = await InsertAsync(expiresAfter: null);

        var listed = (await Requests.ListClaimableAsync(1000)).Select(r => r.Id).ToList();

        listed.Should().NotContain(expired).And.Contain(neverExpires);
        (await Requests.TryClaimAsync(expired)).Should().BeFalse();
        (await Requests.TryClaimAsync(neverExpires)).Should().BeTrue();
    }

    [Fact]
    public async Task Complete_RecordsTheStatusAndTheResult()
    {
        var id = await InsertAsync(TimeSpan.FromSeconds(15));
        await Requests.TryClaimAsync(id);

        await Requests.CompleteAsync(id, PrivilegedRequestStatus.Failed,
            new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied").ToJson());

        var row = await Requests.GetAsync(id);
        row!.Status.Should().Be(PrivilegedRequestStatus.Failed);
        PrivilegedRequestResult.From(row).Should().Be(new PrivilegedRequestResult(StatusCode.PermissionDenied, "denied"));
    }

    [Fact]
    public async Task DeleteFinishedOlderThan_RemovesOnlyOldFinishedRequests()
    {
        var oldSucceeded = await InsertAsync(expiresAfter: null);
        await Requests.CompleteAsync(oldSucceeded, PrivilegedRequestStatus.Succeeded, PrivilegedRequestResult.Ok().ToJson());
        var oldPendingPastExpiry = await InsertAsync(TimeSpan.Zero);
        var oldRunning = await InsertAsync(expiresAfter: null);
        await Requests.TryClaimAsync(oldRunning);
        var oldPendingNoExpiry = await InsertAsync(expiresAfter: null);
        var recentFailed = await InsertAsync(expiresAfter: null);
        await Requests.CompleteAsync(recentFailed, PrivilegedRequestStatus.Failed, PrivilegedRequestResult.Ok().ToJson());
        await fixture.Repository.ExecuteAsync(
            $"""UPDATE "{PrivilegedRequestSchema.TableName}" SET "RequestedAt" = now() - interval '2 days' WHERE "Id" = ANY(@Ids)""",
            new { Ids = new[] { oldSucceeded, oldPendingPastExpiry, oldRunning, oldPendingNoExpiry } });

        var deleted = await Requests.DeleteFinishedOlderThanAsync(TimeSpan.FromDays(1));

        deleted.Should().Be(2);
        (await Requests.GetAsync(oldSucceeded)).Should().BeNull();
        (await Requests.GetAsync(oldPendingPastExpiry)).Should().BeNull();
        (await Requests.GetAsync(oldRunning)).Should().NotBeNull();
        (await Requests.GetAsync(oldPendingNoExpiry)).Should().NotBeNull();
        (await Requests.GetAsync(recentFailed)).Should().NotBeNull();
    }

    [Fact]
    public async Task FailAbandoned_FailsOnlyRequestsClaimedLongerAgoThanTheCutoff()
    {
        var abandoned = await InsertAsync(expiresAfter: null);
        await Requests.TryClaimAsync(abandoned);
        var recentlyClaimed = await InsertAsync(expiresAfter: null);
        await Requests.TryClaimAsync(recentlyClaimed);
        var neverClaimed = await InsertAsync(expiresAfter: null);
        // Ages the claim time the claim itself recorded, so a claim that recorded none stays NULL
        // and matches nothing. The never-claimed row is aged too: a pending row is never failed.
        await fixture.Repository.ExecuteAsync(
            $"""UPDATE "{PrivilegedRequestSchema.TableName}" SET "ClaimedAt" = "ClaimedAt" - interval '2 hours', "RequestedAt" = "RequestedAt" - interval '2 hours' WHERE "Id" = ANY(@Ids)""",
            new { Ids = new[] { abandoned, neverClaimed } });
        var result = new PrivilegedRequestResult(StatusCode.Unavailable, "The worker stopped before finishing this request.");

        var failed = await Requests.FailAbandonedAsync(TimeSpan.FromHours(1), result.ToJson());

        failed.Should().Be(1);
        var row = await Requests.GetAsync(abandoned);
        row!.Status.Should().Be(PrivilegedRequestStatus.Failed);
        PrivilegedRequestResult.From(row).Should().Be(result);
        (await StatusAsync(recentlyClaimed)).Should().Be(PrivilegedRequestStatus.Running);
        (await StatusAsync(neverClaimed)).Should().Be(PrivilegedRequestStatus.Pending);
    }

    // ── The registry's owner read and conditional write ──────────────────────

    [Fact]
    public async Task GetOwners_MatchesTheTypeNameIgnoringCase()
    {
        (await Registry.UpsertIfOwnerAsync("OwnerProbe", """{"ownerTenantId":"tenant-a"}""", "tenant-a")).Should().BeTrue();

        (await Registry.GetOwnersAsync("ownerprobe")).Should().Equal("tenant-a");
        (await Registry.GetOwnersAsync("NoSuchProbe")).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertIfOwner_ARowOfAnotherOwner_IsLeftUnchanged()
    {
        await Registry.UpsertIfOwnerAsync("ContestProbe", """{"ownerTenantId":"tenant-a","marker":1}""", "tenant-a");

        (await Registry.UpsertIfOwnerAsync("ContestProbe", """{"ownerTenantId":"tenant-b","marker":2}""", "tenant-b")).Should().BeFalse();
        (await Registry.GetOwnersAsync("ContestProbe")).Should().Equal("tenant-a");

        (await Registry.UpsertIfOwnerAsync("ContestProbe", """{"ownerTenantId":"tenant-a","marker":3}""", "tenant-a")).Should().BeTrue();
        (await fixture.Repository.QuerySingleOrDefaultAsync<string>(
            "SELECT schema_json->>'marker' FROM _iverson_schema WHERE type_name = 'ContestProbe'")).Should().Be("3");
    }

    [Fact]
    public async Task UpsertIfOwner_ALegacyRowWithNoOwnerKey_CountsAsOwnerless()
    {
        await Registry.UpsertAsync("LegacyProbe", """{"typeName":"LegacyProbe"}""");

        (await Registry.GetOwnersAsync("LegacyProbe")).Should().Equal((string?)null);
        (await Registry.UpsertIfOwnerAsync("LegacyProbe", """{"ownerTenantId":"tenant-a"}""", "tenant-a")).Should().BeFalse();
        (await Registry.UpsertIfOwnerAsync("LegacyProbe", """{"ownerTenantId":null}""", null)).Should().BeTrue();
    }

    // ── The worker's RegisterSchema ──────────────────────────────────────────

    [Fact]
    public async Task TwoOwnersRacingForANewName_ExactlyOneWins()
    {
        var executor = NewExecutor();

        var results = await Task.WhenAll(
            executor.RunAsync(RegisterRow(Descriptor("RaceProbe", "tenant-a")), CancellationToken.None),
            executor.RunAsync(RegisterRow(Descriptor("RaceProbe", "tenant-b")), CancellationToken.None));

        results.Select(r => r.Code).Should().BeEquivalentTo(new[] { StatusCode.OK, StatusCode.PermissionDenied });
        var winner = results[0].Code == StatusCode.OK ? "tenant-a" : "tenant-b";
        (await Registry.GetOwnersAsync("RaceProbe")).Should().Equal(winner);
    }

    [Fact]
    public async Task ACaseVariantOfAnotherTenantsType_IsPermissionDenied()
    {
        await Applier().ExecuteAsync([Descriptor("CaseProbe", "tenant-a")], [], CancellationToken.None);

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("caseprobe", "tenant-b")], [], CancellationToken.None),
            StatusCode.PermissionDenied, "registered to another tenant");

        (await Registry.GetOwnersAsync("CASEPROBE")).Should().Equal("tenant-a");
    }

    [Fact]
    public async Task ANameWhoseTableIsAPlumbingTable_IsAlreadyExists()
    {
        // ToTableName("IversonTokenRevocation") is the revocation table's own name.
        await new TokenRevocationRepository(fixture.Repository).EnsureTableAsync();

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("IversonTokenRevocation", "tenant-a")], [], CancellationToken.None),
            StatusCode.AlreadyExists, $"has no {SchemaDescriptor.TenantColumnName} column");

        (await Registry.GetOwnersAsync("IversonTokenRevocation")).Should().BeEmpty();
    }

    [Fact]
    public async Task ANameWhoseRelationIsAView_IsAlreadyExists()
    {
        await fixture.Repository.ExecuteAsync("""CREATE VIEW view_probes AS SELECT gen_random_uuid() AS "Id" """);

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("ViewProbe", "tenant-a")], [], CancellationToken.None),
            StatusCode.AlreadyExists, "is not an ordinary table");
    }

    [Fact]
    public async Task AnOrphanedTableHoldingAnotherTenantsRows_IsAlreadyExists()
    {
        await Applier().ExecuteAsync([Descriptor("OrphanProbe", "tenant-a")], [], CancellationToken.None);
        await fixture.Repository.ExecuteAsync(
            """INSERT INTO orphan_probes ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'theirs', 'tenant-b')""");
        await Registry.DeleteAsync("OrphanProbe");

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("OrphanProbe", "tenant-a")], [], CancellationToken.None),
            StatusCode.AlreadyExists, "holds rows of another tenant");

        (await Registry.GetOwnersAsync("OrphanProbe")).Should().BeEmpty();
    }

    [Fact]
    public async Task ACaseVariantOfTheOwnersOwnType_StillRunsTheAdoptionCheck()
    {
        // ToTableName keeps the spelling's case boundaries: "Caseorphanprobe" derives
        // caseorphanprobes, "CaseOrphanProbe" case_orphan_probes. tenant-b's registration of the
        // second is gone but its table and rows are not, and tenant-a's stored row for the first
        // must not let it take them over.
        await Applier().ExecuteAsync([Descriptor("CaseOrphanProbe", "tenant-b")], [], CancellationToken.None);
        await fixture.Repository.ExecuteAsync(
            """INSERT INTO case_orphan_probes ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'theirs', 'tenant-b')""");
        await Registry.DeleteAsync("CaseOrphanProbe");
        await Applier().ExecuteAsync([Descriptor("Caseorphanprobe", "tenant-a")], [], CancellationToken.None);

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("CaseOrphanProbe", "tenant-a")], [], CancellationToken.None),
            StatusCode.AlreadyExists, "holds rows of another tenant");

        (await Registry.GetOwnersAsync("CaseOrphanProbe")).Should().Equal("tenant-a");
    }

    [Fact]
    public async Task AFirstLetterVariantOfTheOwnersOwnType_SkipsTheAdoptionCheck()
    {
        // "Firstletterprobe" and "firstletterprobe" derive the same table, firstletterprobes, which
        // the stored row adopted. Another tenant's earlier row in it must not block its owner.
        await Applier().ExecuteAsync([Descriptor("Firstletterprobe", "tenant-a")], [], CancellationToken.None);
        await fixture.Repository.ExecuteAsync(
            """INSERT INTO firstletterprobes ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'theirs', 'tenant-b')""");

        await Applier().ExecuteAsync([Descriptor("firstletterprobe", "tenant-a")], [], CancellationToken.None);

        (await Registry.GetOwnersAsync("Firstletterprobe")).Should().OnlyContain(owner => owner == "tenant-a");
    }

    [Theory]
    [InlineData("a-tenant")] // sorts before the owner
    [InlineData("z-tenant")] // sorts after it
    [InlineData(null)]       // a table whose tenant column allows NULL
    public async Task TheAdoptionCheck_CountsEveryTenantValueButTheOwnersAsAnotherTenants(string? foreignTenant)
    {
        var typeName = $"ForeignRowProbe{(foreignTenant ?? "Null").Replace("-", "")}";
        var table = SchemaBuilder.ToTableName(typeName);
        await Applier().ExecuteAsync([Descriptor(typeName, "tenant-a")], [], CancellationToken.None);
        await fixture.Repository.ExecuteAsync($"""ALTER TABLE {table} ALTER COLUMN "__TenantId" DROP NOT NULL""");
        await fixture.Repository.ExecuteAsync(
            $"""INSERT INTO {table} ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'mine', 'tenant-a')""");
        var inspector = new ExistingTableInspector(fixture.Repository);

        (await inspector.HasRowsOfAnotherTenantAsync(table, SchemaDescriptor.TenantColumnName, "tenant-a")).Should().BeFalse();

        await fixture.Repository.ExecuteAsync(
            $"""INSERT INTO {table} ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'theirs', @Tenant)""",
            new { Tenant = foreignTenant });

        (await inspector.HasRowsOfAnotherTenantAsync(table, SchemaDescriptor.TenantColumnName, "tenant-a")).Should().BeTrue();
    }

    [Fact]
    public async Task TheModelChangeRecovery_AdoptsATableHoldingOnlyTheOwnersRows()
    {
        // The documented recovery: delete the registry row, then register again.
        await Applier().ExecuteAsync([Descriptor("RecoveryProbe", "tenant-a")], [], CancellationToken.None);
        await fixture.Repository.ExecuteAsync(
            """INSERT INTO recovery_probes ("Id", "Title", "__TenantId") VALUES (gen_random_uuid(), 'mine', 'tenant-a')""");
        await Registry.DeleteAsync("RecoveryProbe");

        await Applier().ExecuteAsync([Descriptor("RecoveryProbe", "tenant-a")], [], CancellationToken.None);

        (await Registry.GetOwnersAsync("RecoveryProbe")).Should().Equal("tenant-a");
    }

    [Fact]
    public async Task AColumnTypeChange_IsFailedPrecondition()
    {
        await Applier().ExecuteAsync([Descriptor("DriftProbe", "tenant-a")], [], CancellationToken.None);

        await AssertRefusedAsync(
            () => Applier().ExecuteAsync([Descriptor("DriftProbe", "tenant-a", titleSqlType: "INTEGER")], [], CancellationToken.None),
            StatusCode.FailedPrecondition, "Migrate the column by hand");
    }

    // ── The advisory locks ───────────────────────────────────────────────────

    // Each test below releases what it holds in a finally, so a failed assertion cannot leave a
    // session lock held for the next test. A waiter gets its own token, cancelled on the way out.

    private static async Task ReleaseAsync(
        IAsyncDisposable? lease, CancellationTokenSource waiterCts, Task<IAsyncDisposable>? waiter)
    {
        try
        {
            if (lease is not null)
                await lease.DisposeAsync();
        }
        finally
        {
            await waiterCts.CancelAsync();
            if (waiter is not null)
            {
                // A waiter that was cancelled or failed holds nothing; one that won is released.
                // Its own exception would only hide the failure that brought the test here.
                try { await (await waiter).DisposeAsync(); }
                catch (Exception) { }
            }
        }
    }

    [Fact]
    public async Task TheRegistrationLock_IsHeldByOneHolderAtATime()
    {
        await using var locks = new PostgresPrivilegedRequestLocks(fixture.ConnectionString);
        using var waiterCts = new CancellationTokenSource();
        IAsyncDisposable? first = await locks.AcquireRegistrationAsync(CancellationToken.None);
        Task<IAsyncDisposable>? second = null;
        try
        {
            second = locks.AcquireRegistrationAsync(waiterCts.Token);
            await Task.Delay(500);
            second.IsCompleted.Should().BeFalse("the first holder still has the lock");

            var releasing = first;
            first = null;
            await releasing.DisposeAsync();
            await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(10));
            second = null;
        }
        finally
        {
            await ReleaseAsync(first, waiterCts, second);
        }
    }

    [Fact]
    public async Task ALockWaiter_OutlastsTheConnectionsCommandTimeout()
    {
        // A one-second command timeout stands in for Npgsql's 30 s default.
        await using var locks = new PostgresPrivilegedRequestLocks(fixture.ConnectionString + ";Command Timeout=1");
        using var waiterCts = new CancellationTokenSource();
        IAsyncDisposable? first = await locks.AcquireRegistrationAsync(CancellationToken.None);
        Task<IAsyncDisposable>? second = null;
        try
        {
            second = locks.AcquireRegistrationAsync(waiterCts.Token);
            await Task.Delay(3000);
            second.IsCompleted.Should().BeFalse("the waiter must still be waiting, not timed out");

            var releasing = first;
            first = null;
            await releasing.DisposeAsync();
            await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(10));
            second = null;
        }
        finally
        {
            await ReleaseAsync(first, waiterCts, second);
        }
    }

    [Fact]
    public async Task ATypeLock_SerialisesOneTypeIgnoringCase_AndBlocksNeitherAnotherTypeNorRegistration()
    {
        await using var locks = new PostgresPrivilegedRequestLocks(fixture.ConnectionString);
        using var waiterCts = new CancellationTokenSource();
        IAsyncDisposable? article = await locks.AcquireTypeAsync("LockArticle", CancellationToken.None);
        Task<IAsyncDisposable>? sameTypeOtherCase = null;
        try
        {
            await using (var other = await locks.AcquireTypeAsync("LockOther", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))) { }
            await using (var registration = await locks.AcquireRegistrationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))) { }

            sameTypeOtherCase = locks.AcquireTypeAsync("lockarticle", waiterCts.Token);
            await Task.Delay(500);
            sameTypeOtherCase.IsCompleted.Should().BeFalse("a type lock folds the name's case");

            var releasing = article;
            article = null;
            await releasing.DisposeAsync();
            await using var sameTypeLease = await sameTypeOtherCase.WaitAsync(TimeSpan.FromSeconds(10));
            sameTypeOtherCase = null;
        }
        finally
        {
            await ReleaseAsync(article, waiterCts, sameTypeOtherCase);
        }
    }

    // The lock pool's backends, told apart by the Application Name its connection string sets.
    private Task<long> BackendsNamedAsync(string applicationName) =>
        fixture.Repository.QuerySingleOrDefaultAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @Name", new { Name = applicationName });

    private static string LockPoolName(string purpose) => $"{purpose}-{Guid.NewGuid():N}"[..40];

    [Fact]
    public async Task ALockWaiter_HoldsNoConnectionWhileItWaits()
    {
        // Unpooled, so a connection the waiter is not using is closed rather than parked idle in
        // the pool, and the backends counted below are exactly the connections in use.
        var applicationName = LockPoolName("lock-waiter");
        await using var locks = new PostgresPrivilegedRequestLocks(
            new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false, ApplicationName = applicationName }
                .ConnectionString);
        using var waiterCts = new CancellationTokenSource();
        IAsyncDisposable? first = await locks.AcquireRegistrationAsync(CancellationToken.None);
        Task<IAsyncDisposable>? second = null;
        try
        {
            second = locks.AcquireRegistrationAsync(waiterCts.Token);
            await Task.Delay(1000);

            // A waiter blocked inside pg_advisory_lock shows as an ungranted advisory lock.
            second.IsCompleted.Should().BeFalse("the first holder still has the lock");
            (await fixture.Repository.QuerySingleOrDefaultAsync<long>(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted")).Should().Be(0);

            // Between tries the waiter has no connection, so only the holder's backend is left. A
            // sample can land on a try in flight, so the fewest seen is what counts.
            var backends = new List<long>();
            for (var i = 0; i < 10; i++)
            {
                backends.Add(await BackendsNamedAsync(applicationName));
                await Task.Delay(100);
            }
            backends.Min().Should().Be(1, "between tries the waiter must hold no connection");

            var releasing = first;
            first = null;
            await releasing.DisposeAsync();
            await using var secondLease = await second.WaitAsync(TimeSpan.FromSeconds(10));
            second = null;
        }
        finally
        {
            await ReleaseAsync(first, waiterCts, second);
        }
    }

    [Fact]
    public async Task AnAcquireWhoseOpenFails_LeavesTheLockPoolAlone()
    {
        // A pool of one connection, which the holder has: the next open waits a second for it
        // and fails. Clearing the pool then would close the holder's connection on its return.
        var applicationName = LockPoolName("lock-open-fails");
        await using var locks = new PostgresPrivilegedRequestLocks(
            new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { MaxPoolSize = 1, Timeout = 1, ApplicationName = applicationName }
                .ConnectionString);
        IAsyncDisposable? held = await locks.AcquireRegistrationAsync(CancellationToken.None);
        try
        {
            await locks.Invoking(l => l.AcquireTypeAsync("OpenFailsProbe", CancellationToken.None))
                .Should().ThrowAsync<NpgsqlException>();

            var releasing = held;
            held = null;
            await releasing.DisposeAsync();
            await Task.Delay(500);

            (await BackendsNamedAsync(applicationName)).Should().Be(1,
                "a failed open holds no lock, so the holder's connection goes back to the pool");
        }
        finally
        {
            if (held is not null)
                await held.DisposeAsync();
        }
    }

    [Fact]
    public async Task ALeaseWhoseUnlockFails_DoesNotLeaveTheLockHeld()
    {
        // pg_advisory_unlock refused to a non-superuser, in a database of the test's own (function
        // privileges are per database), so the unlock fails on a connection that is still healthy.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"unlock_probe_{suffix}";
        var login = $"unlock_probe_{suffix}";
        await fixture.Repository.ExecuteAsync($"CREATE DATABASE {database}");
        await fixture.Repository.ExecuteAsync($"CREATE ROLE {login} LOGIN PASSWORD 'unlock-probe'");
        var ownerInProbe = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        var loginInProbe = new NpgsqlConnectionStringBuilder(ownerInProbe) { Username = login, Password = "unlock-probe" }.ConnectionString;

        try
        {
            await using (var owner = new NpgsqlConnection(ownerInProbe))
            {
                await owner.OpenAsync();
                await owner.ExecuteAsync("REVOKE EXECUTE ON FUNCTION pg_advisory_unlock(bigint) FROM PUBLIC");
            }

            await using (var locks = new PostgresPrivilegedRequestLocks(loginInProbe))
            {
                var lease = await locks.AcquireRegistrationAsync(CancellationToken.None);
                (await lease.Invoking(l => l.DisposeAsync().AsTask()).Should().ThrowAsync<PostgresException>())
                    .Which.SqlState.Should().Be("42501");

                await using var probe = new NpgsqlConnection(ownerInProbe);
                await probe.OpenAsync();
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                var taken = false;
                while (!taken && DateTimeOffset.UtcNow < deadline)
                {
                    taken = await probe.ExecuteScalarAsync<bool>(
                        "SELECT pg_try_advisory_lock(@Key)", new { Key = 1L << 41 }); // the registration key
                    if (!taken)
                        await Task.Delay(100);
                }

                taken.Should().BeTrue("the failed lease's connection must not go back to the pool still holding the lock");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await fixture.Repository.ExecuteAsync($"DROP DATABASE {database} WITH (FORCE)");
            await fixture.Repository.ExecuteAsync($"DROP ROLE {login}");
        }
    }

    [Fact]
    public async Task TheLockKeys_LieOutsideTheRangeOfTheHashtextTableLocks()
    {
        await using var locks = new PostgresPrivilegedRequestLocks(fixture.ConnectionString);
        await using var registration = await locks.AcquireRegistrationAsync(CancellationToken.None);
        await using var type = await locks.AcquireTypeAsync("LockKeyProbe", CancellationToken.None);

        // pg_locks shows a bigint advisory key's high half in classid (objsubid 1). The
        // hashtext(...)::bigint keys that ApplySchemaAsync and StartupDdl take are sign-extended
        // int4 values, so their high half is 0 or 2^32 - 1.
        var highHalves = (await fixture.Repository.QueryAsync<long>(
            "SELECT classid::bigint FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1")).ToList();

        highHalves.Should().HaveCount(2);
        highHalves.Should().OnlyContain(h => h != 0L && h != 4294967295L);
    }

    // ── End to end ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ARegistration_ThroughTheQueue_IsAppliedByTheWorker_AndReloadedByTheApi()
    {
        await fixture.Repository.ExecuteAsync($"""DELETE FROM "{PrivilegedRequestSchema.TableName}" """);

        var apiRegistry = NewRegistry();
        var resolver = Substitute.For<IEmbeddingServiceResolver>();
        resolver.Get(Arg.Any<string?>()).Returns(Substitute.For<IEmbeddingService>());
        var orchestrator = new SchemaRegistrationOrchestrator(
            resolver,
            apiRegistry,
            new QueuedSchemaRegistrationExecutor(new PrivilegedRequestClient(Requests, new PrivilegedRequestWaits()), apiRegistry),
            NullLogger<SchemaRegistrationOrchestrator>.Instance);
        var worker = new PrivilegedRequestWorker(Requests, NewExecutor(), NullLogger<PrivilegedRequestWorker>.Instance);

        var type = new TypeDescriptor { TypeName = "QueuedProbe" };
        type.Properties.Add(new PropertyDescriptor { Name = "Id", ObjectType = ObjectType.Guid, IsKey = true });
        type.Properties.Add(new PropertyDescriptor { Name = "Title", ObjectType = ObjectType.String });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var registered = await orchestrator.RegisterAsync(new SchemaRequest { RootType = type }, "tenant-e2e", CancellationToken.None);

            registered.Should().Equal("QueuedProbe");
            apiRegistry.Get("QueuedProbe")!.OwnerTenantId.Should().Be("tenant-e2e");
            (await Registry.GetOwnersAsync("QueuedProbe")).Should().Equal("tenant-e2e");
            (await fixture.Repository.QuerySingleOrDefaultAsync<string>(
                $"""SELECT "Status" FROM "{PrivilegedRequestSchema.TableName}" """)).Should().Be(PrivilegedRequestStatus.Succeeded);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
