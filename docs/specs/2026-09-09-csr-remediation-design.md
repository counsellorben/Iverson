# CSR Remediation — Design

**Date:** 2026-09-09
**Source review:** `docs/criticalreviews/2026-09-09-iverson-critical-security-review-1.md` (12 findings: 2 High, 4 Medium, 6 Low; SQS 16/100)
**Base:** `main` @ `a62177d`
**Scope:** close all 12 findings.

---

## 1. Context and constraints

The review found the application-layer security engineering sound — deny-by-default authorization, a four-layer tenant boundary, well-defended raw-SQL surfaces — with the failures concentrated at the deployment edge and in the client SDKs.

**Deployment status: local/kind only.** Nothing has been deployed to a cloud environment. Three consequences:

- No credential rotation is in scope. Nothing real has crossed a wire.
- The `values-aws/azure/gcp.yaml` files are *templates* to correct before a first deploy, not live configuration.
- Urgency is normal-development, not incident-grade. There is no hotfix path.

**Precedent.** The 2026-07-16 CSR remediation closed 9 findings via 12 SDD tasks in a dedicated worktree, with per-task review plus a final whole-branch review. That structure worked and is reused here. That effort also found the CSR document itself contained a materially wrong claim; accordingly, every finding in this remediation was re-verified against the codebase during design rather than taken from the report on faith. Three claims in the report were corrected as a result (§6).

---

## 2. Structure

One git worktree off `main`, **11 SDD tasks**, per-task review between each, final whole-branch review scoped to seams before merge.

> **Worktree gotcha.** `main` currently has 20 unpushed commits, and worktrees branch from `origin/main`. Either push `main` first, or run `git merge main --ff-only` inside the worktree immediately after creating it. This exact failure bit the last CSR remediation.

| # | Task | Findings | Subsystem |
|---|---|---|---|
| 1 | Delete `/probe/*`; Ingress path allow-list | #1 | `Iverson.Api` + Helm |
| 2 | Cloud values → `https`; scheme-templated redirect URIs | #2a | Helm values + blueprint |
| 3 | Gate test/bypass identities and tenant seeding | #5 | Blueprint + `Program.cs` |
| 4 | SDK TLS default flip — Python, TypeScript | #2b | Clients |
| 5 | SDK TLS default flip — Go, Java, .NET | #2b | Clients |
| 6 | Authorize HAVING clauses | #3 | `Iverson.StarRocks` |
| 7 | Agent cache key + document delimiters | #4, #11 | `Iverson.Agents` |
| 8 | Connection strings fail closed | #6 | `appsettings` + compose + Helm |
| 9 | Security headers | #7 | nginx + Ingress |
| 10 | `/v1/traces` body limit; Authentik pagination | #10, #12 | `Iverson.Api` |
| 11 | SSH.NET pin; CI checksums; action SHA pinning | #8, #9 | Deps + CI |

**Grouping.** Tasks 2 and 3 are adjacent because both edit `blueprints-configmap-service-clients.yaml`. The five SDKs split across two tasks because one diff spanning five languages is not reviewable. Tasks 7, 10 and 11 each pair findings sharing a file or category.

**Ordering.** Blockers first (1, 2), then the config work sharing their files (3), then the largest and most breaking change (4, 5) while review attention is freshest, then isolated code fixes (6–8), then config and supply chain (9–11).

---

## 3. Task designs

### Task 1 — Delete `/probe/*`; Ingress path allow-list (Finding #1)

Delete the four `/probe/*` endpoints from `Program.cs` (:345, :351, :357, :363). They have **zero consumers** anywhere in the repository; `/health` already aggregates the same four backend checks.

Replace the api Ingress rule (`path: /`, `pathType: Prefix`) with two rules:

| Path | Type | Purpose |
|---|---|---|
| `/iverson.` | Prefix | All six gRPC services — they share proto package `iverson`, so one prefix covers every service and future ones |
| `/v1/traces` | Prefix | The admin UI's same-origin OTLP relay |

`/health`, `/health/live`, `/build` and `/metrics` stay exactly where they are and keep `AllowAnonymous`. Once the Ingress stops publishing `/`, they are unreachable from outside regardless of port, and in-cluster access is already constrained by the default-deny NetworkPolicies.

**Explicitly not doing:** moving those endpoints to port 8081, which the source review recommended. Verification showed it buys no security beyond the allow-list while forcing changes to the Prometheus scrape target and its NetworkPolicy. See §6.

