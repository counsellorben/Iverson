# CSR Round 7 Remediation — Design

Source: `docs/criticalreviews/2026-09-15-iverson-critical-security-review-7.md` (all 11 findings). Each finding is independently small and touches a disjoint set of files, so this design covers all 11 as one spec rather than eleven separate cycles — matching this session's established precedent for prior CSR remediation designs.

---

## 1. Python SDK OAuth2 token-fetch redirect guard (F1)

File: `Iverson.Clients/Python/iverson_client/auth.py`

`_CachedTokenProvider.get_token` is the only place in the Python SDK that constructs the token-fetch request (`iverson_client/core.py` only imports the class and checks the endpoint's URL scheme — it never builds its own request). It currently calls `urllib.request.urlopen(request)` using Python's default global opener, which follows 307/308 redirects and resends the original POST body (including `client_secret`) to whatever `Location` a compromised or malicious token endpoint names — the same vulnerability class already fixed for the DotNet/Java/Go/TypeScript SDKs.

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

**Branch protection on `main`:** require these 15 status-check contexts before merge (enumerated by reading the actual job structure, not assumed), no required review (solo-maintained repo):

- From `dependency-scan.yml` (7 jobs, each its own check context — no per-job `name:` override, so the context is the job id): `nuget-audit`, `npm-audit-adminui`, `npm-audit-ts-sdk`, `govulncheck`, `owasp-dependency-check-java`, `pip-audit-python-sdk`, `pip-audit-agents`
- From `codeql.yml` (one job, matrixed across 6 languages, each its own context named `Analyze (<language>)`): `Analyze (actions)`, `Analyze (csharp)`, `Analyze (go)`, `Analyze (java-kotlin)`, `Analyze (javascript-typescript)`, `Analyze (python)`
- The 2 new jobs this design adds (below)

Configured via `gh api repos/counsellorben/Iverson/branches/main/protection` (confirmed the authenticated account has `admin: true` on this repo).

**New CI job — .NET build/test:** a new workflow `.github/workflows/dotnet-build.yml` (kept independent of `dependency-scan.yml`'s manifest-only scope) running `dotnet build Iverson.slnx` then `dotnet test --filter "Category!=Integration"` (the same filter already confirmed working earlier this session). Mirrored as a new job in `.gitlab-ci.yml` under a new `build-test` stage (the existing `stages:` list only has `validate`/`dependency-scan` — neither fits an application build/test job semantically).

**New CI job — TypeScript SDK build/test:** `npm ci && npm test` in `Iverson.Clients/TypeScript` (typecheck + vitest, confirmed working after this session's earlier vitest/vite pin fix). Same placement pattern: new GitHub workflow job, new GitLab `build-test`-stage job.

---

## 3. Rate limiting (F7)

Two mechanisms, matched to each transport per this codebase's own existing convention (verified, not assumed — `ActingUserInterceptor` is the established precedent for exactly this kind of cross-cutting gRPC concern):

**gRPC entity CRUD/search RPCs** (`ObjectMapping`, `ObjectPersistence`, `ObjectRetrieval`, `ObjectSearch`): a new `RateLimitInterceptor : Interceptor`, registered once at `AddGrpc(options => options.Interceptors.Add<RateLimitInterceptor>())` alongside the existing `ActingUserInterceptor` — confirmed this registration style applies globally to all 4 services, not per-service. Uses `System.Threading.RateLimiting.PartitionedRateLimiter` (confirmed available at net10.0 via a real compile-and-run probe against `Microsoft.NET.Sdk.Web`, no new NuGet package needed), partitioned by the `sub` claim off `context.GetHttpContext().User` (confirmed reliably present — `MapInboundClaims = false` is already set on the default JWT scheme specifically so `FindFirst("sub")` resolves correctly, per the existing in-code comment documenting this exact mechanism). Rejected calls throw `RpcException(new Status(StatusCode.ResourceExhausted, ...))` — the same shape `ActingUserInterceptor` already uses successfully (confirmed no global exception handler exists anywhere in `Program.cs` that would intercept or reshape it first).

Limit: **50,000 requests/minute per principal**, sliding window. Sized generously above `Iverson.LoadTest`'s realistic ceiling — LoadTest defaults to 16 concurrent workers sharing **one** service credential with no self-throttling anywhere in its worker loop (confirmed by reading `Iverson.LoadTest/Program.cs`), and no live stack was available this session to measure its actual throughput, so this is an analytical estimate (roughly 15,000-30,000 req/min at a realistic 20-50ms per-call round-trip) with generous headroom above it, not a tuned number. Still bounds a truly pathological runaway to a real ceiling instead of unlimited.

**`/v1/traces`**: ASP.NET Core's built-in `AddRateLimiter`/`.RequireRateLimiting("traces")` middleware (confirmed the route's `app.MapPost("/v1/traces", ...)` builder supports this extension). Limit: **60 requests/minute per principal** — this endpoint is not a hot path for any legitimate caller (LoadTest doesn't call it; it's a browser/SDK trace-export relay).

---

## 4. DLQ `/admin/reconcile/{typeName}` hardening (F4, optional)

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

The script currently generates 7 *different* secrets (`IVERSON_LOADTEST_CLIENT_SECRET` and 6 siblings) — it does not yet cover the 6 keys this finding targets. Extend it with 6 more `rand()`-generated lines:

```bash
cat > "$ENV_FILE" <<EOF
IVERSON_LOADTEST_CLIENT_SECRET=$(rand)
IVERSON_WEBTEST_CLIENT_SECRET=$(rand)
IVERSON_ADMIN_AUTOMATION_CLIENT_SECRET=$(rand)
IVERSON_SMOKE_TEST_PASSWORD=$(rand)
IVERSON_BYPASS_PASSWORD=$(rand)
IVERSON_ADMIN_ORCHESTRATOR_PASSWORD=$(rand)
IVERSON_ADMIN_ORCHESTRATOR_TOKEN=$(rand)
POSTGRES_PASSWORD=$(rand)
QDRANT__SERVICE__API_KEY=$(rand)
AUTHENTIK_SECRET_KEY=$(rand)
AUTHENTIK_POSTGRESQL__PASSWORD=$(rand)
AUTHENTIK_BOOTSTRAP_PASSWORD=$(rand)
AUTHENTIK_BOOTSTRAP_TOKEN=$(rand)
EOF
```
Variable names on the `.env` side are kept identical to the compose YAML key names (double underscores preserved for `QDRANT__SERVICE__API_KEY`/`AUTHENTIK_POSTGRESQL__PASSWORD`), matching the existing convention exactly — e.g. `IVERSON_LOADTEST_CLIENT_SECRET` is the same string on both sides today, and the new ones follow suit. Convert all 6 occurrence-groups in `Iverson.Server/docker-compose.yml` (11 total line occurrences across `POSTGRES_PASSWORD` ×1, `QDRANT__SERVICE__API_KEY` ×1, `AUTHENTIK_SECRET_KEY` ×3, `AUTHENTIK_POSTGRESQL__PASSWORD` ×3, `AUTHENTIK_BOOTSTRAP_PASSWORD` ×2, `AUTHENTIK_BOOTSTRAP_TOKEN` ×2) to the `${VAR:?message}` required-with-no-default pattern already used for the other 7, e.g. `QDRANT__SERVICE__API_KEY: ${QDRANT__SERVICE__API_KEY:?run scripts/generate-compose-secrets.sh first}`.

---

## 9. Non-code notes (F3, F8, F11)

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
| Exact current line numbers/format of all 6 docker-compose secret keys (11 occurrences) | `grep -n` against `docker-compose.yml` for each key name — confirmed exact lines |

## Known issues / accepted as out of scope

- **Rate-limit threshold is an analytical estimate, not a live measurement** (per your choice) — no live stack was available this session to run a real `Iverson.LoadTest` pass and measure actual throughput. 50,000 req/min is sized with generous headroom above the ~15,000-30,000 req/min analytical ceiling, but should be revisited if a real measurement becomes available later.
- **F3, F8, F11 have no code fix** — documented in §9 as ops/upstream/accepted items, not implementation tasks.

---

Spec written and committed to `docs/specs/2026-09-15-csr-round7-remediation-design.md`. Assumptions were verified against the codebase (see the "Verified assumptions" section). Please review and let me know if you want any changes. When approved, the typical next step is `critical-design-review` against this spec.
