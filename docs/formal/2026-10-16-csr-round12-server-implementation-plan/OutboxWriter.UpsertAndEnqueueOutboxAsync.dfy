// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T9 S5
// Source: Iverson.Server/Iverson.Sql/OutboxWriter.cs `UpsertAndEnqueueOutboxAsync` (5e)
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/OutboxWriter.UpsertAndEnqueueOutboxAsync.dfy

// ---------------------------------------------------------------------------
// Shared vocabulary (only the names this candidate uses)
// ---------------------------------------------------------------------------

// A C# string (UTF-16 code units under --unicode-char false); Length is an int.
type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Option<T> = None | Some(value: T)

// ---------------------------------------------------------------------------
// Candidate model
// ---------------------------------------------------------------------------

// The four calls the transaction body makes on its IDbTransactionContext, in issue order.
// The SQL texts and parameter objects are built before the transaction and are not read
// by any property, so UpsertRow and OutboxInsert carry no arguments.
datatype TxStep = EnterTenantScope(tenant: CsString) | UpsertRow | ExitRoleScope | OutboxInsert

// Written(steps): the method ran the transaction body and returned outboxRowId;
// ArgumentNull: ArgumentNullException.ThrowIfNull(tenantId) threw.
datatype WriteOutcome = Written(steps: seq<TxStep>) | ArgumentNull

// Approved axiom: OutboxWriter.WithTenantColumn (OutboxWriter.cs:89-103), as a total function.
function WithTenantColumnJson(payload: CsString, tenantColumn: Option<CsString>, tenant: CsString): CsString

// The lambda passed to txRunner.ExecuteInTransactionAsync (OutboxWriter.cs:57-69).
// tenantId is non-null here: the only path that reaches it passed ThrowIfNull.
function TransactionBody(tenantId: CsString, payloadJson: CsString): seq<TxStep>
{
  [EnterTenantScope(tenantId)]   // await tx.EnterTenantScopeAsync(tenantId);
  + [UpsertRow]                  // await tx.ExecuteAsync(upsertSql, new { Json = payloadJson });
  + [ExitRoleScope]              // await tx.ExitRoleScopeAsync();
  + [OutboxInsert]               // await tx.ExecuteAsync(outboxSql, new { ... });
}

// OutboxWriter.UpsertAndEnqueueOutboxAsync (OutboxWriter.cs:18-72).
function UpsertAndEnqueue(tenantId: Option<CsString>, payload: CsString, tenantColumn: Option<CsString>): WriteOutcome
{
  // ArgumentNullException.ThrowIfNull(tenantId);
  if tenantId.None? then ArgumentNull
  else
    var t := tenantId.value;
    // table / keyColumn / allCols / updateSet / upsertSql / outboxSql / outboxRowId:
    // built before the transaction, not read by any property (see correspondence table).
    // payloadJson = WithTenantColumn(payloadJson, schema.TenantColumn, tenantId);
    var payloadJson := WithTenantColumnJson(payload, tenantColumn, t);
    // await txRunner.ExecuteInTransactionAsync(async tx => { ... });
    var steps := TransactionBody(t, payloadJson);
    // return outboxRowId;
    Written(steps)
}

// ---------------------------------------------------------------------------
// Properties (frozen)
// ---------------------------------------------------------------------------

// Citation: "The api's maintenance-role reads and deletes for types without a tenant column, and the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4) and "await _txRunner.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<IDbTransactionContext, Task>>());" (plan T9 S1, `UpsertAndEnqueueOutboxAsync_NullTenantId_IsRefusedBeforeTheTransaction`)
lemma P1_ANullTenantIsRefusedBeforeTheTransaction(payload: CsString, tenantColumn: Option<CsString>)
  ensures UpsertAndEnqueue(None, payload, tenantColumn) == ArgumentNull
{
}

// Citation: "the outbox writer's owner-role branch for a null tenant, become fail-closed errors:" (spec §6.4)
lemma P2_TheUpsertAlwaysRunsInsideTheTenantScope(t: CsString, payload: CsString, tenantColumn: Option<CsString>)
  ensures UpsertAndEnqueue(Some(t), payload, tenantColumn) ==
    Written([EnterTenantScope(t), UpsertRow, ExitRoleScope, OutboxInsert])
{
}