### Task 2 — Transport scheme in cloud values and OIDC blueprint (Finding #2a)

Introduce **one** new value, `global.externalScheme`, defaulting to `http`, set to `https` in `values-aws.yaml`, `values-azure.yaml` and `values-gcp.yaml`. It templates both:

- `adminUi.oidcAuthority` in the three cloud values files (currently hardcoded `http://` while `apiBaseUrl` is already `https://`)
- The blueprint's redirect URIs in `blueprints-configmap-service-clients.yaml:117-129`, currently hardcoded `http://{{ .Values.global.ingressHost }}/admin/callback` with `matching_mode: strict`

Stating the scheme once removes the mismatch between the app's origin and the registered redirect URI, which would otherwise fail strict matching the moment TLS is enabled.

The `http://localhost:5173/*` redirect URIs are gated by Task 3's flag rather than by a second value of their own — they are dev fixtures, which is exactly what that flag governs.

`values-laptop.yaml` and `values-local.yaml` inherit the `http` default; no change needed beyond Task 3's flag.

### Task 3 — Gate test identities and tenant seeding (Finding #5)

Add `global.provisionTestIdentities`, **default `false`**, set `true` in `values-local.yaml` and `values-laptop.yaml`.

Gated entries in `blueprints-configmap-service-clients.yaml`:

- `iverson-acting-user-smoke-test` user
- `iverson-loadtest-bypass` group and `iverson-loadtest-bypass-user`
- `loadtest` and `webtest` OAuth2 service clients and applications
- `tenant_id_loadtest` and `tenant_id_webtest` scope mappings
- `iverson-loadtest-human` provider and application
- The `http://localhost:5173/*` redirect URIs (from Task 2)

**Not gated:** `iverson-admin-orchestrator` and its API token. Verification showed these back `Authentik__AdminToken`, wired at `charts/api/templates/deployment.yaml:158-160` and consumed by `IdpAdminClient` for the `TenantAdmin` RPCs. That is production functionality, not a test fixture. See §6.

Server side: gate the `Program.cs:438-439` legacy tenant seeding loop behind `Tenancy:SeedLegacyTenants` (bool, default `false`), read with the existing `cfg.GetValue("Key", default)` convention and wired from the Helm value using the existing `Engagement__Enabled` env-var pattern (`deployment.yaml:94-95`).

compose is unaffected: it uses the separate static `blueprints/compose-only/` file, which Helm does not template. It will need `Tenancy__SeedLegacyTenants=true` added to keep its seeded tenants.

### Tasks 4 and 5 — SDK transport defaults (Finding #2b)

Flip the transport default to TLS in all **five** SDKs. Task 4 covers Python and TypeScript; Task 5 covers Go, Java and .NET.

| SDK | Current default | Change |
|---|---|---|
| Python | `use_tls: bool = False` (`core.py:828`) | Default `True`; explicit `use_tls=False` for plaintext |
| TypeScript | `useTls: boolean = false` (`core.ts:810`) | Default `true` |
| Go | `grpc.WithInsecure()` (`coordinator.go:81`) | `credentials.NewTLS` by default; explicit insecure option |
| Java | `.usePlaintext()` hardcoded in all three `(host, port, …)` constructors (`IversonClient.java:47,68,77`) | TLS-by-default convenience constructors; plaintext via an explicit named factory |
| .NET | `UnsafeUseInsecureChannelCallCredentials = true` set unconditionally (`ServiceCollectionExtensions.cs:93`) | Make it conditional on an explicit opt-in threaded through `AddIversonClient` |

Every SDK can express TLS with system trust roots using its existing gRPC dependency; no new packages.

All local consumers explicitly opt into plaintext: the per-language test suites, the five conformance drivers, the samples, `Iverson.Agents/Python/iverson_agent/__main__.py`, and `Iverson.LoadTest/Program.cs`. Roughly 15 files.

This is a **breaking change** for any external consumer of these SDKs, accepted deliberately (§7).

### Task 6 — Authorize HAVING clauses (Finding #3)

`BuildHaving` (`StarRocksQueryBuilder.cs:604`) currently takes `(clauses, logic, param, paramPrefix)` — no schema, no `tableMap`, no `authz` — and splices `clause.Property` verbatim after backtick-escaping. `BuildGroupBy` gates GROUP BY keys, metric fields, metric expressions and ORDER BY with `IsFieldAllowed`; HAVING is the one clause family that was missed.

