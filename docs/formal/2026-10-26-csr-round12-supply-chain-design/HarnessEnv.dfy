// Spec: /home/ben/repositories/Iverson/docs/specs/2026-10-26-csr-round12-supply-chain-design.md @ f6f5db2cbea0425d938739f73a8d6e9147479682
// Source: §5 item 4, lines 184-184
// Re-run: dafny verify --verification-time-limit 30 --warn-contradictory-assumptions --manual-lemma-induction docs/formal/2026-10-26-csr-round12-supply-chain-design/HarnessEnv.dfy
// Round: 1

type Env = map<string, string>

const Exact: set<string> := {"PATH", "HOME", "TMPDIR", "TEMP", "TMP", "LANG", "USER", "SHELL", "M2_HOME", "VIRTUAL_ENV"}
const Prefixes: set<string> := {"LC_", "DOTNET_", "NUGET_", "JAVA_HOME", "MAVEN_", "GO", "NODE_", "NPM_CONFIG_", "npm_config_", "PYTHON", "PIP_"}
const CaTrust: set<string> := {"SSL_CERT_FILE", "SSL_CERT_DIR", "GRPC_DEFAULT_SSL_ROOTS_FILE_PATH", "JAVA_TOOL_OPTIONS"}
const HarnessSecrets: set<string> := {"IVERSON_CLIENT_SECRET", "IVERSON_CLIENT_ID", "IVERSON_TOKEN_ENDPOINT", "IVERSON_CLIENT_SCOPE", "IVERSON_ACTING_USER_HOST_HEADER"}
const DriverKeys: set<string> := {"IVERSON_DRIVER_CLIENT_SECRET", "IVERSON_DRIVER_SERVICE_TOKEN", "IVERSON_DRIVER_ACTING_TOKEN", "IVERSON_DRIVER_WRONG_ACTING_TOKEN"}

// Allowlist: exact names or names starting with a toolchain prefix (line 184).
predicate Allowed(name: string)
{
  name in Exact || exists p :: p in Prefixes && p <= name
}

// Empty environment plus the allowlist taken from the parent (line 184).
function BuildEnv(parent: Env): Env
{
  map k | k in parent.Keys && Allowed(k) :: parent[k]
}

// Driver run: allowlist, plus CA-trust variables from the parent, plus the context (line 184).
function CaTrustEnv(parent: Env): Env
{
  map k | k in parent.Keys && k in CaTrust :: parent[k]
}

function RunEnv(parent: Env, ctx: Env): Env
{
  (BuildEnv(parent) + CaTrustEnv(parent)) + ctx
}

lemma NotAllowed(s: string)
  requires s in HarnessSecrets
  ensures !Allowed(s)
{
  forall p | p in Prefixes
    ensures !(p <= s)
  {
    assert p == "LC_" || p == "DOTNET_" || p == "NUGET_" || p == "JAVA_HOME" || p == "MAVEN_" || p == "GO"
        || p == "NODE_" || p == "NPM_CONFIG_" || p == "npm_config_" || p == "PYTHON" || p == "PIP_";
    assert s == "IVERSON_CLIENT_SECRET" || s == "IVERSON_CLIENT_ID" || s == "IVERSON_TOKEN_ENDPOINT"
        || s == "IVERSON_CLIENT_SCOPE" || s == "IVERSON_ACTING_USER_HOST_HEADER";
    assert |p| > 0 && |s| > 0 && s[0] == 'I' && p[0] != 'I';
  }
}

// Citation: "**Harness build tools get no secrets.**" … "Build steps (`:175`) get the allowlist only." (§5 item 4, line 184)
lemma P1_BuildStepsGetNoHarnessSecret(parent: Env, s: string)
  requires s in HarnessSecrets
  ensures s !in BuildEnv(parent)
{
  NotAllowed(s);
}

// Citation: "starts every process from an empty environment plus an allowlist" (line 184)
lemma P2_DriverRunGetsNoHarnessSecret(parent: Env, ctx: Env, s: string)
  requires ctx.Keys == DriverKeys && s in HarnessSecrets
  ensures s !in RunEnv(parent, ctx)
{
  NotAllowed(s);
}

// Citation: "the driver run step adds `BuildEnvironment(context)` (VA 40)" (line 184)
lemma P3_DriverRunGetsItsContext(parent: Env, ctx: Env, k: string)
  requires ctx.Keys == DriverKeys && k in ctx
  ensures k in RunEnv(parent, ctx) && RunEnv(parent, ctx)[k] == ctx[k]
{
}

// Citation: "and the CA-trust variables `SSL_CERT_FILE`, `SSL_CERT_DIR`, `GRPC_DEFAULT_SSL_ROOTS_FILE_PATH` and `JAVA_TOOL_OPTIONS`, which the drivers need to verify the compose TLS server (VA 48)" (line 184)
lemma P4_DriverRunGetsCaTrust(parent: Env, ctx: Env, v: string)
  requires ctx.Keys == DriverKeys && v in CaTrust && v in parent
  ensures v in RunEnv(parent, ctx) && RunEnv(parent, ctx)[v] == parent[v]
{
}

// Citation: "an allowlist (`PATH`, `HOME`, `TMPDIR`, `TEMP`, `TMP`, `LANG`, `LC_*`, `USER`, `SHELL`, …)" (line 184)
lemma P5_BuildStepsKeepExactAllowlist(parent: Env, v: string)
  requires v in parent && v in Exact
  ensures v in BuildEnv(parent) && BuildEnv(parent)[v] == parent[v]
{
}
