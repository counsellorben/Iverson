// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ a11e848d722d05cc808840ae796e6afdb2101aa2
// Source: §2.3 "Shared invalidation" (and §6 line 492), lines 83-88
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/CacheInvalidation.dfy
// Round: 3

// ---------------------------------------------------------------------------
// Model: one api replica's dedicated listener connection, seen as a trace of
// events. Writes come from any instance (worker or api) to the shared Postgres.
// ---------------------------------------------------------------------------

// Line 84: the writers, in the spec's order.
datatype Writer =
  | RemoveUser                 // "RemoveUser (both revokes)"
  | SetTenantAdminDemotion     // "SetTenantAdmin's demotion (both revokes)"
  | DeleteTenant               // "DeleteTenant"
  | Suspend                    // "Suspend"
  | Reactivate                 // "and Reactivate"
  | CreateTenantInsert         // "CreateTenant's insert"
  | CreateTenantRollbackDelete // "and its rollback delete"
  | WorkerLegacySeed           // "the worker's legacy-tenant seed (SeedIfMissingAsync)"

// Line 84: "Every revocation write and every tenant-status write".
datatype Table = Revocation | TenantStatus

// Line 85: the two caches the listener invalidates.
datatype Cache = TokenRevocationCache | TenantStatusCache

datatype Event =
  | Write(writer: Writer, table: Table) // a committed write (line 84)
  | Connect                             // connect or reconnect of the listener (line 86)
  | Disconnect                          // the listener connection is lost (lines 86-87)

type Trace = seq<Event>

// Line 84: "Every revocation write and every tenant-status write issues
// pg_notify in the same transaction as the write." Every writer in the list
// notifies, including the worker's seed ("which notifies in the same
// transaction as its insert").
function NotifiesInSameTransaction(w: Writer, tb: Table): bool
{
  match w
  case RemoveUser => true
  case SetTenantAdminDemotion => true
  case DeleteTenant => true
  case Suspend => true
  case Reactivate => true
  case CreateTenantInsert => true
  case CreateTenantRollbackDelete => true
  case WorkerLegacySeed => true
}

// Line 85: "Each api replica holds one dedicated listener connection".
// A Connect happens only when the listener is not connected; a Disconnect only
// when it is. Writes may happen at any time.
function ListenStateAfter(t: Trace, n: nat): bool
  requires n <= |t|
{
  if n == 0 then false                     // the listener starts disconnected
  else if t[n-1].Connect? then true        // line 86: "issues LISTEN first"
  else if t[n-1].Disconnect? then false    // VA14: "registrations end with the session"
  else ListenStateAfter(t, n-1)
}

predicate ValidTrace(t: Trace)
{
  forall n :: 0 <= n < |t| ==>
    (t[n].Connect? ==> !ListenStateAfter(t, n)) &&
    (t[n].Disconnect? ==> ListenStateAfter(t, n))
}

// The listener's LISTEN registration is in effect when event i happens.
predicate ListeningAt(t: Trace, i: nat)
  requires i < |t|
{
  ListenStateAfter(t, i)
}

// VA14: "LISTEN takes effect at transaction commit"; "a no-grant login LISTENed and received a NOTIFY; a NOTIFY with no listener was not delivered to a later LISTEN"; registrations end with the session
predicate Delivered(t: Trace, i: nat)
  requires i < |t| && t[i].Write?
  ensures Delivered(t, i) <==> ListeningAt(t, i)

// The set of caches the listener invalidates as a result of event i.
function InvalidatesAt(t: Trace, i: nat): set<Cache>
  requires i < |t|
{
  match t[i]
  // Line 85: "On every notification it invalidates both TokenRevocationCache
  // and TenantStatusCache. The payload is only a hint." A write yields a
  // notification only if it notifies (line 84) and the notification is
  // delivered (VA14; line 86 "not replayed").
  case Write(w, tb) =>
    if NotifiesInSameTransaction(w, tb) && Delivered(t, i)
    then {TokenRevocationCache, TenantStatusCache}
    else {}
  // Line 86: "On every connect and reconnect it issues LISTEN first, then
  // invalidates both caches."
  case Connect => {TokenRevocationCache, TenantStatusCache}
  // Line 87: "a lost listener is logged and counted" -- no invalidation.
  case Disconnect => {}
}

// Both caches are invalidated at event i.
predicate InvalidatedAt(t: Trace, i: nat)
  requires i < |t|
{
  InvalidatesAt(t, i) == {TokenRevocationCache, TenantStatusCache}
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: "Every revocation write and every tenant-status write issues `pg_notify` in the same transaction as the write … On every notification it invalidates both `TokenRevocationCache` and `TenantStatusCache`." (§2.3 lines 84–85)
lemma P1_ListeningWriteInvalidates(t: Trace, i: nat)
  requires ValidTrace(t) && i < |t| && t[i].Write? && ListeningAt(t, i)
  ensures InvalidatedAt(t, i)
{
  assert Delivered(t, i);
  assert NotifiesInSameTransaction(t[i].writer, t[i].table);
}

// Citation: "On every connect and reconnect it issues `LISTEN` first, then invalidates both caches. A notification sent while it was disconnected is not replayed." (§2.3 line 86)
lemma P2_NoWriteMissedAcrossReconnect(t: Trace, i: nat, j: nat)
  requires ValidTrace(t) && i < j < |t| && t[i].Write? && t[j].Connect?
  ensures exists k :: i <= k <= j && InvalidatedAt(t, k)
{
  assert InvalidatedAt(t, j);
}
