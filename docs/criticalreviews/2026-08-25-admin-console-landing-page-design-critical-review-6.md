# Critical Design Review: 2026-08-25-admin-console-landing-page-design (Round 6)

**Spec:** `/home/ben/repositories/Iverson/docs/specs/2026-08-25-admin-console-landing-page-design.md`
**Verified Assumptions section:** present

## 0. Coverage enumeration

Enumeration built before consulting rounds 1-5. The rewritten Transport section is the round's new
surface. Following round 5's result — where two defects fell only once the live artifact was
dumped — this round dumped the `/health` body, the Qdrant collection-info key set, and read the
proto messages behind every widget that claims a specific field, rather than reasoning about them.

### Sections

| Row | Disposition |
|---|---|
| Problem | `ok` — `router.tsx` index redirect, four `Coming soon` pages, metric inventory re-resolved. |
| Scope — in-scope | `ok` — four bullets, each resolving to a Design section; "browser-reachable route to the API's HTTP/1.1 port" now matches Design 1's Transport. |
| Scope — out-of-scope | `ok` — the `/health` write exception matches `Program.cs:310-311`. |
| Design 1 — service placement | `ok` — `admin_console.proto` alongside the six existing protos; two admin services already precedent it. |
| Design 1 — gRPC-not-REST rationale | `ok` — re-verified the collision in both directions against `charts/admin-ui/templates/ingress.yaml:21`. |
| Design 1 — Transport, opening paragraph | `ok` — `appsettings.json:12-13` / `:16-17` unchanged; the 400-vs-200 claim re-confirmed against the live ports. |
| Design 1 — Transport, `/admin-api` prefix and the not-`/admin` argument | `ok` — the `(/|$)` guard genuinely excludes `/admin-api`: after a literal `^/admin`, position 6 is `-`, which matches neither `/` nor end-of-string, and `^/admin` cannot match elsewhere. No precedence contest with the console's rule. |
| Design 1 — Transport, new-Ingress bullet | `ok` — the `/admin-api(/|$)(.*)` + `rewrite-target: /$2` pair does produce `/iverson.<Service>/<Method>` and `/health` at the backend; it is the same shape `charts/admin-ui/templates/ingress.yaml` uses, and that Ingress sets no `use-regex` annotation either. Annotation-scoping reason re-checked at `charts/api/templates/ingress.yaml:4-6` and `values-aws.yaml:75`. |
| Design 1 — Transport, `group.order` paragraph | `ok` — `grep -rn "group.name\|group.order"` still returns no Ingress annotations in the chart, so the explicit-ordering instruction is necessary and not redundant. |
| Design 1 — Transport, vite bullet | `ok` — vite `server.proxy` supports a rewrite; the prefix is a plain string key there. |
| Design 1 — Transport, relative-base paragraph | `ok` — `apiBaseUrl` still read only at `config.ts:16`; the grpc-web URL composition (`host` + `/<package>.<Service>/<Method>`) matches how ts-proto's `grpc-web` output builds requests. |
| Design 1 — `GetMetrics`, mangling rule | `ok` — the corrected rule matches the live endpoint: the two duration series really are `http_{server,client}_request_duration_seconds_*`, and the `_total`-on-counters half is corroborated by `aspnetcore_authorization_attempts_total` on the same endpoint. |
| Design 1 — `GetMetrics`, other bullets | `ok` — config key, named `HttpClient`, absent-Prometheus handling, and both NetworkPolicy additions (`networkpolicies.yaml:7-10`, `:38-63`, `:474`, `:487-490`). |
| Design 1 — `GetQdrantStats` | `→ §2.1` — the capability claims hold; **the field list does not.** |
| Design 1 — Authorization | `ok` — `.RequireAuthorization("Operator").EnableGrpcWeb()` matches `Program.cs:444`'s shape. |
| Design 2 — Band A table | `ok` — **each claim checked against the message it comes from.** `/health`'s live body is `{status, checks{postgres,starrocks,qdrant,kafka}}`; `Tenant` carries `tenant_id`, `display_name`, **`status`** so "list with state" is real; `SchemaType` carries `fields` and `relations`, and `SchemaField`/`SchemaRelation` are populated messages, so "field counts, relation edges" is real. |
| Design 2 — Band B table | `ok` — four widgets, each rendering a scalar the corrected contract supplies. |
| Design 2 — Band C table | `→ §2.1` — same field list as Design 1. |
| Design 2 — health-strip constraints | `ok` — live body confirms the four-key `checks` object and `starrocks: true` on the enabled branch; the `"disabled"` branch is the other arm of `Program.cs:321`. |
| Design 2 — data-volume constraints | `ok` — `:490-494`, `:514`, `:501` re-read. |
| Design 2 — RPC-health constraints | `ok` — `http_route` confirmed live (A36); the corrected metric names now match. |
| Design 2 — embedding-latency constraint | `ok` — two named clients at `ServiceCollectionExtensions.cs:12,29`. |
| Design 3 — cadence table | `ok` — 60s health strip; no sparkline row remains. |
| Design 3 — failure/degradation, implementation shape, testing | `ok` — isolation, stale, backoff, hidden-tab, 401; hook transport-agnostic; vitest 3.2 and three test files. |
| Design 4a / 4b / 4c / 4d | `ok` — `Program.cs:438-445` still lacks `.EnableGrpcWeb()` on the two named services; `.github/workflows/` still only two files; `docker-compose.yml:438` worker service present; blueprint glob and `Sidebar.tsx:20,25` unchanged. |
| Verified assumptions (A1-A36) | See §1. |
| Known issues | `ok` — three entries, none re-raised. |