Extend the signature to accept the resolver and authz context, and validate each `clause.Property` as **either**:

1. a member of the statement's output-alias set, **or**
2. a column that passes `IsFieldAllowed`

rejecting everything else with the same exception shape the sibling gates use.

The alias sets differ per call site and must be passed explicitly, not inferred:

| Call site | Alias set |
|---|---|
| `BuildAggregate` (:262) | `bucket_key`, `doc_count`, `metric_val` (fixed) |
| `BuildGroupBy` (:380) | `request.Metrics.Select(m => m.Name)`, plus the GROUP BY key columns (already authorized) |
| `StarRocksPipelineBuilder` (:518) | Already pre-validated via `RequireColumn` against `metricAliases`; unchanged |

**Two existing tests change:**

- `BuildGroupBy_HavingPropertyWithBacktick_EscapesEmbeddedBacktick` (`StarRocksQueryBuilderTests.cs:2064`) passes `Property = "evil\`alias"` and asserts the escaping. That property is neither an alias nor a column, so it will now be rejected. Rewrite it to assert rejection. `EscapeIdentifier` remains as defence-in-depth rather than the primary control.
- `BuildHaving_PrefixOverload_UsesPrefix` (:2619) calls the four-argument overload and needs updating for the new signature.

Add negative tests mirroring the existing GROUP BY / ORDER BY authorization tests, so all four clause families stay in lockstep.

### Task 7 — Agent cache key and document delimiters (Findings #4, #11)

**Cache key.** `_user_key` (`session.py:97-104`) base64-decodes the JWT payload and returns `sub` **without verifying the signature**; that value keys `SchemaCache`, and a cache hit returns without contacting the server (`schema.py:42-43`). Replace it with `hashlib.sha256(token.encode()).hexdigest()` — unforgeable, preserves per-user cache locality, no parsing, no claim trust.

`test_user_key_is_the_jwt_subject_when_the_token_is_a_jwt` (`tests/test_session.py:243-251`) pins exactly the behaviour being removed. Replace it with a test pinning the security property: a token carrying another user's `sub` must **not** collide with that user's cache key.

**No LRU bound**, despite the source review suggesting one. Entries are created only after a successful server round-trip, so growth is bounded by the real user count, not by attacker input. See §6.

**Document delimiters.** Wrap each document in explicit delimiters with a provenance marker in `_render_one` (`retrieval.py:168`) — the single formatting point, covering both the initial page and tool results — and add a line to `REASONER_SYSTEM` stating that document content is data, never instructions.

### Task 8 — Connection strings fail closed (Finding #6)

Verification changed this task materially. The `??` fallbacks in `Program.cs:163-168` are **dead code**: `appsettings.json` itself carries the credentials, so `GetConnectionString` never reaches them.

```json
"Postgres":  "Host=postgres;...;Username=iverson;Password=iverson",
"StarRocks": "Server=starrocks;...;Uid=root;Pwd=;AllowPublicKeyRetrieval=true;"
```

Changes:

1. Remove both connection strings from `appsettings.json`; put the dev values in `appsettings.Development.json` so `dotnet run` still works locally. Production then fails closed on missing configuration.
2. Add an explicit `ConnectionStrings__Postgres` to compose, which runs `ASPNETCORE_ENVIRONMENT=Production` and currently relies on `appsettings.json` for it. (compose already sets `ConnectionStrings__StarRocks`.)
3. Delete the now-unreachable `??` fallbacks in `Program.cs`.
4. Remove `AllowPublicKeyRetrieval=true` from all three locations — `appsettings.json`, `charts/api/templates/deployment.yaml:92`, and both compose services — and **verify the StarRocks connection still works** against compose. If StarRocks genuinely requires it, restore it and document why in the task report. This is the one item settled by experiment rather than by design.

### Task 9 — Security headers (Finding #7)

No `Content-Security-Policy`, `Strict-Transport-Security`, `X-Frame-Options` or `X-Content-Type-Options` exists anywhere in the repository. Add them in `Iverson.AdminUI/nginx.conf`, which already replaces the base image's `default.conf`:

