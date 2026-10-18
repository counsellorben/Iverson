// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-16-csr-round12-server-implementation-plan.md, T7 S3
// Source: Iverson.Server/Iverson.Sql/PostgresPrivilegedRequestLocks.cs `RegistrationKey`, `TypeKeyBase`, and the key expressions of `AcquireRegistrationAsync` and `AcquireTypeAsync`
// Code ref: f7a0842fc0e707359e777ddf44f7c2e23050c968
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-16-csr-round12-server-implementation-plan/PostgresPrivilegedRequestLocks.Keys.dfy

// ---- Shared vocabulary ----

type CsString = s: seq<char> | |s| < 0x8000_0000

newtype Int32 = x: int | -0x8000_0000 <= x < 0x8000_0000

// ---- Approved axioms ----

// Postgres hashtext(text) -> int4.
function HashText(s: CsString): Int32

// Postgres lower(text) -> text.
function PgLower(s: CsString): CsString

// ---- Transliteration ----

// C#: internal const long RegistrationKey = 1L << 41;
const RegistrationKey: int := 0x200_0000_0000

// C#: internal const long TypeKeyBase = 1L << 40;
const TypeKeyBase: int := 0x100_0000_0000

// C#: AcquireRegistrationAsync: AcquireAsync("@Key", new { Key = RegistrationKey }, ct)
//     -> SELECT pg_try_advisory_lock(@Key) per poll, and pg_advisory_unlock(@Key) on release;
//        @Key bound as bigint (the same key text and parameters for both).
function RegistrationLockKey(): int { RegistrationKey }

// C#: AcquireTypeAsync: AcquireAsync("@Base + hashtext(lower(@TypeName))",
//     new { Base = TypeKeyBase, TypeName = typeName }, ct)
//     -> SELECT pg_try_advisory_lock(@Base + hashtext(lower(@TypeName))) per poll, and
//        pg_advisory_unlock of the same expression on release: bigint + int4, exact
//        (Postgres raises on bigint overflow; P1's third clause rules it out).
function TypeKey(typeName: CsString): int
{
  TypeKeyBase + HashText(PgLower(typeName)) as int
}

// ---- Properties ----

// Citation: "**Locks:** session advisory locks on dedicated pooled connections, released in `finally`, with keys chosen outside the `int4` `hashtext` range used elsewhere." (spec §5.1)
lemma P1_EveryLockKeyLiesOutsideTheInt4Range(t: CsString)
  ensures !(-0x8000_0000 <= RegistrationKey < 0x8000_0000)
  ensures !(-0x8000_0000 <= TypeKey(t) < 0x8000_0000)
  ensures -0x8000_0000_0000_0000 <= TypeKey(t) < 0x8000_0000_0000_0000
{
}

// Citation: "`ReconcileType` takes a per-type lock, so a long reconcile never blocks registration." (spec §5.1)
lemma P2_NoTypeLockIsTheRegistrationLock(t: CsString)
  ensures TypeKey(t) != RegistrationKey
{
}

// Citation: "sameTypeOtherCase.IsCompleted.Should().BeFalse("a type lock folds the name's case");" (plan T7 S1f, `ATypeLock_SerialisesOneTypeIgnoringCase_AndBlocksNeitherAnotherTypeNorRegistration`)
lemma P3_ATypeLockFoldsTheNamesCase(a: CsString, b: CsString)
  requires PgLower(a) == PgLower(b)
  ensures TypeKey(a) == TypeKey(b)
{
}