### Rules and operands

| Row | Disposition |
|---|---|
| Path rule: `/admin-api(/|$)(.*)` vs the console's `/admin(/|$)(.*)`, both directions | `ok` — over-inclusion: `/admin-api/...` cannot match the console rule (guard checked character by character). Under-inclusion: `/admin-api/x` and `/admin-api` both match the new rule, so nothing the console sends is missed. |
| Path rule: `/admin-api(/|$)(.*)` vs **native gRPC** traffic, both directions | `ok` — SDK clients send bare `/iverson.<Service>/<Method>`, which does not begin with `/admin-api`, so they are not captured. This is the operand round 4 found broken under the previous arrangement; the prefix genuinely separates the two consumers. |
| Path rule: `/admin-api(/|$)(.*)` vs the api Ingress's `/` catch-all | `ok` — on nginx a matching regex location wins over the longest prefix location; on ALB the spec now mandates explicit `group.order`. Both controllers covered. |
| Rewrite rule: `rewrite-target: /$2` over real request paths | `ok` — traced two real shapes: `/admin-api/iverson.ObjectSearchService/Search` → `$2 = iverson.ObjectSearchService/Search` → `/iverson.ObjectSearchService/Search`; `/admin-api/health` → `/health`. Both land on paths the API actually serves. |
| Field rule: what `GetQdrantStats` claims to return vs what the store holds | `→ §2.1` — **checked against a real collection's key set, in both directions.** |
| Name-mangling rule | `ok` — corrected version matches the live endpoint for both the unit-bearing and unit-less instruments. |
| Eligibility predicate: which collections `GetQdrantStats` enumerates | `ok` — live listing returns five: `benchmark_documents_chunks_tenant_bypass`, `benchmark_documents_tenant_bypass`, `iverson-probe`, `vector_docs_chunks_tenant_bypass`, `vector_docs_tenant_bypass`. Per-tenant naming confirms the spec's cross-tenant characterisation. |
| Eligibility predicate: which processes emit Band B metrics | `ok` — the six hosted services are inside `if (workloadRole == "worker")` (`Program.cs:254-264`); the API's live `/metrics` carries none of their series. |

### Data-flow arrows

