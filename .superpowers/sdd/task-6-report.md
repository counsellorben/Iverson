# Task 6 Report: Authentik `tenant_id` claim provisioning

## Commit

`b368425` — "feat(deploy): issue tenant_id claim for human and service callers"

## What was implemented

### Step 1: Human `tenant_id` scope-mapping
- Added `authentik_providers_oauth2.scopemapping` entry `tenant_id`, expression
  `return {"tenant_id": request.user.attributes.get("tenant_id")}`, in both
  `blueprints/compose-only/service-clients.yaml` and
  `templates/blueprints-configmap-service-clients.yaml` (top-level scopemapping
  block, alongside the existing `admin`/`schema_admin`/`groups` mappings).
- Bound it via `property_mappings` to both human providers:
  `iverson-oidc-default` and `iverson-loadtest-human` (added alongside the
  existing `groups` binding on each).
- Seeded a `tenant_id` attribute onto both seeded human test users' `attrs.attributes`:
  - `iverson-acting-user-smoke-test` → `tenant-smoke-test`
  - `iverson-loadtest-bypass-user` → `tenant-bypass`
  (Both users authenticate via `iverson-loadtest-human`, per `Program.cs`'s
  shared `actingUserClientId`, so both needed a `tenant_id` value for the
  attribute-sourced mapping to return anything.)
- Updated `mint_acting_user_token.py:316`: `"scope": "openid groups"` →
  `"scope": "openid groups tenant_id"`.
- Updated `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs:249`
  (the only other human-flow scope-request literal, used by LoadTest's own
  acting-user token minting): `scope=openid%20groups%20offline_access` →
  `scope=openid%20groups%20tenant_id%20offline_access`.

### Step 2: Per-client constant `tenant_id` scope-mappings
- Added three constant scopemappings in both blueprint files:
  - `tenant_id_loadtest` → `{"tenant_id": "tenant-loadtest"}`
  - `tenant_id_webtest` → `{"tenant_id": "tenant-webtest"}`
  - `tenant_id_admin` → `{"tenant_id": "tenant-admin"}`
- Bound each to its own provider only, via `property_mappings`:
  - `iverson-loadtest` → `schema_admin`, `tenant_id_loadtest`
  - `iverson-webtest` → `tenant_id_webtest` (this provider previously had no
    `property_mappings` block at all — added one)
  - `iverson-admin-automation` → `admin`, `schema_admin`, `tenant_id_admin`
- In the Helm ConfigMap template's `{{- range $name := list "loadtest" "webtest" "admin-automation" }}` loop, extended the existing `if/else-if` on `$name` with a third `webtest` branch and added the matching tenant scope to each existing branch.

### Step 3: Parallel blueprint edits
Verified both files end up structurally identical (same scope-mapping set, same provider→scope-mapping bindings, same user attributes), differing only in indentation depth and Helm-templated pieces (client_id/secret/password lookups, `$name` range loop). See verification below.

### Step 4: Commit
Done — `b368425`.

## Verification

