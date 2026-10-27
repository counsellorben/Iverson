// Plan: /home/ben/repositories/Iverson/docs/plans/2026-10-26-csr-round12-supply-chain-implementation-plan.md, T14 S1
// Source: Iverson.Server/Iverson.ClientConformance/DriverRunner.cs IsAllowed, BuildStepEnvironment, DriverRunEnvironment, and the existing BuildEnvironment they call
// Replay base: a6c008dce9e4c846f8c34456ddd6297636d31979
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction --unicode-char false --allow-deprecation docs/formal/2026-10-26-csr-round12-supply-chain-implementation-plan/DriverRunner.Environment.dfy

// ---------------------------------------------------------------------------
// Library boundary
// ---------------------------------------------------------------------------

// A C# `string?`.
datatype Option<T> = None | Some(value: T)

// DriverContext (DriverRunner.cs:12-28): only the fields BuildEnvironment reads.
datatype DriverContext = DriverContext(
  ClientSecret: Option<string>,
  ActingToken: string,
  ServiceToken: string,
  WrongActingToken: string)

// string.StartsWith(prefix, StringComparison.Ordinal): an ordinal prefix test.
predicate StartsWith(name: string, prefix: string)
{
  |prefix| <= |name| && name[..|prefix|] == prefix
}

// VA 40: the variables the harness itself reads (model const, not in the code).
const HarnessSecrets: seq<string> :=
  ["IVERSON_CLIENT_SECRET", "IVERSON_CLIENT_ID", "IVERSON_TOKEN_ENDPOINT", "IVERSON_CLIENT_SCOPE",
   "IVERSON_ACTING_USER_HOST_HEADER"]

// ---------------------------------------------------------------------------
// Transliteration
// ---------------------------------------------------------------------------

// DriverRunner.cs:350-364 BuildEnvironment
function BuildEnvironment(context: DriverContext): map<string, string>
{
  map[
    "IVERSON_DRIVER_CLIENT_SECRET" := (if context.ClientSecret.Some? then context.ClientSecret.value else ""),
    "IVERSON_DRIVER_SERVICE_TOKEN" := context.ServiceToken,
    "IVERSON_DRIVER_ACTING_TOKEN" := context.ActingToken,
    "IVERSON_DRIVER_WRONG_ACTING_TOKEN" := context.WrongActingToken
  ]
}