| Row | Disposition |
|---|---|
| browser → `/admin-api/iverson.<Service>/<Method>` → Ingress rewrite → api:8081 | `ok` — every hop traced: prefix match, `$2` capture, backend path, and `Protocols: Http1` serving grpc-web via `UseGrpcWeb()` (`Program.cs:284`). |
| SDK client → `/iverson.<Service>/<Method>` → api Ingress → api:8080 | `ok` — untouched by the new rule; the arrow round 4 found broken now survives. |
| browser → `/admin-api/health` → rewrite → `/health` on 8081 | `ok` — live body confirms the consuming operation's parameters (`checks.postgres` etc.) exist in what it receives. |
| Qdrant `GetCollectionInfoAsync` → `GetQdrantStats` response → Band C render | `→ §2.1` — **the render requires a parameter that does not exist in the artifact the stage reads.** |
| `ListTenants` → tenant-roster render | `ok` — `Tenant.status` exists, so "state" is sourced. |
| `GetSchema` → schema-catalog render | `ok` — `SchemaType.fields` and `.relations` exist and are populated messages. |
| Prometheus series → `GetMetrics` → Band B renders | `ok` — corrected names match live series; every Band B render is a scalar the contract supplies. |
| api pod → Prometheus `:9090` | `ok` — both policy additions stated. |

### Candidates generated and dropped

| Candidate | Why dropped |
|---|---|
| `/admin-api(/|$)(.*)` is a catch-all: it also exposes `/metrics`, `/probe/sql`, `/probe/vector`, `/probe/kafka` (all `AllowAnonymous`) and `/admin/dlq`, `/reconcile` on port 8081, which previously had no ingress route at all | Real change in exposed surface, but the asked-for behavior does not fail without addressing it — the widgets work either way. This is a security-posture question and belongs to `critical-security-review`, which has not been run on this spec. |
| "The route serves all five gRPC widgets" — eight of the nine widgets are gRPC-backed, not five | Arithmetic slip in prose. The route is one rule regardless of the count, so nothing is built wrongly from it. Second instance of this shape after round 5's "seven numbers"; both are stale counts left behind as content moved, and neither changes an implementable decision. |
| `Program.cs:284` calls `UseGrpcWeb()` after `UseAuthentication`/`UseAuthorization`, whereas the ASP.NET Core example orders it before | Pre-existing pipeline ordering that this design does not introduce, and middleware ordering is `critical-implementation-review`'s surface. |
| The new Ingress's TLS/listener annotations (`certificate-arn`, `listen-ports`, `ssl-redirect`) are unspecified for the AWS group | Configuration detail an implementer resolves from the sibling Ingress; the design's outcome does not depend on which of them is restated. |
| grpc-web may require an absolute `host`, making "relative same-origin base" imprecise | If so the base becomes `window.location.origin + '/admin-api'`, still same-origin and still the same route. No prescription changes. |

## 1. Verified-assumptions cross-check

A1-A36 reconfirmed under a fresh read; citations resolve to the lines they name. A36 is new since
round 5 and was verified independently against the live `/metrics` endpoint rather than taken from
the update.

**Span check — dependencies introduced by the rewritten Transport section, plus one the widget
tables have always rested on:**

1. *"`/admin-api` cannot match the console's own ingress rule."* Load-bearing for the whole prefix
   choice; no listed assumption covers it. **Verified in-round and holds** — the `(/|$)` guard in
   `charts/admin-ui/templates/ingress.yaml:21` requires `/` or end-of-string after `admin`, and
   `/admin-api` supplies `-`. Closes clean.
2. *"A rewrite can strip the prefix so the backend sees the real gRPC path."* No assumption covers
   the rewrite mechanics. **Verified in-round and holds** — traced `$2` capture over two real
   request shapes; the pattern is already in service in this chart for `/admin`. Closes clean.
3. *"`GetCollectionInfoAsync` returns the three counts the Band C widget displays."* A5 verifies the
   method **exists** and is already called; nothing covers what it returns. This is exactly the
   gap the span check exists for — an assumption verified as scoped, with the design depending on
   something one step beyond it. **Verified in-round and FAILS** — see §2.1.

