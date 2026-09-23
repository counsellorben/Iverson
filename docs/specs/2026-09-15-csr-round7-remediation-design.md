# CSR Round 7 Remediation — Design

Source: `docs/criticalreviews/2026-09-15-iverson-critical-security-review-7.md` (all 11 findings). Each finding is independently small and touches a disjoint set of files, so this design covers all 11 as one spec rather than eleven separate cycles — matching this session's established precedent for prior CSR remediation designs.

---

## 1. Python SDK OAuth2 token-fetch redirect guard (F1)

File: `Iverson.Clients/Python/iverson_client/auth.py`

`_CachedTokenProvider.get_token` is the only place in the Python SDK that constructs the token-fetch request (`iverson_client/core.py` only imports the class and checks the endpoint's URL scheme — it never builds its own request). Every one of the other four language SDKs explicitly disables automatic redirect-following on the OAuth2 client-credentials token-fetch HTTP call. The Python SDK currently relies on `urllib`'s default behavior, which happens not to leak `client_secret` via redirect (CPython's `HTTPRedirectHandler` raises on POST+307/308, and drops the request body entirely on POST+301/302/303) — but this makes its safety an accident of the standard library's own implementation choices rather than an explicit guarantee, and leaves it the only SDK of the five without one. The fix brings it in line with the other four explicitly.

**Fix:** install a custom opener with a redirect handler that refuses every redirect, and use it in place of the default:

```python
class _NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # refuse the redirect; caller sees the original response/error

_no_redirect_opener = urllib.request.build_opener(_NoRedirectHandler)
```
and in `get_token`:
```python
with _no_redirect_opener.open(request) as response:
    payload = json.loads(response.read())
```
One file, no new dependency. No existing Python SDK test exercises redirect-following behavior, so nothing needs updating besides adding a new test.

---

## 2. Branch protection + CI build/test jobs (F2)

**Branch protection on `main`:** `enforce_admins: false` — the repo's actual workflow is local merge commits pushed straight to `main` (not PRs; 54 commits are queued this way as of this design), and this setting keeps that workflow intact while still binding Dependabot's PRs and any non-admin actor, which covers both incidents this finding cites. No required review (solo-maintained repo).

Before applying, verify empirically which candidate status-check contexts can currently pass: once the 2 new jobs below exist, open a throwaway PR against `main`, let every candidate context report, and require only the ones observed green. The candidate list (subject to that verification):

- From `dependency-scan.yml` (7 jobs, each its own check context — no per-job `name:` override, so the context is the job id): `nuget-audit`, `npm-audit-adminui`, `npm-audit-ts-sdk`, `govulncheck`, `owasp-dependency-check-java`, `pip-audit-python-sdk`, `pip-audit-agents`
- From `codeql.yml` (one job, matrixed across 6 languages, each its own context named `Analyze (<language>)`): `Analyze (actions)`, `Analyze (csharp)`, `Analyze (go)`, `Analyze (java-kotlin)`, `Analyze (javascript-typescript)`, `Analyze (python)`
- The 2 new jobs this design adds (below)

`owasp-dependency-check-java` is expected to observe red during this verification pass (its NVD API key is not yet provisioned — see F8 in §9) and therefore won't be added to the required list by this process; add it once the key exists and it's independently observed passing. This is a natural consequence of the verify-first approach above, not a special case.

Configured via `gh api repos/counsellorben/Iverson/branches/main/protection` (confirmed the authenticated account has `admin: true` on this repo).

**New CI job — .NET build/test:** a new workflow `.github/workflows/dotnet-build.yml` (kept independent of `dependency-scan.yml`'s manifest-only scope) running `dotnet build Iverson.slnx` then `dotnet test --filter "Category!=Integration"`. Mirrored as a new job in `.gitlab-ci.yml` under a new `build-test` stage (the existing `stages:` list only has `validate`/`dependency-scan` — neither fits an application build/test job semantically).

**Prerequisite — the filter currently excludes only 4 of 23 container-referencing test files, not all container-dependent tests.** Only `PipelineIntegrationTests`, `TenantIsolationIntegrationTests`, `ObjectSearchVectorIntegrationTests`, and `RegisterSchemaAuthorizationIntegrationTests` carry `[Trait("Category", "Integration")]` today. A full sweep of every `IClassFixture<...ContainerFixture>`/`ICollectionFixture<...ContainerFixture>` consumer across `Iverson.Server` (cross-referenced against which fixtures genuinely start containers, vs. in-memory-only `WebApplicationFactory`-based fixtures, vs. static-method-only usage that starts nothing) found **13 additional test classes** that start real containers and need the same trait added: `EngagementStoreConsumerKafkaOrderingTests`, `DocumentRerenderQueuePostgresIntegrationTests`, `ReconciliationQueuePostgresIntegrationTests`, `StarRocksReadinessIntegrationTests`, `AuthentikRecoveryFlowIntegrationTests`, `DlqRepositoryPostgresIntegrationTests`, `PostgresIntegrationTests`, `TenantRepositoryPostgresIntegrationTests`, `StarRocksIntegrationTests`, `QdrantIntegrationTests`, `QdrantTenantIsolationIntegrationTests`, `QdrantVectorServiceTests`, `TenantScopedAccessIntegrationTests`. Tag all 13 with `[Trait("Category", "Integration")]` as a prerequisite step of this task — once done, the filter genuinely excludes every container-starting test everywhere it runs, so neither GitHub's nor GitLab's runner needs a Docker daemon for this job.

**New CI job — TypeScript SDK build/test:** `npm ci && npm test` in `Iverson.Clients/TypeScript` (typecheck + vitest, confirmed working after this session's earlier vitest/vite pin fix). Same placement pattern: new GitHub workflow job, new GitLab `build-test`-stage job.

---

## 3. Rate limiting (F7)

Two mechanisms, matched to each transport per this codebase's own existing convention (verified, not assumed — `ActingUserInterceptor` is the established precedent for exactly this kind of cross-cutting gRPC concern):

**gRPC entity CRUD/search RPCs** (`ObjectMapping`, `ObjectPersistence`, `ObjectRetrieval`, `ObjectSearch`): a new `RateLimitInterceptor : Interceptor`, registered once at `AddGrpc(options => options.Interceptors.Add<RateLimitInterceptor>())` alongside the existing `ActingUserInterceptor` — confirmed this registration style applies globally to all 4 services, not per-service. Uses `System.Threading.RateLimiting.PartitionedRateLimiter` (confirmed available at net10.0 via a real compile-and-run probe against `Microsoft.NET.Sdk.Web`, no new NuGet package needed), partitioned by the `sub` claim off `context.GetHttpContext().User` (confirmed reliably present — `MapInboundClaims = false` is already set on the default JWT scheme specifically so `FindFirst("sub")` resolves correctly, per the existing in-code comment documenting this exact mechanism). Rejected calls throw `RpcException(new Status(StatusCode.ResourceExhausted, ...))` — the same shape `ActingUserInterceptor` already uses successfully (confirmed no global exception handler exists anywhere in `Program.cs` that would intercept or reshape it first).

Limit: **50,000 requests/minute per principal**, sliding window. Sized generously above `Iverson.LoadTest`'s realistic ceiling — LoadTest defaults to 16 concurrent workers sharing **one** service credential with no self-throttling anywhere in its worker loop (confirmed by reading `Iverson.LoadTest/Program.cs`), and no live stack was available this session to measure its actual throughput, so this is an analytical estimate (roughly 15,000-30,000 req/min at a realistic 20-50ms per-call round-trip) with generous headroom above it, not a tuned number. Still bounds a truly pathological runaway to a real ceiling instead of unlimited.

**`/v1/traces`**: ASP.NET Core's `Microsoft.AspNetCore.RateLimiting` middleware. Two things the design must get right, both confirmed by direct testing (an in-process `TestServer` probe, not just reading the API): (1) `builder.Services.AddRateLimiter(...)` alone does nothing — `app.UseRateLimiter()` must also be added to the request pipeline, after `UseAuthentication()`/`UseAuthorization()` so the limiter can read `HttpContext.User` — without it, `.RequireRateLimiting("traces")` on the endpoint is silently inert (verified: 4/4 requests passed with it absent, 2 of 4 rejected once added); (2) a plain named limiter (`AddFixedWindowLimiter`/`AddSlidingWindowLimiter`) is a single limiter **shared across every caller**, not per-principal — the partitioned form is required for the "per principal" limit this design states: `options.AddPolicy("traces", ctx => RateLimitPartition.GetSlidingWindowLimiter(ctx.User.FindFirst("sub")?.Value ?? "anon", _ => new SlidingWindowRateLimiterOptions { ... }))`. Limit: **60 requests/minute per principal** — this endpoint is not a hot path for any legitimate caller (LoadTest doesn't call it; it's a browser/SDK trace-export relay).

---

## 4. DLQ `/admin/reconcile/{typeName}` hardening (F4, optional; F4's primary fix is tracked in §9)

File: `Iverson.Server/Iverson.Api/Program.cs:400-412`

Add the same acting-user requirement `/admin/dlq` and `/admin/dlq/{id}/replay` already have, for consistency, even though the CSR report calls this "not a live gap" (the endpoint returns no data, only triggers a reconciliation run):

```csharp
app.MapPost("/admin/reconcile/{typeName}", async (
    string typeName,
    Iverson.Api.Reconciliation.ReconciliationService reconciliation,
    AuditLog audit,
    HttpContext httpContext) =>
{
    var actingUserResult = await httpContext.AuthenticateAsync("ActingUser");
    if (!actingUserResult.Succeeded || actingUserResult.Principal is null)
        return Results.Unauthorized();
    // ... existing body unchanged
}).WithName("Reconcile").RequireAuthorization("Operator");
```

---

## 5. StarRocks DSL length cap (F5)

File: `Iverson.Server/Iverson.StarRocks/StarRocksPipelineBuilder.cs`

`RejectForbiddenCharacters` (line 374) is the single shared choke point for all 4 raw-expression entry points in the codebase (confirmed by reading every call site: `StarRocksPipelineBuilder.cs:220` (pipeline metric), `:389` (`ValidateDeriveExpr`), `StarRocksQueryBuilder.cs:250` (`BuildAggregate`), `:528` (`BuildMetricExpr`)) — adding the length check there covers all of them in one edit:

```csharp
internal static void RejectForbiddenCharacters(string expr, string errorContext)
{
    if (expr.Length > MaxExpressionLength)
        throw Invalid($"{errorContext}: expression exceeds {MaxExpressionLength} characters.");
    // ... existing character checks unchanged
}
```
`MaxExpressionLength = 1000`. Confirmed no existing test fixture uses an expression anywhere near this length (longest found: ~40 characters).

---

## 6. Escape `planner.py`'s schema text (F6)

File: `Iverson.Agents/Python/iverson_agent/planner.py`

Confirmed `render_schema`'s only caller is `session.py:137`, feeding directly into `plan(...)` — no other consumer expects raw/unescaped text, so escaping is safe. Reuse the existing `_escape` helper (imported the same way `evaluate.py` already does: `from iverson_agent.retrieval import _escape`) and add a tag boundary matching the codebase's established convention:

```python
prompt = f"<schema>\n{_escape(schema_text)}\n</schema>\n\nQuestion: {question}"
```
with the same "data, not instructions" framing already used for `<passage>`/`<answer>` in `evaluate.py`'s system prompt, extended to cover `<schema>` too.

---

## 7. Terraform state-backend hardening (F9)

- `Iverson.Server/deploy/terraform/bootstrap/gcp/main.tf`, `resource "google_storage_bucket" "state"`: add `public_access_prevention = "enforced"` (valid at the pinned `hashicorp/google ~> 5.30` provider).
- `Iverson.Server/deploy/terraform/bootstrap/azure/main.tf`, `resource "azurerm_storage_account" "state"`: add `allow_nested_items_to_be_public = false` (valid at the pinned `hashicorp/azurerm ~> 3.90` provider).

---

## 8. docker-compose secrets (F10)

File: `scripts/generate-compose-secrets.sh`

The script currently generates 7 *different* secrets (`IVERSON_LOADTEST_CLIENT_SECRET` and 6 siblings) — it does not yet cover the keys this finding targets. Extend it with 5 more `rand()`-generated lines (`AUTHENTIK_POSTGRESQL__PASSWORD` is excluded — see below, its compose site stays hardcoded):

```bash
NAMES=(
    IVERSON_LOADTEST_CLIENT_SECRET
    IVERSON_WEBTEST_CLIENT_SECRET
    IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET
    IVERSON_SMOKE_TEST_PASSWORD
    IVERSON_BYPASS_PASSWORD
    IVERSON_ADMIN_ORCHESTRATOR_PASSWORD
    IVERSON_ADMIN_ORCHESTRATOR_TOKEN
    POSTGRES_PASSWORD
    QDRANT__SERVICE__API_KEY
    AUTHENTIK_SECRET_KEY
    AUTHENTIK_BOOTSTRAP_PASSWORD
    AUTHENTIK_BOOTSTRAP_TOKEN
)

touch "$ENV_FILE"
for name in "${NAMES[@]}"; do
    if ! grep -q "^${name}=" "$ENV_FILE"; then
        echo "${name}=$(rand)" >> "$ENV_FILE"
    fi
done
```
This replaces the script's previous behavior of exiting early and refusing to touch an existing `.env`. That guard meant a developer who had already run the script before this change would never receive the new variables, and `docker compose up` would then hard-fail on `${VAR:?...}` for all of them. The append-if-missing form generates the whole file on a first run (exactly as before) and adds only what's missing on a later run, leaving every already-provisioned value — including the original 7, which Authentik has already used to provision itself — untouched.
Variable names on the `.env` side are kept identical to the compose YAML key names (double underscores preserved for `QDRANT__SERVICE__API_KEY`/`AUTHENTIK_POSTGRESQL__PASSWORD`), matching the existing convention exactly — e.g. `IVERSON_LOADTEST_CLIENT_SECRET` is the same string on both sides today, and the new ones follow suit. Convert 5 of these 6 occurrence-groups in `Iverson.Server/docker-compose.yml` (**9** total line occurrences: lines 35, 114, 334, 352, 359, 360, 395, 402, 403 — across `POSTGRES_PASSWORD` ×1, `QDRANT__SERVICE__API_KEY` ×1, `AUTHENTIK_SECRET_KEY` ×3, `AUTHENTIK_BOOTSTRAP_PASSWORD` ×2, `AUTHENTIK_BOOTSTRAP_TOKEN` ×2 — `AUTHENTIK_POSTGRESQL__PASSWORD`'s 3 occurrences at lines 339, 357, 400 are excluded, see below) to the `${VAR:?message}` required-with-no-default pattern already used for the other 7, e.g. `QDRANT__SERVICE__API_KEY: ${QDRANT__SERVICE__API_KEY:?run scripts/generate-compose-secrets.sh first}`.

**Three of these six secrets are also read as literal copies elsewhere, not just at the declaration sites above** — converting only the declarations desynchronizes the producer (now random) from the consumer (still the old literal), breaking authentication between services:
- `POSTGRES_PASSWORD` is also embedded in `Password=iverson` at `docker-compose.yml:456,554` (`ConnectionStrings__Postgres`) — rewrite both to `Password=${POSTGRES_PASSWORD:?...}`.
- `QDRANT__SERVICE__API_KEY` is also embedded as `Qdrant__ApiKey=dev-only-...` at `docker-compose.yml:478,565`, and as `QDRANT_API_KEY = "dev-only-..."` in `Iverson.LoadTest/scripts/ingest.py:155` — rewrite the two compose sites to `Qdrant__ApiKey=${QDRANT__SERVICE__API_KEY:?...}` and update `ingest.py:155` to read the same env var instead of a hardcoded literal.
- `AUTHENTIK_POSTGRESQL__PASSWORD` is additionally baked into `deploy/postgres/init-authentik-db.sql:1` (`CREATE USER authentik WITH PASSWORD 'authentik';`), a static file mounted into the Postgres init directory (`docker-compose.yml:41`). **Excluded from this task**: parameterizing it would mean replacing that static SQL file with an entrypoint script that interpolates the env var at container start — a materially bigger change than a compose-value swap. Leave `AUTHENTIK_POSTGRESQL__PASSWORD` hardcoded for now; revisit as a separate, dedicated task if this secret needs to move off a static default.

The remaining two secrets (`AUTHENTIK_BOOTSTRAP_PASSWORD`, `AUTHENTIK_BOOTSTRAP_TOKEN`) have only documentation copies (`docker-compose.yml:8`'s header comment, `Iverson.AdminUI/README.md:28`) — no process reads them, so the stack still starts correctly after conversion, but both will state a password that's no longer accurate. Update both as a follow-on doc-drift edit, not a functional requirement of this task.

---

## 9. Non-code notes (F3, F4's primary fix, F8, F11)

- **F4's primary fix (correct `docs/security/tma.md`'s stale F1):** already applied to the file on disk earlier this session, but never committed — `docs/security/tma.md` is entirely untracked (`git log --oneline -- docs/security/tma.md` returns nothing; `git status --porcelain docs/security/` shows `?? docs/security/`). The remaining task is `git add -f docs/security/tma.md && git commit`, not re-doing the edit.
- **F3 (Authentik `add_user` global scope):** no fix available — Authentik 2026.5.3 cannot object-scope this permission. Recommend alerting on `add_user` API calls against the orchestrator credential via Authentik's own audit log, as a manual ops task; not something this repo's code can implement.
- **F8 (`NVD_API_KEY`):** a repository administrator must register an NVD API key and add it as a secret in both GitHub and GitLab settings. No code change.
- **F11 (`/v1/traces` payload validation):** no action — the CSR report's own remediation states existing caps (content-type allowlist + 1 MiB size cap) are accepted as sufficient absent an observed abuse pattern.

---

## Verified assumptions

| Assumption | Evidence |
|---|---|
| `iverson_client/auth.py` is the only Python SDK file constructing the token-fetch request | `grep` for `token_endpoint`/`urlopen`/`urllib` across the SDK; `core.py` only imports `_CachedTokenProvider` and checks the endpoint's URL scheme, builds no request of its own |
| No existing Python SDK test exercises redirect-following behavior | `grep -rln "redirect" Iverson.Clients/Python/tests/` — zero matches |
| `_escape`'s import path for reuse in `planner.py` | `evaluate.py:12`: `from iverson_agent.retrieval import _escape` — same import `planner.py` will use |
| Current CI structure has no existing `dotnet build`/`npm test` job to collide with | `.github/workflows/` has exactly 3 files (`codeql.yml`, `dependency-scan.yml`, `deploy-validate.yml`), none build/test app code; `.gitlab-ci.yml` has 2 stages (`validate`, `dependency-scan`), neither fits |
| `dotnet test --filter "Category!=Integration"` and `npm ci && npm test` (TS SDK) are the correct, currently-working commands | Both run successfully earlier this session (2,671 .NET tests passed; 254 TS SDK tests passed) |
| Exact GitHub status-check context names for the existing dependency-scan and CodeQL jobs | Read both workflow files directly: 7 job ids in `dependency-scan.yml` (no per-job `name:` override, so context = job id), 6-language matrix in `codeql.yml` producing `Analyze (<language>)` contexts |
| The authenticated `gh` account can configure branch protection | `gh api repos/counsellorben/Iverson --jq '.permissions'` → `{"admin":true,...}` |
| `System.Threading.RateLimiting` (`SlidingWindowRateLimiter` et al.) is usable at net10.0 with no new NuGet package | Compiled and ran a real throwaway project (`Microsoft.NET.Sdk.Web`, net10.0) constructing `SlidingWindowRateLimiter` — succeeded |
| The `sub` claim is reliably present on the primary/service principal for every authenticated gRPC call | `Program.cs`: `MapInboundClaims = false` is set on the default JWT scheme specifically so `FindFirst("sub")` isn't remapped — documented in-code, same mechanism `ActingUserInterceptor` already depends on for the acting-user scheme |
| Registering an interceptor once via `AddGrpc(options => options.Interceptors.Add<T>())` covers all 4 gRPC services | `ActingUserInterceptor` is registered exactly this way and is confirmed (by this session's CSR work) to protect all relevant methods across the registered services |
| No global exception handler would intercept/reshape a thrown `RpcException` | `grep` for `UseExceptionHandler`/`IExceptionHandler`/`catch (Exception` across `Program.cs` — the one `catch` found is scoped to a startup-time embedding-init call, unrelated to request handling |
| `/v1/traces`'s route registration supports `.RequireRateLimiting(...)` | `Program.cs:547`: `app.MapPost("/v1/traces", ...).RequireAuthorization()` — a standard `RouteHandlerBuilder`, chainable with `.RequireRateLimiting` |
| `Iverson.LoadTest`'s traffic pattern relative to a per-principal rate limit | `Program.cs`: `--concurrency` defaults to 16 parallel workers, one `IversonClientCredentials` (one shared `client_id`/`client_secret`) minted once for the whole run, no `Delay`/`Sleep`/throttle found anywhere in the worker loop — confirmed this would trip a naively-sized limit (e.g. 300/min), forcing the revised 50,000/min threshold |
| `RejectForbiddenCharacters` is the single shared choke point for every raw-expression entry point | Read all 4 call sites directly: `StarRocksPipelineBuilder.cs:220,389`, `StarRocksQueryBuilder.cs:250,528` — all four call the same function |
| No existing StarRocks test fixture uses an expression near 1000 characters | Longest `Expression = "..."` literal found in `StarRocksQueryBuilderTests.cs`: ~40 characters |
| `/admin/reconcile/{typeName}`'s exact current code shape | Read `Program.cs:400-412` directly — matches the design's expected structure for adding the acting-user check |
| `render_schema`'s output has no consumer expecting raw/unescaped text | `grep -rn "render_schema"` — only caller is `session.py:137`, feeding directly into `plan(...)` |
| GCS/Azure state-backend resource names and provider versions | `Iverson.Server/deploy/terraform/bootstrap/{gcp,azure}/main.tf` — `google_storage_bucket.state` (provider `~> 5.30`), `azurerm_storage_account.state` (provider `~> 3.90`); both target arguments are long-stable in these provider lines |
| `scripts/generate-compose-secrets.sh` already covers the 6 secrets F10 targets | Read the script directly — it generates 7 *different* secrets (`IVERSON_*` client/test credentials), none of which overlap with `POSTGRES_PASSWORD`/`AUTHENTIK_*`/`QDRANT__SERVICE__API_KEY`; the script needs extending, not just reusing |
| Exact current line numbers/format of all 6 docker-compose secret keys (12 occurrences) | `grep -n` against `docker-compose.yml` for each key name — confirmed exact lines: 35, 114, 334, 339, 352, 357, 359, 360, 395, 400, 402, 403 |
| The 6 docker-compose secrets have no second consumer reading the same literal value elsewhere in the repo | `grep -rn` for each of the 6 literal values across the whole main checkout — 3 have real second consumers (`POSTGRES_PASSWORD`: `docker-compose.yml:456,554`; `QDRANT__SERVICE__API_KEY`: `:478,565` and `Iverson.LoadTest/scripts/ingest.py:155`; `AUTHENTIK_POSTGRESQL__PASSWORD`: `deploy/postgres/init-authentik-db.sql:1`), 3 do not (documentation copies only) |
| The complete population of test classes that start real containers, not just the ones with an obvious name | Full sweep: `grep -rn "IClassFixture<\|ICollectionFixture<"` across `Iverson.Server`'s test tree (34 hits), cross-referenced against which fixture classes are `IAsyncLifetime` (real containers) vs. `WebApplicationFactory<Program>`-based (in-memory only, 9 files confirmed safe) vs. static-method-only usage (2 files confirmed safe) — 13 classes confirmed to start real containers with no existing trait |

## Known issues / accepted as out of scope

- **Rate-limit threshold is an analytical estimate, not a live measurement** (per your choice) — no live stack was available this session to run a real `Iverson.LoadTest` pass and measure actual throughput. 50,000 req/min is sized with generous headroom above the ~15,000-30,000 req/min analytical ceiling, but should be revisited if a real measurement becomes available later.
- **F3, F8, F11 have no code fix** — documented in §9 as ops/upstream/accepted items, not implementation tasks.

---

Spec written and committed to `docs/specs/2026-09-15-csr-round7-remediation-design.md`. Assumptions were verified against the codebase (see the "Verified assumptions" section). Please review and let me know if you want any changes. When approved, the typical next step is `critical-design-review` against this spec.
