// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-19-csr-round12-platform-design.md @ 19d74cfb95c4ccb8ceca9e939494828f3359c3a5
// Source: §2.6 "Bootstrap password", lines 188-192
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-19-csr-round12-platform-design/InitialSetupClosure.dfy
// Round: 1

// Authentik flow policy engine mode (§2.6 line 190 "default `any` engine mode";
// line 191 "sets `policy_engine_mode: all`").
datatype EngineMode = Any | All

// Line 190: "The flow's default `any` engine mode".
function DefaultEngineMode(): EngineMode { Any }

// Line 191: "a policy that always returns false".
function ClosurePolicyResult(): bool { false }

// Flow-level bindings of the `initial-setup` flow: the stock binding first
// (line 190 "the context its stock binding requires"), then the closure's
// always-false binding (line 191 "binds a policy ... at flow level").
function ClosureBindings(stock: bool): seq<bool> { [stock, ClosurePolicyResult()] }

// Line 191: the blueprint entry "sets `policy_engine_mode: all`".
function ClosureEngineMode(): EngineMode { All }

// VA62: mode `all` + a `return False` flow-level binding → denied; "the binding alone, in `any` mode, left the prompt reachable"
predicate FlowAdmitted(mode: EngineMode, bindings: seq<bool>)
  ensures FlowAdmitted(mode, bindings) <==> (if mode == Any then exists i :: 0 <= i < |bindings| && bindings[i] else forall i :: 0 <= i < |bindings| ==> bindings[i])

// Model-level link: the closure as the rule computes it is exactly the
// property's configuration.
lemma ClosureIsAllWithFalse(stock: bool)
  ensures ClosureEngineMode() == All
  ensures ClosureBindings(stock) == [stock, false]
{
}

// Citation: "sets `policy_engine_mode: all` and binds a policy that always returns false at flow level, so the flow is denied whatever the stock binding returns" (§2.6 line 191)
lemma P1_ClosureDenies(stock: bool)
  ensures !FlowAdmitted(All, [stock, false])
{
  var b := [stock, false];
  assert !b[1];
  assert !(forall i :: 0 <= i < |b| ==> b[i]);
}

// Citation: "The flow's default `any` engine mode lets that binding admit it (Verified assumption 62)." (§2.6 line 190)
lemma P2_AnyModeStockAdmits()
  ensures FlowAdmitted(Any, [true, false])
{
  var b := [true, false];
  assert 0 <= 0 < |b| && b[0];
}