## 2. Literal-wrongness findings

### 2.1 — `vectors count` is not a field Qdrant returns; the Band C widget promises a number that does not exist

**Description.** Design 1's `GetQdrantStats` says it "Returns points count, vectors count, and
indexed-vectors count per collection", and Design 2's Band C table renders "Points, vectors,
indexed vectors per collection". Qdrant's `CollectionInfo` carries no `vectors_count`; it was
deprecated and removed. One of the three numbers the widget displays cannot be sourced.

**Evidence.** Read from the running Qdrant rather than derived — full top-level key set for two
real collections on the deployment's own instance (server version **1.18.2**, matching the
`Qdrant.Client` 1.18.1 package at `Iverson.Vector/Iverson.Vector.csproj:18`):

- `benchmark_documents_tenant_bypass`: `config`, `indexed_vectors_count`, `optimizer_status`,
  `payload_schema`, `points_count`, `segments_count`, `status`, `update_queue`.
  `vectors_count` **absent**.
- `vector_docs_tenant_bypass`: identical key set. `vectors_count` **absent**.

The counts that do exist are `points_count`, `indexed_vectors_count` and `segments_count`. Two of
the widget's three numbers are real; the middle one is not. This is not a zero-value case — the
key is not present at all, on collections holding 32 and 67 points respectively.

**Proposed fix.** Replace the field list in both places with what the store actually returns.
The smallest correction is to drop the missing one, leaving "points count and indexed-vectors
count per collection" in Design 1 and "Points, indexed vectors per collection" in Design 2. If a
third number is wanted to keep the tile's shape, `segments_count` is available on the same
response; `status` (`green` on both live collections) is also there and is the one field that
reports health rather than volume.

## 3. Forced decisions

No forced decisions found.

Round 5's §3.1 is resolved: Design 1 now states the `/admin-api` prefix, why it is not `/admin`,
and how the ingress separates the two consumers. This round's §0 traced that arrangement hop by
hop and both the browser and SDK arrows survive it, so nothing about the transport remains
unpicked.

## 4. Previously addressed

- **Round 5 §3.1** — the transport axis is decided and written as a rewritten `/admin-api` prefix,
  with the not-`/admin` argument stated. Both consumer arrows verified this round.
- **Round 5 §2.1** — the mangling rule now includes the unit segment and names the two duration
  series literally; both match the live endpoint.
- **Round 5 §2.2** — the fan-out backlog widget renders current values only; every Band B render
  is now a scalar the contract supplies.
- **Round 4 §2.1 / §2.2** — the path-based split that broke native gRPC is gone, replaced by the
  prefix; the `group.order` requirement is now stated explicitly in the Ingress bullet.
- **Rounds 1-3** — NetworkPolicy additions, `http_route` exclusion, read-only scope exception,
  removed CI clause, and the separate-Ingress annotation reasoning all still read correctly on a
  fresh check.

## 5. Recommendation

⚠️ **Approve with literal-wrongness fixes** — §2 non-empty, §3 empty.

§3 is empty for the second time in six rounds and, unlike round 3, nothing in §2 is conditional on
an open decision: §2.1 is a two-line content correction with no downstream consequences. The
transport arrangement that produced a finding in four consecutive rounds was traced hop by hop
this round and holds in both directions.

§2.1 came from the same technique that produced round 5's two findings: dumping the artifact
instead of reasoning about its shape. A5 has said since round 1 that `GetCollectionInfoAsync`
exists and is already called — true, and it kept the question of what it *returns* out of view for
five rounds.

Five candidates were generated and dropped with reasons recorded in §0. One of them is worth the
user's attention as a routing decision rather than a defect: the `/admin-api` catch-all newly
exposes port 8081's anonymous endpoints through the ingress, which is `critical-security-review`'s
surface and that skill has not been run against this spec.
