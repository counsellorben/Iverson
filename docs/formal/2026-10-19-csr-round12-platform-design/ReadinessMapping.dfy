// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ 19d74cfb95c4ccb8ceca9e939494828f3359c3a5
// Source: §3.5, lines 315-316
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/ReadinessMapping.dfy
// Round: 1

// The readiness statuses the mapping keeps (§3.5 line 316).
datatype Status = Healthy | AuthPending | Unhealthy

// What one readiness probe observes. The login is refused, the connection
// otherwise fails, the probe query returns rows, the query fails with a
// server error code, or the 5 s query timeout fires (§3.5 line 315).
datatype ProbeOutcome =
  | LoginRefused
  | ConnectFailed
  | Rows(count: nat)
  | QueryError(code: int)
  | QueryTimedOut

// The mapping, in the spec's order of cases:
//   line 315: "Two rows mean Healthy."
//   line 316: "`AuthPending` now arises only from a refused login."
//   line 316: every other outcome keeps the existing mapping, Unhealthy.
function Classify(o: ProbeOutcome): Status
{
  if o.Rows? && o.count == 2 then Healthy
  else if o.LoginRefused? then AuthPending
  else Unhealthy
}

// VA45: "as a login with no grants: SELECT * FROM TABLE(generate_series(1,2)) returned two rows with a backend up (0.5 s) and ERROR 1064 Backend node not found with none (0.4 s)"
function QueryOutcome(backendAlive: bool): ProbeOutcome
  ensures QueryOutcome(backendAlive) == (if backendAlive then Rows(2) else QueryError(1064))

// Citation: "With no grant needed, `AuthPending` now arises only from a refused login." (§3.5 line 316)
lemma P1_AuthPendingOnlyFromRefusedLogin(o: ProbeOutcome)
  ensures Classify(o) == AuthPending <==> o.LoginRefused?
{
}

// Citation: "Two rows mean Healthy. The query needs no grant, returns rows with a backend alive, and fails in about 0.4 s ("Backend node not found") with none (Verified assumption 45)." (§3.5 line 315)
lemma P2_RowsMeanHealthy(backendAlive: bool)
  ensures Classify(QueryOutcome(backendAlive)) == (if backendAlive then Healthy else Unhealthy)
{
}
