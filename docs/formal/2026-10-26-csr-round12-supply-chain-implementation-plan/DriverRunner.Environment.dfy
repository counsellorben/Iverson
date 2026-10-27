// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T14 S1
// Source: Iverson.Server/Iverson.ClientConformance/DriverRunner.cs IsAllowed, BuildStepEnvironment, DriverRunEnvironment, and the existing BuildEnvironment they call
// Code ref: d0eab48b734ab0c7023d50d11a58dd51167028fa
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/DriverRunner.Environment.dfy

// ---------------------------------------------------------------------------
// Library boundary
// ---------------------------------------------------------------------------

datatype Option<T> = None | Some(value: T)

// DriverRunner.cs:12-28 — only the fields BuildEnvironment reads. ClientSecret is `string?`.
datatype DriverContext = DriverContext(
  ClientSecret: Option<string>,
  ServiceToken: string,
  ActingToken: string,
  WrongActingToken: string)

// string.StartsWith(prefix, StringComparison.Ordinal): an ordinal prefix test.
predicate StartsWith(s: string, prefix: string)
{
  |prefix| <= |s| && s[..|prefix|] == prefix
}

// ---------------------------------------------------------------------------
// Model constants (DriverRunner.cs:372-381; HarnessSecrets from VA 40)
// ---------------------------------------------------------------------------

