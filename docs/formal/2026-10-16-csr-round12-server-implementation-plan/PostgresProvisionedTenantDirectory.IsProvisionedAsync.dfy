// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T10 S3
// Source: Iverson.Server/Iverson.Api/Tenancy/PostgresProvisionedTenantDirectory.cs `IsProvisionedAsync`, `RecordProvisionedAsync`
// Replay base: c79d7de067519edb3b325fbc0aa53332944c6d1c
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PostgresProvisionedTenantDirectory.IsProvisionedAsync.dfy

// ---- Shared vocabulary ----
type CsString = s: seq<char> | |s| < 0x8000_0000

datatype Result<T> = Ok(value: T) | Threw

// ---- Transliteration ----
// _provisioned: ConcurrentDictionary<string, byte>(StringComparer.Ordinal), modelled as the set of its keys
// (ordinal comparison is seq<char> equality; the byte value is always 0 and never read).

// IsProvisionedAsync, full outcome: `query` is the result of
// sql.QuerySingleOrDefaultAsync<bool>("SELECT EXISTS (...)"), which may throw.
// Returns (outcome, new cache, whether Postgres was asked).
function IsProvisionedOutcome(cache: set<CsString>, t: CsString, query: Result<bool>): (Result<bool>, set<CsString>, bool)
{
  if t in cache then (Ok(true), cache, false)                       // if (_provisioned.ContainsKey(tenantId)) return true;
  else
    match query                                                     // var found = await sql.QuerySingleOrDefaultAsync<bool>(...)
    case Threw => (Threw, cache, true)                              //   exception propagates; cache untouched
    case Ok(found) =>
      var cache' := if found then cache + {t} else cache;           // if (found) _provisioned.TryAdd(tenantId, 0);
      (Ok(found), cache', true)                                     // return found;
}

// The non-throwing path, named as the frozen headers require: `found` is the EXISTS answer.
function IsProvisioned(cache: set<CsString>, t: CsString, found: bool): (bool, set<CsString>, bool)
{
  var r := IsProvisionedOutcome(cache, t, Ok(found));
  (r.0.value, r.1, r.2)
}

// RecordProvisionedAsync, full outcome: `exec` is the result of sql.ExecuteAsync(INSERT ... ON CONFLICT DO NOTHING).
function RecordOutcome(cache: set<CsString>, t: CsString, exec: Result<()>): (Result<()>, set<CsString>)
{
  match exec                                                        // await sql.ExecuteAsync(...)
  case Threw => (Threw, cache)                                      //   exception propagates; cache untouched
  case Ok(_) => (Ok(()), cache + {t})                               // _provisioned.TryAdd(tenantId, 0);
}

// The non-throwing path: the cache after the insert.
function Record(cache: set<CsString>, t: CsString): set<CsString>
{
  RecordOutcome(cache, t, Ok(())).1
}

// ---- Properties ----

// Citation: "`Iverson.StarRocks` takes an injected `IProvisionedTenantDirectory`, implemented over Postgres in `Iverson.Api` and cached per process once a tenant is found." (spec §7.3) and "await _sql.Received(2).QuerySingleOrDefaultAsync<bool>(" (plan T10 S1m, `AnUnrecordedTenant_IsAskedAgain_BecauseTheWorkerMayRecordItAtAnyMoment`)
lemma P1_AFoundTenantIsCachedAndAnAbsentOneIsAskedAgain(cache: set<CsString>, t: CsString, found: bool)
  ensures IsProvisioned(cache, t, found).0 == (t in cache || found)
  ensures IsProvisioned(cache, t, found).2 == (t !in cache)
  ensures IsProvisioned(cache, t, found).1 == (if t !in cache && found then cache + {t} else cache)
{
}

// Citation: "(await directory.IsProvisionedAsync("acme")).Should().BeTrue();" (plan T10 S1m, `Recording_InsertsIdempotently_AndCachesTheTenant`)
lemma P2_RecordingCachesTheTenant(cache: set<CsString>, t: CsString)
  ensures Record(cache, t) == cache + {t}
{
}