- `X-Frame-Options: DENY` and CSP `frame-ancestors 'none'` — the console drives tenant lifecycle and user management
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: no-referrer`
- `Content-Security-Policy` — verification confirmed the app loads only same-origin scripts (`config.js` and the bundled entry point), no CDN and no external fonts, so `default-src 'self'` plus `connect-src 'self' <oidc-authority>` is sufficient. Check whether the Vite build emits inline styles requiring `style-src 'self' 'unsafe-inline'`.
- `Strict-Transport-Security` — templated on `global.externalScheme` from Task 2, emitted only when the scheme is `https`

Mirror the header set at the Ingress via the existing `ingress.annotations` passthrough so API responses are covered too.

### Task 10 — `/v1/traces` limit and Authentik pagination (Findings #10, #12)

**`/v1/traces`** (`Program.cs:461-470`) streams the request body to Jaeger with no size limit and forwards the caller's `Content-Type` verbatim, gated only by bare `RequireAuthorization()`. Add a per-endpoint request body size limit and restrict `Content-Type` to `application/x-protobuf`. This is not SSRF — the destination is a fixed configured `BaseAddress` and no part of the request influences it.

**`IdpAdminClient.ListUsersByTenantAsync`** (`IdpAdminClient.cs:57-101`) infers Authentik's pagination envelope; the class comment records it was never verified against a live instance. If the shape differs, the method silently truncates. `RequireUserInTenantAsync` fails closed on truncation (safe), but user listing and offboarding fail open (not safe).

Verify the envelope against a running Authentik (compose brings one up) or its `/api/v3/schema/` document, then treat an unrecognised shape as an error rather than end-of-list.

### Task 11 — Supply chain (Findings #8, #9)

**SSH.NET.** `2023.0.0` (GHSA-q939-rpr3-3284, High) arrives transitively via `Testcontainers 3.9.0` in four test projects. No production project is affected. There is **no** `Directory.Packages.props`, so add a direct `PackageReference` to a patched version in each of `Iverson.Api.Tests`, `Iverson.Sql.Tests`, `Iverson.StarRocks.Tests` and `Iverson.Vector.Tests`. Prefer this over upgrading Testcontainers to 4.x, which is a breaking change for test infrastructure and out of proportion to a test-only advisory.

**CI integrity.** `.gitlab-ci.yml` downloads kubeconform, kube-score and tfsec release archives and executes them with no verification (`:26-28`, `:47-51`). Pin each to a published SHA-256 and verify before extraction. Pin the GitHub Actions in `.github/workflows/` by commit SHA rather than major tag.

---

## 4. Definition of done

**Per task:** implementation plus tests, and a clean per-task review before the next task starts.

**Before merge:** the full non-integration .NET suite, the agent's pytest suite, TypeScript vitest, and the Go and Java client suites all green. Because Tasks 4 and 5 change transport defaults across five SDKs, the **client conformance harness must pass in all five languages**. Then a final whole-branch review scoped to seams rather than to tasks.

> **There is no CI that runs these suites.** GitHub Actions is CodeQL plus deploy-validate; GitLab CI is deploy-validate only. Every suite gate above is a local activity and must be run deliberately. Establish the baseline on `main` when the worktree is created, so a pre-existing failure is not misattributed to this work.

**Regression surface — what must still work:** compose bring-up, kind bring-up and its smoke test, the LoadTest, the five-language conformance harness, and the admin UI login flow end to end. Tasks 3, 4 and 5 are the likeliest to break these, since gated identities and flipped transport defaults both cut through the local dev loop.

---

## 5. Verified assumptions

40 assumptions were enumerated against the design before any verification, then checked against the codebase.

**Confirmed:**

| Assumption | Evidence |
|---|---|
| `/probe/*` has zero consumers | Repo-wide grep: only the definitions in `Program.cs` |
| All gRPC services share one proto package | `Common/Proto/*.proto` — all six declare `package iverson` |
| Prometheus does not scrape via the Ingress | `charts/prometheus/templates/configmap.yaml:12` — target `{{ .Release.Name }}-api:8081` |
| `/build` is consumed only in-process | `BuildIdentityEndpointTests.cs:15,21` — `AuthTestWebApplicationFactory` |
| Admin UI makes no API calls | No gRPC/fetch calls in `src/pages/`; only OIDC and same-origin `/v1/traces` |
| Admin orchestrator is production infrastructure | `charts/api/templates/deployment.yaml:158-160` wires `Authentik__AdminToken` from its Secret |
| Conformance harness needs the gated identities | `Iverson.ClientConformance/{TokenBroker,Requirements,Scenarios/IdentityScenario}.cs` |
| Config and Helm wiring conventions exist | `Program.cs:171` (`cfg.GetValue`), `deployment.yaml:94-95` (`Engagement__Enabled`) |
| `BuildAggregate` alias set | `{bucket_key, doc_count, metric_val}` — grep of emitted `AS` clauses |
| `BuildGroupBy` alias set | `metric.Name` via `quotedName` at `StarRocksQueryBuilder.cs:427` |
| `_user_key` has one caller | `session.py:132` |
| `_render_one` is the single format point | `retrieval.py:168`, used by `render_context` and `_render_block` |
| Admin UI loads no external origins | `index.html` — only `config.js` and the bundled entry point |
| `.NET` insecure flag is unconditional | `ServiceCollectionExtensions.cs:93` inside `AttachCredentials` |
| HAVING field-authz gap is real | Probe test during the review: GROUP BY and ORDER BY rejected an unauthorized field; HAVING emitted `HAVING \`Rating\` > @h0` |

**Wrong or under-specified — design changed:**

| Assumption | What verification found | Effect |
|---|---|---|
| compose and Helm both set both connection strings | `appsettings.json` carries them; the `Program.cs` fallbacks are dead code; compose sets only StarRocks; Helm also sets `AllowPublicKeyRetrieval=true` | **Task 8 redesigned** |
| Three cloud values files | `values-laptop.yaml` also exists (six total) | Task 2/3 coverage widened |
| Six client SDKs | Five: DotNet, Go, Java, Python, TypeScript (`Common` is protos) | Task 4/5 scope corrected |
| No existing test pins HAVING behaviour | Two do (`:2064`, `:2619`) | Task 6 includes rewriting them |
| No agent test pins `_user_key` | `test_session.py:243-251` does | Task 7 includes replacing it |
| A central package-version file exists | None; no `Directory.Packages.props` | Task 11 pins in 4 projects |
| CI runs the test suites | It does not | Done-criteria state the local-only gate |

**Deferred to task time (need a live service):** whether StarRocks requires `AllowPublicKeyRetrieval` (Task 8); the Authentik pagination envelope (Task 10); whether StarRocks *executes* the HAVING reference — severity only, the fix is identical either way (Task 6); the suite baseline on `main`.

---

## 6. Corrections to the source review

Three claims in `2026-09-09-iverson-critical-security-review-1.md` were found wrong during design and are corrected here. The review file is left as the historical record.

1. **".NET requires an explicit insecure-channel opt-in."** Repeated from a comment in `IversonClient.java`. It does not: `ServiceCollectionExtensions.cs:93` sets `UnsafeUseInsecureChannelCallCredentials = true` unconditionally, defeating grpc-dotnet's own guard for every consumer. .NET is a fifth instance of Finding #2, not the safe counter-example.

2. **"Move `/health`, `/build`, `/metrics` to port 8081."** Over-built. The Ingress path allow-list alone closes the exposure; the port move would additionally force Prometheus scrape-target and NetworkPolicy changes for no security gain.

3. **"`iverson-admin-orchestrator` is a test identity."** It backs `Authentik__AdminToken` and the production `TenantAdmin` RPCs. It stays provisioned in every environment.

A fourth item was rescoped rather than corrected: the review recommended an LRU bound on the agent's schema cache. Cache entries are created only after a successful server round-trip, so growth is bounded by real user count; the bound is speculative capacity work and is dropped.

---

## 7. Known issues accepted as out of scope

Agreed with **ben** during design:

- **Narrowing `iverson-admin-orchestrator` off `is_superuser: true`.** Real Authentik-role work with uncertain payoff. The account remains a standing superuser in every environment.
- **Rotating local dev credentials.** They are intentionally static and local-only, and the 2026-07-16 remediation settled the same question with documentation rather than rotation. Nothing has been deployed to a cloud, so nothing has crossed a real wire.
- **In-cluster TLS / mTLS between services.** Every finding here concerns the edge and the client SDKs; in-cluster hops are covered by the default-deny NetworkPolicies. A service mesh is a far larger project.
- **Breaking change to the client SDKs.** Flipping transport defaults to TLS breaks any external consumer of the five SDKs. Accepted deliberately in preference to a narrower credentials-only gate.
- **`AllowedHosts: "*"`** in `appsettings.json`. Noted during verification; no Host-header-dependent logic was identified, so it is not treated as a finding.