const Exact: seq<string> :=
  ["PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "USER", "SHELL", "M2_HOME", "VIRTUAL_ENV", "UseSharedCompilation"]

const Prefixes: seq<string> :=
  ["LC_", "DOTNET_", "NUGET_", "JAVA_HOME", "MAVEN_", "GO", "NODE_", "NPM_CONFIG_", "npm_config_", "PYTHON", "PIP_", "MSBUILD"]

const DeniedPrefixes: seq<string> := ["GOOGLE_"]

const CaTrust: seq<string> :=
  ["SSL_CERT_FILE", "SSL_CERT_DIR", "GRPC_DEFAULT_SSL_ROOTS_FILE_PATH", "JAVA_TOOL_OPTIONS"]

const HarnessSecrets: seq<string> :=
  ["IVERSON_CLIENT_SECRET", "IVERSON_CLIENT_ID", "IVERSON_TOKEN_ENDPOINT", "IVERSON_CLIENT_SCOPE", "IVERSON_ACTING_USER_HOST_HEADER"]

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// DriverRunner.cs:350-366 BuildEnvironment (pre-existing).
function BuildEnvironment(context: DriverContext): map<string, string>
{
  map[
    "IVERSON_DRIVER_CLIENT_SECRET" := (if context.ClientSecret.Some? then context.ClientSecret.value else ""),
    "IVERSON_DRIVER_SERVICE_TOKEN" := context.ServiceToken,
    "IVERSON_DRIVER_ACTING_TOKEN" := context.ActingToken,
    "IVERSON_DRIVER_WRONG_ACTING_TOKEN" := context.WrongActingToken
  ]
}

// AllowedPrefixes.Any(prefix => name.StartsWith(prefix, Ordinal)), left to right.
function AnyStartsWith(prefixes: seq<string>, name: string): bool
  decreases |prefixes|
{
  if |prefixes| == 0 then false
  else StartsWith(name, prefixes[0]) || AnyStartsWith(prefixes[1..], name)
}

// DriverRunner.cs:383-385 IsAllowed.
function IsAllowed(name: string): bool
{
  !AnyStartsWith(DeniedPrefixes, name)
  && (name in Exact || AnyStartsWith(Prefixes, name))
}

// DriverRunner.cs:388-397 BuildStepEnvironment. The foreach writes each allowlisted key of
// `parent` once (dictionary keys are distinct), so the loop's result is this comprehension.
function BuildStepEnvironment(parent: map<string, string>): map<string, string>
{
  map name | name in parent && IsAllowed(name) :: parent[name]
}

// DriverRunner.cs:405-409: foreach (var name in CaTrustNames) if (parent[name] is string value) environment[name] = value;
function AddCaTrust(environment: map<string, string>, parent: map<string, string>, names: seq<string>): map<string, string>
  decreases |names|
{
  if |names| == 0 then environment
  else
    var name := names[0];
    var lookup: Option<string> := if name in parent then Some(parent[name]) else None; // Hashtable indexer: null when absent
    var environment' := if lookup.Some? then environment[name := lookup.value] else environment;
    AddCaTrust(environment', parent, names[1..])
}

// DriverRunner.cs:410-411: foreach (var (name, value) in BuildEnvironment(context)) environment[name] = value;
// Keys are distinct, so the loop is a right-biased map union.
function AddAll(environment: map<string, string>, extra: map<string, string>): map<string, string>
{
  environment + extra
}

// DriverRunner.cs:402-413 DriverRunEnvironment.
function DriverRunEnvironment(parent: map<string, string>, context: DriverContext): map<string, string>
{
  var environment := BuildStepEnvironment(parent);
  var environment1 := AddCaTrust(environment, parent, CaTrust);
  AddAll(environment1, BuildEnvironment(context))
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma AddCaTrustKeys(environment: map<string, string>, parent: map<string, string>, names: seq<string>, k: string)
  ensures k in AddCaTrust(environment, parent, names) <==> (k in environment || (k in names && k in parent))
  ensures k !in names && k in environment ==> AddCaTrust(environment, parent, names)[k] == environment[k]
  ensures k in names && k in parent ==> AddCaTrust(environment, parent, names)[k] == parent[k]
  decreases |names|
{
  if |names| > 0 {
    var name := names[0];
    var environment' := if name in parent then environment[name := parent[name]] else environment;
    AddCaTrustKeys(environment', parent, names[1..], k);
    assert names == [name] + names[1..];
  }
}

lemma SecretNotAllowed(s: string)
  requires s in HarnessSecrets
  ensures !IsAllowed(s)
{
  // Every harness secret begins "IVERSON_"; no allowlisted name or prefix does.
  assert s[0] == 'I';
  forall i | 0 <= i < |Prefixes| ensures !StartsWith(s, Prefixes[i]) {
    assert |Prefixes[i]| > 0 && Prefixes[i][0] != 'I';
  }
  NoPrefixMatch(Prefixes, s);
}

lemma ExactAllowed(v: string)
  requires v in Exact
  ensures IsAllowed(v)
{
  assert v == "PATH" || v == "HOME" || v == "TMPDIR" || v == "TEMP" || v == "TMP" || v == "LANG" || v == "USER" || v == "SHELL" || v == "M2_HOME" || v == "VIRTUAL_ENV" || v == "UseSharedCompilation";
  assert |v| > 0 && v[0] != 'G';
  assert !StartsWith(v, "GOOGLE_");
  assert AnyStartsWith(DeniedPrefixes, v) == StartsWith(v, "GOOGLE_");
}

lemma NoPrefixMatch(prefixes: seq<string>, name: string)
  requires forall i | 0 <= i < |prefixes| :: !StartsWith(name, prefixes[i])
  ensures !AnyStartsWith(prefixes, name)
  decreases |prefixes|
{
  if |prefixes| > 0 {
    assert !StartsWith(name, prefixes[0]);
    forall i | 0 <= i < |prefixes[1..]| ensures !StartsWith(name, prefixes[1..][i]) {
      assert prefixes[1..][i] == prefixes[i + 1];
    }
    NoPrefixMatch(prefixes[1..], name);
  }
}

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

// Citation: design HarnessEnv.P1_BuildStepsGetNoHarnessSecret: "Build steps (`:175`) get the allowlist only." (spec line 184).
lemma P1_BuildStepsGetNoHarnessSecret(parent: map<string, string>, s: string)
  requires s in HarnessSecrets
  ensures s !in BuildStepEnvironment(parent)
{
  SecretNotAllowed(s);
}

// Citation: design HarnessEnv.P2_DriverRunGetsNoHarnessSecret: "starts every process from an empty environment plus an allowlist" (spec line 184); test `DriverRunEnvironment_AddsTheCaTrustVariablesAndTheContext_AndNoHarnessSecret`.
lemma P2_DriverRunGetsNoHarnessSecret(parent: map<string, string>, ctx: DriverContext, s: string)
  requires s in HarnessSecrets
  ensures s !in DriverRunEnvironment(parent, ctx)
{
  SecretNotAllowed(s);
  AddCaTrustKeys(BuildStepEnvironment(parent), parent, CaTrust, s);
  assert s !in CaTrust;
  assert s !in BuildEnvironment(ctx);
}

// Citation: design HarnessEnv.P3_DriverRunGetsItsContext: "the driver run step adds `BuildEnvironment(context)` (VA 40)" (spec line 184).
lemma P3_DriverRunGetsItsContext(parent: map<string, string>, ctx: DriverContext, k: string)
  requires k in BuildEnvironment(ctx)
  ensures k in DriverRunEnvironment(parent, ctx) && DriverRunEnvironment(parent, ctx)[k] == BuildEnvironment(ctx)[k]
{
}

// Citation: design HarnessEnv.P4_DriverRunGetsCaTrust: "and the CA-trust variables `SSL_CERT_FILE`, `SSL_CERT_DIR`, `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` and `JAVA_TOOL_OPTIONS`" (spec line 184).
lemma P4_DriverRunGetsCaTrust(parent: map<string, string>, ctx: DriverContext, v: string)
  requires v in CaTrust && v in parent
  ensures v in DriverRunEnvironment(parent, ctx) && DriverRunEnvironment(parent, ctx)[v] == parent[v]
{
  AddCaTrustKeys(BuildStepEnvironment(parent), parent, CaTrust, v);
  assert v !in BuildEnvironment(ctx);
}

// Citation: design HarnessEnv.P5_BuildStepsKeepExactAllowlist: "an allowlist (`PATH`, `HOME`, `TMPDIR`, `TEMP`, `TMP`, `LANG`, `LC_*`, `USER`, `SHELL`, …)" (spec line 184).
lemma P5_BuildStepsKeepExactAllowlist(parent: map<string, string>, v: string)
  requires v in parent && v in Exact
  ensures v in BuildStepEnvironment(parent) && BuildStepEnvironment(parent)[v] == parent[v]
{
  ExactAllowed(v);
}

// Citation: test `BuildStepEnvironment_IsTheAllowlistOnly`; "starts every process from an empty environment plus an allowlist" (spec line 184).
lemma P6_BuildStepsOnlyAllowlisted(parent: map<string, string>, v: string)
  requires v in BuildStepEnvironment(parent)
  ensures v in parent && IsAllowed(v)
{
}