// DriverRunner.cs:372-373 AllowedNames
const Exact: seq<string> :=
  ["PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "USER", "SHELL", "M2_HOME", "VIRTUAL_ENV"]

// DriverRunner.cs:375-376 AllowedPrefixes
const AllowedPrefixes: seq<string> :=
  ["LC_", "DOTNET_", "NUGET_", "JAVA_HOME", "MAVEN_", "GO", "NODE_", "NPM_CONFIG_", "npm_config_", "PYTHON", "PIP_"]

// DriverRunner.cs:378-381 CaTrustNames
const CaTrust: seq<string> :=
  ["SSL_CERT_FILE", "SSL_CERT_DIR", "GRPC_DEFAULT_SSL_ROOTS_FILE_PATH", "JAVA_TOOL_OPTIONS"]

// Enumerable.Any(prefixes, prefix => name.StartsWith(prefix, Ordinal)), in sequence order.
function AnyPrefix(prefixes: seq<string>, name: string): bool
{
  if |prefixes| == 0 then false
  else StartsWith(name, prefixes[0]) || AnyPrefix(prefixes[1..], name)
}

// DriverRunner.cs:383-385 IsAllowed
function IsAllowed(name: string): bool
{
  name in Exact
  || AnyPrefix(AllowedPrefixes, name)
}

// DriverRunner.cs:388-397 BuildStepEnvironment
// foreach over the parent's entries: keys are distinct, so the insertion order does not
// change the resulting map; each allowlisted key is copied with its value.
function BuildStepEnvironment(parent: map<string, string>): map<string, string>
{
  map name | name in parent && IsAllowed(name) :: parent[name]
}

// DriverRunner.cs:406-410: foreach (var name in CaTrustNames) if (parent[name] is string value) environment[name] = value;
function CopyCaTrust(environment: map<string, string>, names: seq<string>, parent: map<string, string>): map<string, string>
  decreases |names|
{
  if |names| == 0 then environment
  else
    var name := names[0];
    var environment' := if name in parent then environment[name := parent[name]] else environment;
    CopyCaTrust(environment', names[1..], parent)
}

// DriverRunner.cs:403-414 DriverRunEnvironment
function DriverRunEnvironment(parent: map<string, string>, context: DriverContext): map<string, string>
{
  var environment := BuildStepEnvironment(parent);
  var environment1 := CopyCaTrust(environment, CaTrust, parent);
  // foreach (var (name, value) in BuildEnvironment(context)) environment[name] = value;
  environment1 + BuildEnvironment(context)
}

// ---------------------------------------------------------------------------
// Helper lemmas
// ---------------------------------------------------------------------------

lemma CopyCaTrustKeys(environment: map<string, string>, names: seq<string>, parent: map<string, string>, k: string)
  ensures k in CopyCaTrust(environment, names, parent) <==> (k in environment || (k in names && k in parent))
  decreases |names|
{
  if |names| > 0 {
    var name := names[0];
    var environment' := if name in parent then environment[name := parent[name]] else environment;
    CopyCaTrustKeys(environment', names[1..], parent, k);
    assert k in names <==> (k == names[0] || k in names[1..]);
  }
}

lemma CopyCaTrustValue(environment: map<string, string>, names: seq<string>, parent: map<string, string>, k: string)
  requires k in names && k in parent
  ensures k in CopyCaTrust(environment, names, parent) && CopyCaTrust(environment, names, parent)[k] == parent[k]
  decreases |names|
{
  var name := names[0];
  var environment' := if name in parent then environment[name := parent[name]] else environment;
  if k in names[1..] {
    CopyCaTrustValue(environment', names[1..], parent, k);
  } else {
    assert k == name;
    CopyCaTrustKeys(environment', names[1..], parent, k);
    CopyCaTrustPreserves(environment', names[1..], parent, k);
  }
}

lemma CopyCaTrustPreserves(environment: map<string, string>, names: seq<string>, parent: map<string, string>, k: string)
  requires k in environment && k !in names
  ensures k in CopyCaTrust(environment, names, parent) && CopyCaTrust(environment, names, parent)[k] == environment[k]
  decreases |names|
{
  if |names| > 0 {
    var name := names[0];
    var environment' := if name in parent then environment[name := parent[name]] else environment;
    assert k in names[1..] ==> k in names;
    CopyCaTrustPreserves(environment', names[1..], parent, k);
  }
}

lemma HarnessSecretNotAllowed(s: string)
  requires s in HarnessSecrets
  ensures !IsAllowed(s)
{
  // Each harness secret starts with 'I'; no allowed name and no allowed prefix does.
  HarnessSecretsStartWithI(s);
  NoExactStartsWithI();
  NoPrefixStartsWithI();
  assert s !in Exact;
  AnyPrefixFirstChar(AllowedPrefixes, s);
}

lemma HarnessSecretsStartWithI(s: string)
  requires s in HarnessSecrets
  ensures |s| > 0 && s[0] == 'I'
{
  var i :| 0 <= i < |HarnessSecrets| && HarnessSecrets[i] == s;
  if i == 0 {} else if i == 1 {} else if i == 2 {} else if i == 3 {} else {}
}

lemma NoExactStartsWithI()
  ensures forall e :: e in Exact ==> |e| > 0 && e[0] != 'I'
{
  forall e | e in Exact
    ensures |e| > 0 && e[0] != 'I'
  {
    var i :| 0 <= i < |Exact| && Exact[i] == e;
  }
}

lemma NoPrefixStartsWithI()
  ensures forall p :: p in AllowedPrefixes ==> |p| > 0 && p[0] != 'I'
{
  forall p | p in AllowedPrefixes
    ensures |p| > 0 && p[0] != 'I'
  {
    var i :| 0 <= i < |AllowedPrefixes| && AllowedPrefixes[i] == p;
  }
}

lemma AnyPrefixFirstChar(prefixes: seq<string>, name: string)
  requires |name| > 0
  requires forall p :: p in prefixes ==> |p| > 0 && p[0] != name[0]
  ensures !AnyPrefix(prefixes, name)
  decreases |prefixes|
{
  if |prefixes| > 0 {
    var p := prefixes[0];
    assert p in prefixes;
    if |p| <= |name| {
      assert name[..|p|][0] == name[0];
      assert name[..|p|] != p;
    }
    assert !StartsWith(name, p);
    assert forall q :: q in prefixes[1..] ==> q in prefixes;
    AnyPrefixFirstChar(prefixes[1..], name);
  }
}

// ---------------------------------------------------------------------------
// Property lemmas (frozen)
// ---------------------------------------------------------------------------

// Citation: design HarnessEnv.P1_BuildStepsGetNoHarnessSecret: "Build steps (`:175`) get the allowlist only." (spec line 184).
lemma P1_BuildStepsGetNoHarnessSecret(parent: map<string, string>, s: string)
  requires s in HarnessSecrets
  ensures s !in BuildStepEnvironment(parent)
{
  HarnessSecretNotAllowed(s);
}

// Citation: design HarnessEnv.P2_DriverRunGetsNoHarnessSecret: "starts every process from an empty environment plus an allowlist" (spec line 184); test `DriverRunEnvironment_AddsTheCaTrustVariablesAndTheContext_AndNoHarnessSecret`.
lemma P2_DriverRunGetsNoHarnessSecret(parent: map<string, string>, ctx: DriverContext, s: string)
  requires s in HarnessSecrets
  ensures s !in DriverRunEnvironment(parent, ctx)
{
  HarnessSecretNotAllowed(s);
  var e0 := BuildStepEnvironment(parent);
  assert s !in e0;
  assert s !in CaTrust;
  CopyCaTrustKeys(e0, CaTrust, parent, s);
  assert s !in BuildEnvironment(ctx).Keys;
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
  var e0 := BuildStepEnvironment(parent);
  CopyCaTrustValue(e0, CaTrust, parent, v);
  assert v !in BuildEnvironment(ctx).Keys;
}

// Citation: design HarnessEnv.P5_BuildStepsKeepExactAllowlist: "an allowlist (`PATH`, `HOME`, `TMPDIR`, `TEMP`, `TMP`, `LANG`, `LC_*`, `USER`, `SHELL`, …)" (spec line 184).
lemma P5_BuildStepsKeepExactAllowlist(parent: map<string, string>, v: string)
  requires v in parent && v in Exact
  ensures v in BuildStepEnvironment(parent) && BuildStepEnvironment(parent)[v] == parent[v]
{
}

// Citation: test `BuildStepEnvironment_IsTheAllowlistOnly`; "starts every process from an empty environment plus an allowlist" (spec line 184).
lemma P6_BuildStepsOnlyAllowlisted(parent: map<string, string>, v: string)
  requires v in BuildStepEnvironment(parent)
  ensures v in parent && IsAllowed(v)
{
}