1. **YAML syntax** — parsed the compose-only file directly with `yaml.safe_load` (stripping the custom `!Find` tag): no errors.
2. **Helm template renders** — `helm template test-release . --show-only charts/authentik/templates/blueprints-configmap-service-clients.yaml` from `Iverson.Server/deploy/helm/iverson` succeeded with no errors (the chart's `lookup` calls resolve to `nil` outside a live cluster, which the existing `{{ if $secret }}...{{ else }}{{ randAlphaNum ... }}{{ end }}` pattern already handles).
3. **Embedded blueprint YAML valid** — extracted `data."service-clients.yaml"` from the rendered ConfigMap and parsed it with `yaml.safe_load` (stripping `!Find`): 20 entries, scope mappings = `['admin', 'schema_admin', 'groups', 'tenant_id', 'tenant_id_loadtest', 'tenant_id_webtest', 'tenant_id_admin']`.
4. **Structural parity between the two blueprint files** — extracted `oauth2provider → property_mappings` and `user → attributes` from both the compose-only file and the rendered ConfigMap template with a small Python script; outputs are byte-for-byte identical (same provider names, same scope bindings in the same order, same user tenant_id values).
5. **Every scope-request call site updated** — repo-wide grep for `scope.*openid` / `openid.*groups` across `*.py`/`*.cs` (excluding build output) turns up exactly two literals, both updated:
   - `Iverson.Server/deploy/scripts/mint_acting_user_token.py:316`
   - `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs:249`
6. **LoadTest still builds** — `dotnet build Iverson.LoadTest/Iverson.LoadTest.csproj -c Debug` succeeded, 0 warnings, 0 errors (confirms the C# string-literal edit is syntactically valid).

## Files changed

- `Iverson.Server/deploy/helm/iverson/charts/authentik/blueprints/compose-only/service-clients.yaml`
- `Iverson.Server/deploy/helm/iverson/charts/authentik/templates/blueprints-configmap-service-clients.yaml`
- `Iverson.Server/deploy/scripts/mint_acting_user_token.py`
- `Iverson.Server/Iverson.LoadTest/Auth/AuthentikFlowExecutorClient.cs` (not in the brief's original "Files" list, but required — see below)

## Self-review findings

1. **`AuthentikFlowExecutorClient.cs` was missing from the brief's file list but genuinely needed the same treatment.** The brief's "Files" section only names `mint_acting_user_token.py:316`, but LoadTest has its own independent human-flow token minter (`AuthentikFlowExecutorClient.MintAsync`) that requests `scope=openid groups offline_access` via its own authorize-URL construction, used by both `ActingUserIdentities` instances in `Program.cs` (the smoke-test user and the bypass user). This is a second, code-duplicated instance of the exact scope literal the brief asked me to update in the Python script. I updated it and consider this a necessary, in-scope addition, not scope creep — it's the same kind of edit at the same layer (adding `tenant_id` to a human token's requested scope), just in a second file the brief's author apparently didn't enumerate.

2. **No service-client (`client_credentials`) scope-request call site exists in LoadTest to update for Step 2, and I did not invent one.** I searched thoroughly for where LoadTest requests `admin`/`schema_admin` scopes for its client_credentials-based service identity (the `IversonClientCredentials` used for the primary gRPC channel, `Program.cs:71-73`) and found none — `Scope` is never set; it's `null` unconditionally (`new IversonClientCredentials(clientId, clientSecret, tokenEndpoint)`, no fourth arg). `IVERSON_CLIENT_ID`/`IVERSON_CLIENT_SECRET`/`IVERSON_TOKEN_ENDPOINT` are also unset anywhere in `docker-compose.yml` or setup scripts — per the LoadTest README they're "optional," implying LoadTest's automated runs don't currently exercise `admin`/`schema_admin` scopes at all (those exist for manual/documented curl-based admin operations, per `docs/user-management-and-security.md`). Since there is no existing precedent to mirror and `Program.cs` isn't in the brief's file list, I left this alone rather than fabricate a new `Scope` value or wire up something the brief didn't ask for. This is a pre-existing gap (predates this task, mirrors how `admin`/`schema_admin` are already unexercised by LoadTest's own automation), not something Task 6 introduces or is required to close.

3. **`docs/user-management-and-security.md`'s scope table (lines ~144-147) is now stale** — it enumerates only `groups` and `admin` as the two custom scope mappings, and its "Claims and scopes" narrative doesn't mention `tenant_id` or the new constant per-client mappings. Not in the brief's file list, so left unchanged; flagged here since a future reader of that doc would get an incomplete picture of which scopes exist. Low severity — the doc's *pattern* description ("define a new scopemapping... attach via property_mappings... request the scope explicitly") remains accurate and is exactly what I followed.

4. **Attribute/tenant value choices were not specified by the brief** and I picked reasonable non-production placeholders (`tenant-smoke-test`, `tenant-bypass`, `tenant-loadtest`, `tenant-webtest`, `tenant-admin`) consistent with the brief's own example (`tenant-loadtest`). These are dev/smoke-test-only values (same security posture as the existing hardcoded passwords/secrets in these files) and don't need to match any specific string for correctness — the live smoke test (explicitly out of scope per the brief) will exercise real cross-tenant behavior.

5. **No live smoke test was run** — explicitly out of scope per the brief ("A live smoke test... is executed during rollout, not in this plan"). Verification here is confined to YAML/Helm structural validation and grep sweeps, as instructed.

## Concerns

None blocking. The two items above (stale doc, no LoadTest service-scope precedent) are informational, not defects in this task's deliverable.

## Fix: LoadTest client_credentials scope

Applied a review finding: self-review item 2 above flagged that LoadTest's own base
gRPC channel `client_credentials` identity (`Program.cs:71-73`) never requests any
scope at all — not `tenant_id`, and not even the pre-existing `admin`/`schema_admin`.
The reviewer confirmed this is a real, if currently-inert, gap relative to the task
brief's literal instruction to "update the LoadTest token requests to include their
tenant scope." The user chose to fix it now.

**What changed:**
- `Iverson.Server/Iverson.LoadTest/Program.cs`: added
  `var clientScope = Environment.GetEnvironmentVariable("IVERSON_CLIENT_SCOPE");`
  alongside the existing `clientId`/`clientSecret`/`tokenEndpoint` reads, and passed
  it through as the 4th argument to `IversonClientCredentials` (its existing optional
  `Scope` parameter, already consumed by `CachedClientCredentialsTokenProvider` when
  present, was simply never populated before). No hardcoded scope value — this base
  identity's `IVERSON_CLIENT_ID` isn't tied to one specific named service client
  (`iverson-loadtest`/`iverson-webtest`/`iverson-admin-automation`); whichever one a
  deployment configures picks its own matching scope string via the new env var, e.g.
  `IVERSON_CLIENT_SCOPE="schema_admin tenant_id_loadtest"`.
- `Iverson.Server/Iverson.LoadTest/README.md`: added `IVERSON_CLIENT_SCOPE` to the
  "Other environment variables" table, documented as optional/unset by default,
  alongside the existing `IVERSON_CLIENT_ID`/`IVERSON_CLIENT_SECRET`/`IVERSON_TOKEN_ENDPOINT`
  row.

**Verification:**
- `dotnet build Iverson.Server/Iverson.LoadTest/Iverson.LoadTest.csproj` — succeeded,
  0 warnings, 0 errors.
- Searched for an existing unit test covering `Program.cs`'s top-level env-var/DI
  wiring (`grep -rl "IVERSON_CLIENT_ID\|IversonClientCredentials"` across
  `Iverson.Api.Tests` and `Iverson.LoadTest`) — none exists; `Program.cs` is a
  top-level-statements console entry point with no test harness around it. A clean
  build is the correct verification here, per the task instructions.

**Concerns:** None. Change is additive and backward-compatible — when
`IVERSON_CLIENT_SCOPE` is unset, `clientScope` is `null`, which is exactly the
previous behavior (unconditional `null` `Scope`).
