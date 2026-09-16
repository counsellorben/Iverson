# Iverson — Threat Model Analysis (whole repository)

*Last refreshed: 2026-09-15 against commit SHA `72c5c4c6df27565dfcbced54f9d714df25efe589`*

**Sources**
- Code: `/home/ben/repositories/Iverson` (this repository, whole-repo scope; monorepo covering `Iverson.Server`, `Iverson.Clients` (5 SDKs), `Iverson.Agents/Python`, `Iverson.AdminUI`, and `Iverson.Server/deploy` (Helm/Terraform/kind/compose))

**References**
- Threat categorization and severity follow this skill's built-in taxonomy and rubric (Appendices A–B of the `peters-toolkit:tma` skill).
- Superseded prior version: whole-repo TMA dated 2026-09-13 (commit `5b29301`). 185 commits landed between that baseline and this refresh, including two full peters-toolkit remediation cycles (CSR round-5 remediation, "outstanding fixes") and roughly 70 automated Dependabot dependency bumps across all six ecosystems. Two of that prior version's findings (F2 — enrichment prompt injection, F3 — no CI dependency scanning) are now closed; this refresh replaces them with a narrower residual and a new finding discovered live during this session (see §5.11, §6).

---

## 2. System overview

Iverson is a multi-tenant .NET 10 gRPC platform for storing, searching, and retrieving caller-defined entity schemas. Tenant data is fanned out asynchronously (via Kafka) into three specialized stores — PostgreSQL (row-level security, source of truth), StarRocks (analytical query/aggregation), and Qdrant (vector/semantic search) — each independently enforcing tenant isolation, with identity provided by a self-hosted Authentik OIDC instance and five language SDKs (DotNet, Go, Java, Python, TypeScript) as the client surface. It matters because it is a content-agnostic multi-tenant data platform: callers define their own schemas and payload contents, so the platform has no visibility into whether stored data is PII, PHI, payment data, or something else — every isolation and authorization guarantee has to hold regardless of what tenants choose to store.

## 3. Architecture & data flows with trust boundaries

### 3.1 Components

| Component | Role |
|---|---|
| `Iverson.Api` (api + worker roles, one image) | gRPC entity CRUD/search services, HTTP admin/health endpoints, Kafka consumers (worker role only) |
| `Iverson.Sql` | PostgreSQL access — row-level security, per-role connection switching |
| `Iverson.StarRocks` | Per-tenant-database analytical store — query DSL → SQL translation |
| `Iverson.Vector` | Qdrant vector store — per-tenant collections, scoped short-lived JWTs |
| `Iverson.Embeddings` | HTTP clients to TEI (embeddings) and Ollama (generative enrichment) |
| `Iverson.Events` | Kafka event contracts and dispatch/retry/DLQ machinery |
| Authentik (external, self-hosted) | OIDC identity provider; two JWT audiences (service, acting-user) |
| `Iverson.AdminUI` | React SPA, OIDC authorization-code login, gRPC-web to the API |
| `Iverson.Clients/*` | 5 language SDKs; client-credentials + optional acting-user token |
| `Iverson.Agents/Python` | Anthropic-backed agent layer built on the Python SDK |
| `deploy/{helm,terraform,kind}` | Kubernetes (3 clouds + local kind), Terraform IaC, Docker Compose for local dev |

### 3.2 Data flow — entity write

Client (SDK) → gRPC `Post`/`Update` (service JWT + optional acting-user JWT) → `ActingUserInterceptor` (tenant-suspension check) → row/field authorization evaluator (fail-closed) → tenant/owner force-set + field allowlist → payload-size validation → PostgreSQL INSERT/UPDATE under the non-superuser `iverson_runtime` role with `app.tenant_id` set (RLS-enforced) → transactional outbox row → Kafka `iverson.entity.events`.

Worker-role fan-out from Kafka: StarRocks (per-tenant DB), Qdrant (per-tenant collection, `+_chunks`), enrichment → Ollama (prompt injection now defended — see §3.5 TB-9 and §5.7), embeddings → TEI, document template re-render. Every projection consumer re-derives tenant authority from the authoritative Postgres row rather than trusting the Kafka event's own claimed tenant (`ProjectionTenantResolution.cs`, generalized across all projection consumers).

### 3.3 Data flow — entity read

Client → gRPC `Get`/`GetMany`/`Search*`/`Aggregate`/`GroupBy`/`Pipeline` → evaluator decision → store read scoped by `(TenantColumn, TenantValue)` in all three stores independently (Postgres RLS, StarRocks per-tenant DB + role, Qdrant per-tenant collection + scoped JWT) → relation hydration (re-evaluated per related type, cycle-safe, depth-capped) → field masking + tenant-column stripping on the response.

### 3.4 Data flow — identity

Browser → Authentik authorization-code flow (no refresh token issued) → id/access token in `sessionStorage` → admin-ui gRPC-web → API `TenantAdmin`/`TenantLifecycle` RPCs, using the *primary* principal's own `tenant_id`/group claims (no acting-user token involved on these two services) → `IdpAdminClient` → Authentik REST, authenticated with a long-lived orchestrator admin Bearer token.

Service-to-service: SDK client-credentials → Authentik token endpoint → `authorization: Bearer <service token>` + optional `x-acting-user-authorization: Bearer <user token>` on the same RPC call.

### 3.5 Trust boundaries

| ID | Boundary | Crossing point | Existing controls |
|---|---|---|---|
| TB-1 | Internet → Ingress | ingress-nginx / ALB / AGIC / GCE | TLS terminated at Ingress where `tlsSecretName` is set (azure/gcp); AWS relies on an externally-managed ACM/ALB listener; local/laptop are plaintext by design |
| TB-2 | Ingress → API pod | plaintext h2c `:8080` | NetworkPolicy restricts `:8080` to the `ingress-nginx` namespace; `:8081` (health) is open for kubelet |
| TB-3 | Unauthenticated → authenticated | ASP.NET `FallbackPolicy` | `RequireAuthenticatedUser()` default; explicit anonymous carve-outs for `/metrics`, `/health`, `/health/live`, `/build`, dev-only `/openapi` |
| TB-4 | Service identity → end-user identity | `x-acting-user-authorization` header | Separate JWT scheme/audience; **absence is not an error** — yields `actingUser == null`, which the row/field evaluator treats as full deny. `GetSchema`/`TenantAdmin`/`/admin/reconcile/{typeName}` operate on the primary principal only (deliberate); `/admin/dlq*` additionally **require** a genuine acting-user token (`Program.cs:414-465`) — corrected this refresh, see §5.4 |
| TB-5 | Tenant ↔ tenant | every entity read/write | Enforced independently in Postgres (RLS + `FORCE` + non-superuser role), StarRocks (per-tenant DB + `SET ROLE`), Qdrant (per-tenant collision-resistant collection name), and an API-layer tenant predicate/comparison; `iverson_maintenance`/`EntityAccess.CrossTenantMaintenance` are the sole, explicit, code-reviewed exemptions |
| TB-6 | Caller query DSL → generated SQL | StarRocks query/pipeline builders | Values parameterized; identifiers resolved through a schema allowlist that excludes the tenant column; raw `expression` fragments gated by a character denylist + token allowlist, not parameterization; `StarRocksParameterGuard` additionally throws if generated SQL contains any unbound placeholder, converting StarRocks' own silent-wrong-results behavior into a loud failure |
| TB-7 | Schema registration → DDL | `RegisterSchema` | `SchemaAdmin` policy + a tight `^[A-Za-z][A-Za-z0-9]*$` identifier regex is the only gate before interpolated `CREATE TABLE`/`ALTER TABLE`/`CREATE POLICY`/`GRANT` in two databases; all identifiers additionally quoted at the SQL-generation site |
| TB-8 | API → Authentik | plaintext HTTP in-cluster | Long-lived, now object-scoped-where-possible orchestrator admin token; compensated by default-deny NetworkPolicy |
| TB-9 | Platform → LLM/embedding backends | plaintext, unauthenticated HTTP | Tenant content leaves the tenant-isolation boundary into a shared model server. **Changed since last refresh**: every enrichment prompt template now wraps untrusted source text in `<<<BEGIN_SOURCE_TEXT>>>...<<<END_SOURCE_TEXT>>>` markers with an explicit "not instructions" framing, and escapes literal marker sequences in the source text itself (`EnrichmentPrompts.cs`, commits `fddd073`/`afe9e97`) — see §5.7 |
| TB-10 | Kafka topic | `iverson.entity.events`/`.dlq` | Full plaintext entity payloads across all tenants on one topic; workers re-derive authority from Postgres rather than trusting the event |
| TB-11 | Browser → admin-ui | nginx origin | Strong CSP (`default-src 'self'`, `base-uri`/`form-action`/`object-src` locked down), `X-Frame-Options`, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, HSTS when external scheme is https |
| TB-12 | CI/CD → `main` | GitHub Actions / GitLab CI, PR merge | **Changed since last refresh, only partially for the better**: `contents: read` default, SHA-pinned actions, checksummed tool downloads, Terraform `-lockfile=readonly`, and (new) a dependency-vulnerability scan gate now runs on every PR across all 6 ecosystems. But `main` carries **zero GitHub branch protection** (`gh api repos/.../branches/main/protection` → 404 "Branch not protected") — no required status checks, no required reviews, no push restrictions — so none of those CI signals actually block a merge; see §5.11, F2 |
| TB-13 | Cloud control plane | EKS public endpoint | CIDR-restricted via a required Terraform variable with no default (fails closed if unset) |

---

## 4. Threat actors

| Actor | Capabilities | Motivation | In scope |
|---|---|---|---|
| External opportunistic attacker | Reaches only what's routed through Ingress: 6 gRPC service prefixes, `/v1/traces`, admin-ui | Credential stuffing against Authentik login, generic scanning/exploitation | Yes |
| Malicious or compromised tenant user (legitimate acting-user token) | Everything a normal tenant user can do: full CRUD/search within their own tenant, arbitrary entity content | Cross-tenant escalation attempts, prompt injection into the enrichment pipeline, resource exhaustion via the query DSL | Yes — the most realistic and highest-leverage actor given the platform's design |
| Leaked or compromised *service* credential (no acting-user token) | Schema registration (if `SchemaAdmin`), `/admin/dlq` read + replay and reconciliation triggers (if `Operator`, satisfiable by an OAuth scope alone), trace relay (if merely authenticated) | Cross-tenant data exfiltration, DDL abuse | Yes |
| Insider / anyone with repo write access | With zero branch protection on `main` (TB-12), any contributor — malicious, careless, or acting on a compromised workstation/credential — can merge or push directly with no required review and no CI gate actually enforced | Malicious insider, accidental breakage, or a compromised commit/PAT | Yes — broadened this refresh; previously scoped only to Authentik/orchestrator-credential holders (still true, see §5.6), now also covers anyone who can open or approve a PR against this repo |
| Compromised supply-chain dependency | Whatever a malicious/vulnerable package in any of 6 dependency ecosystems can do at build or runtime | Opportunistic or targeted supply-chain compromise | Yes — SCA scanning now exists (closing the prior refresh's F3), but with no branch protection gating on it, and with 2 of 6 ecosystems (.NET solution, TypeScript SDK) having no CI job that even builds/tests them, a compromised or merely broken dependency update reaches `main` unverified. **Demonstrated twice this session** (not hypothetical) — see §5.11, F2 |
| Automated bots/scanners | Generic internet noise against anonymous endpoints | Reconnaissance, opportunistic exploitation attempts | Yes, low severity — anonymous endpoints leak only non-sensitive operational metadata |
| Nation-state / advanced persistent threat | — | — | **Out of scope.** Nothing in this system's data classification (a general-purpose multi-tenant data platform, no evidence of government/critical-infrastructure use) suggests it is a plausible nation-state target specifically. |

---

## 5. Threat analysis (STRIDE-per-element)

### 5.1 Anonymous HTTP surface (`/metrics`, `/health`, `/health/live`, `/build`, dev-only `/openapi`)
- **Information disclosure**: `/metrics` exposes the full Prometheus registry; `/build` exposes assembly names and hashes; `/health` reveals per-backend (Postgres/StarRocks/Qdrant/Kafka) up/down status. All anonymous, all in scope for the external-opportunistic and bot actors.
- **Existing mitigations**: not routed through the Ingress path table's six gRPC prefixes and `/v1/traces` — but still reachable at the pod's `:8080`/`:8081`, so exposure depends on NetworkPolicy, not routing.
- **Residual risk**: Low. Reconnaissance-grade value only.

### 5.2 Authenticated entity CRUD/search RPCs (`ObjectMapping`, `ObjectPersistence`, `ObjectRetrieval`, `ObjectSearch`)
- **Elevation of privilege / tampering**: the query DSL exposes raw `expression` strings on aggregation metrics (`ObjectSearch.Aggregate`/`GroupBy`/`Pipeline`) that reach generated StarRocks SQL.
- **Existing mitigations**: two independent layers (character denylist including SQL-comment sequences, then a token allowlist restricting anything not a known function/operator/available column); values elsewhere are parameterized; identifiers resolved through a tenant-column-excluding allowlist; `StarRocksParameterGuard` catches unbound placeholders that StarRocks would otherwise silently treat as unset variables; output-size and shape caps bound both cost and injection surface area.
- **Residual risk**: Low, monitored. A denylist+allowlist combination is inherently more brittle against a *novel* StarRocks syntax feature than pure parameterization, but no concrete bypass is evidenced — tracked as a longer-term architectural note, not a finding.

### 5.3 Schema registration → DDL (`RegisterSchema`)
- **Elevation of privilege**: a successful registration interpolates caller-supplied type/property names directly into `CREATE TABLE`/`ALTER TABLE`/`CREATE POLICY`/`GRANT` statements in both Postgres and StarRocks.
- **Existing mitigations**: `SchemaAdmin` policy gate; a tight `^[A-Za-z][A-Za-z0-9]*$` allowlist regex (no quotes, no whitespace, no separators) on every identifier; a closed reserved-name enumeration for the tenant column; every identifier additionally quoted at the SQL-generation site (double-quoted Postgres, backtick-quoted StarRocks), so even a reserved-keyword collision parses as a valid quoted identifier.
- **Residual risk**: Low. The identifier allowlist admits no characters capable of terminating a quoted identifier or introducing a new SQL clause.

### 5.4 Acting-user impersonation mechanism — **correction: prior refresh's F1 was stale**
- **Spoofing / elevation of privilege**: the acting-user header is validated as a full second JWT (separate authority/audience), not trusted metadata.
- **Existing mitigations**: fail-closed row/field evaluator (denies all entity access when no acting-user principal is present); tenant-suspension check gated on the header's presence. `/admin/dlq` and `/admin/dlq/{id}/replay` now **require** a genuine acting-user token (`AuthenticateAsync("ActingUser")` returns 401 on failure, `Program.cs:414-465`) — fixed by commit `926c3b4` (2026-09-14, an earlier *csr-round4-remediation* fix, unrelated to this session's own work), which is already an ancestor of this document's own cited snapshot commit. **This refresh's own prior version of this document stated F1 as "unchanged, confirmed still present" — that was inaccurate**, carried forward from an even earlier TMA round without independently re-verifying the code; caught and corrected by `docs/criticalreviews/2026-09-15-iverson-critical-security-review-7.md` Finding #4, which also ran the regression test live (`ServiceTokenOnly_GetAdminDlq_Returns401`, 6/6 passed) as evidence.
- **Required mitigation / residual risk (Low — see F4)**: `GetSchema` and `TenantAdmin` surfaces still run off the primary principal only (architecturally deliberate). The one real remaining gap: `/admin/reconcile/{typeName}` (`Program.cs:400-412`) requires only the `Operator` policy against the primary principal, no acting-user check — but this endpoint returns no data, only triggers a reconciliation run, so blast radius is narrow.

### 5.5 Tenant isolation (Postgres / StarRocks / Qdrant)
- **Elevation of privilege / information disclosure (tenant boundary bypass)**: the platform's core multi-tenancy guarantee.
- **Existing mitigations**: four independent layers — Postgres RLS (policy + `FORCE` + non-superuser `iverson_runtime` role), StarRocks per-tenant database + role switch, Qdrant per-tenant collision-resistant collection naming (SHA-256 fingerprinted specifically to defeat separator-based aliasing) + 30-second scoped JWTs, and an API-layer tenant predicate applied independently of all three stores. The `iverson_maintenance`/`EntityAccess.CrossTenantMaintenance` exemption is explicit, narrow, and previously subject to a two-round adversarial review (confirmed to leak no cross-tenant row content).
- **Residual risk**: Low. The most heavily fortified and most recently, thoroughly reviewed boundary in the system.

### 5.6 API ↔ Authentik integration
- **Spoofing / tampering**: a compromised orchestrator admin token is the single highest-value credential in the system for identity-plane compromise.
- **Existing mitigations**: object-scoped `change_user`/`reset_user_password`; `is_superuser: false` explicitly pinned in both deployment targets; default-deny NetworkPolicy compensates for the plaintext transport; MFA enforced on human logins; pagination/response-shape assertions in `IdpAdminClient` throw rather than silently truncate on an unrecognized Authentik response shape.
- **Required mitigation / residual risk (Medium — see F3)**: Authentik's global `add_user` permission cannot currently be object-scoped, so a leaked orchestrator token can still create a *new* user directly into any non-superuser group. Unchanged since last refresh; not re-touched this session.

### 5.7 Enrichment / embedding pipeline (LLM integration) — **materially changed since last refresh**
- **Tampering (prompt injection)**: entity content a tenant user controls is interpolated into enrichment prompt templates, and the model's output is written back into that entity's columns and republished.
- **Existing mitigations — now closed**: every prompt template in `Iverson.Server/Iverson.Embeddings/EnrichmentPrompts.cs` wraps untrusted source text in `<<<BEGIN_SOURCE_TEXT>>>...<<<END_SOURCE_TEXT>>>` markers with an explicit "not instructions" framing, and `EscapeUntrustedText` neutralizes literal marker sequences in the source text itself so attacker content cannot forge a fake boundary (commits `fddd073`, `afe9e97`, both 2026-09-14 — ancestors of current HEAD). Source text is also truncated to a configured character cap; the source row is re-derived from the authoritative Postgres row (not the possibly-stale Kafka event payload) with a fail-closed check if the tenant cannot be resolved.
- **Independently, the Python agent layer** (`Iverson.Agents/Python/iverson_agent/{retrieval,evaluate}.py`) has its own, separately-verified defense for the same threat class: `_escape` HTML-escapes `<`/`>` in all attacker-influenceable text before wrapping in `<doc n="..." key="...">...</doc>` (retrieval) or `<passage>`/`<answer>` (the LLM-judge harness) tags, with an explicit system-prompt instruction that tagged content is data, not instructions. Both halves (tag boundary + escaping) were verified together this session via literal mutation testing (revert either half, confirm the forged-tag test then fails).
- **Residual risk**: Low. This finding (prior refresh's F2) is **closed** — both the production enrichment path and the agent/evaluation path now have tag-boundary-plus-escaping defenses, and both were verified by reproducing the original bypass and confirming the fix defeats it, not just by reading the diff.

### 5.8 Async event pipeline (Kafka)
- **Information disclosure**: `iverson.entity.events`/`.dlq` carry full plaintext entity payloads for every tenant on shared topics.
- **Existing mitigations**: SASL_SCRAM-SHA-512 + TLS in the Helm profile; consumers re-derive tenant authority from the authoritative Postgres row rather than trusting the event's own claimed tenant (this pattern was generalized across all projection consumers since last refresh — `ProjectionTenantResolution.cs`); retry/DLQ contract with poison-message detection.
- **Residual risk**: Low for boundary bypass; Medium for confidentiality if broker access controls or TLS configuration were misconfigured, since the payload itself carries no additional protection — a single-control dependency, not a currently-demonstrated gap.

### 5.9 Observability relay (`POST /v1/traces`)
- **Tampering / denial of service**: any authenticated service principal — no acting-user token, no specific scope beyond `RequireAuthorization()` — can relay an arbitrary JSON or protobuf blob directly into the cluster's Jaeger collector.
- **Existing mitigations**: content-type allowlist (`json`/`x-protobuf` only); a 1 MiB cap enforced both via declared `Content-Length` and the actual request-body-size feature.
- **Required mitigation / residual risk (Low — see F5)**: no structural/schema validation of the relayed payload beyond content-type and size.

### 5.10 Admin console (browser)
- **Information disclosure (token theft via XSS)**: id/access tokens live in `sessionStorage`.
- **Existing mitigations**: strong CSP (`base-uri`/`form-action`/`object-src` locked down), `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, HSTS when serving over https, and no refresh token ever issued or stored, bounding the value of a stolen token to its short access-token lifetime.
- **Residual risk**: Low. No code-level XSS evidenced in this recon (that determination is CSR's job); existing controls meaningfully bound blast radius if one is later found.

### 5.11 CI/CD and supply chain — **materially changed since last refresh, mixed result**
- **Tampering (build/dependency compromise)**: six independent dependency ecosystems (NuGet, two npm trees, Go modules, Maven, two Python trees), all on Dependabot's weekly schedule (`.github/dependabot.yml`).
- **Genuinely closed since last refresh**: `.github/workflows/dependency-scan.yml` and `.gitlab-ci.yml` now run a dependency-vulnerability scan across **all six** ecosystems on every PR and push to `main` (`dotnet list package --vulnerable`, `npm audit --audit-level=high` ×2, `govulncheck`, OWASP dependency-check for Maven, `pip-audit` ×2). This closes the prior refresh's F3 as stated. CodeQL SAST separately covers 5 languages.
- **One narrow residual (Low — see F4)**: the Maven/Java gate is correctly *configured* (plugin pinned, `-DnvdApiKeyEnvironmentVariable` used correctly so the raw key never reaches the command line or logs) but cannot complete a scan without a human-provisioned `NVD_API_KEY` secret, which does not currently exist in either CI system's settings. It fails closed (a visibly red check, not a silent pass) — a prior critical-security-review round already flagged and disclosed this as an ops task, not a code defect.
- **New finding, discovered live this session (Medium — see F2)**: `main` carries **zero GitHub branch protection** — confirmed via `gh api repos/counsellorben/Iverson/branches/main/protection` returning 404 "Branch not protected". This means the dependency-scan gate above, and CodeQL, run and report a status on every PR, but **nothing requires either check to pass before a merge**. Separately, **no CI workflow anywhere in this repository builds or runs the test suite for the .NET solution or the TypeScript SDK** — `dependency-scan.yml` only runs `npm audit`/`dotnet list package --vulnerable` (a manifest scan) against those two ecosystems, never `dotnet build`/`dotnet test` or `npm ci && npm test`. The combination is not hypothetical: during this session, two of the ~70 Dependabot PRs that auto-merged this way were genuinely broken and shipped to `main` undetected until manually caught —
  - `vitest` 3.2.6→5.0.0 in the TypeScript SDK: vitest 5.0.0 ships a `config.d.ts` that imports `@vitest/expect`, a package it no longer lists as a dependency (an upstream packaging defect), breaking the SDK's `tsc -p tsconfig.test.json` typecheck outright; its unpinned `vite` peer also resolved to a known-incompatible v8.
  - `FluentAssertions` 7.0.0→8.11.0 across 10 .NET test projects: FluentAssertions 8 introduces its own public `FluentAssertions.Value` type, colliding with `Google.Protobuf.WellKnownTypes.Value`/`Qdrant.Client.Grpc.Value` wherever both are wildcard-imported — 27+ `CS0104` compile errors across 7 files, meaning the entire .NET solution failed to build on `main` until this session's manual fix.
  Both were fixed this session (`dce9094`, `7107e1e`), but the structural gap that let them land unreviewed and unverified — no branch protection, no build job for 2 of 6 ecosystems — remains open. A third such incident, or a genuinely malicious (not merely broken) dependency update in either of those two ecosystems, would land on `main` the same way.

### 5.12 Cloud control plane / IaC
- **Elevation of privilege**: public EKS API endpoint.
- **Existing mitigations**: CIDR restriction is a *required* Terraform variable with no default — fails closed if an operator omits it.
- **Residual risk**: Low. A positive control, not a gap; included for completeness.

### 5.13 At-rest encryption (node disks, storage classes) — **not covered by the prior refresh**
- **Information disclosure (data at rest)**: cluster node boot/OS disks and dynamically-provisioned volumes.
- **Existing mitigations**: GKE node boot disks encrypted with a customer-managed data-volume key (`9869c64`); AKS node OS disks encrypted with a disk-encryption-set (`b281e6f`); an admission-time deny rule blocks PVCs requesting a storage class outside an explicit encrypted-storageclass allowlist (`b16b9ea`). Predates this refresh's baseline (committed 2026-09-08) but was absent from the prior TMA document — added here for completeness, not independently re-verified live in this pass (evidence is the commit history and the project's own memory index, not a fresh Terraform/Helm read).
- **Residual risk**: Unknown for the Azure check specifically — the project's own tracking (`project-at-rest-encryption`) records "Azure check 5 ships unverified by choice," i.e., a known, deliberate gap in that platform's own verification coverage, not a code defect found here.

### 5.14 Dev-only tooling and secrets (docker-compose, LoadTest CLI, Agent CLI, conformance harness)
- **Cryptographic failure (hardcoded secrets)**: `docker-compose.yml` and the compose-only Authentik blueprint commit static credentials (`POSTGRES_PASSWORD`, `AUTHENTIK_SECRET_KEY` ×3, `AUTHENTIK_POSTGRESQL__PASSWORD`, `AUTHENTIK_BOOTSTRAP_PASSWORD`/`_TOKEN`, `QDRANT__SERVICE__API_KEY` — roughly seven distinct secrets, including a non-expiring Authentik API token).
- **Existing mitigations**: every instance is self-labeled dev-only; confined to a localhost-only compose target with no production code path reading these files; the file's own header comment states Kubernetes targets generate real per-release secrets at deploy time. A separate, newer set of compose secrets (`IVERSON_LOADTEST_CLIENT_SECRET` and five siblings) already uses the stronger `${VAR:?message}` required-with-no-default pattern via `scripts/generate-compose-secrets.sh` — a pattern not yet extended to the older seven.
- **Residual risk (Low — see F6)**: trips a strict reading of "hardcoded secrets in source" even though real-world exposure is minimal given the containment already verified.

---

## 6. Findings — prioritized roadmap

| # | Title | Tag | Severity | Category |
|---|---|---|---|---|
| F1 | `/admin/reconcile/{typeName}` requires only the `Operator` policy against the primary principal (no acting-user check) — triggers reconciliation only, returns no data | Implemented | Low | Broken Access Control (A01) |
| F2 | `main` has zero branch protection, and 2 of 6 dependency ecosystems (.NET solution, TypeScript SDK) have no CI build/test job — demonstrated twice this session by real broken Dependabot merges reaching `main` unverified | Implemented | Medium | Vulnerable Components & Supply Chain (A06) / Security Misconfiguration (A05) |
| F3 | Authentik orchestrator's global `add_user` cannot be object-scoped — new-user-into-group residual | Planned | Medium | Broken Access Control (A01) |
| F4 | Java/Maven dependency-scan gate is correctly configured but cannot complete a scan without a provisioned `NVD_API_KEY` secret (fails closed) | Implemented | Low | Vulnerable Components & Supply Chain (A06) |
| F5 | Unvalidated trace relay (`/v1/traces`) reachable by any authenticated service principal | Implemented | Low | Security Misconfiguration (A05) |
| F6 | Seven static dev-only credentials committed in source (docker-compose + compose blueprint) | Implemented | Low | Cryptographic Failures & Data Exposure (A02) |
| F7 | Pervasive in-cluster plaintext transport is compensated by a single NetworkPolicy control | Implemented | Low | Cryptographic Failures / Security Misconfiguration (A02/A05) |
| F8 | Audit log has no tamper-evident store; sanitization is CRLF-stripping only | Implemented | Low | Repudiation |
| F9 | `values-aws.yaml`'s TLS termination lives entirely outside this chart's management (ALB-assumed, unverified) | Planned | Low | Security Misconfiguration — Requires Verification |

**Closed since last refresh** (carried here for continuity, not active findings):
- *Prior F2 — unmitigated prompt injection into the enrichment pipeline*: closed. Tag-boundary + escaping defense shipped in both the production enrichment path and the agent/evaluation path, verified by reproducing the original bypass. See §5.7.
- *Prior F3 — no CI-gated dependency-vulnerability scanning*: closed for 5 of 6 ecosystems; narrowed to F4 above for the Java/Maven ecosystem's operational (not code) gap.
- *This refresh's own F1 (as first written) — leaked Operator-credential DLQ bypass*: **was stale at the moment it was written** — the fix (`926c3b4`) predates this document's own cited snapshot commit. Corrected the same day by `docs/criticalreviews/2026-09-15-iverson-critical-security-review-7.md` Finding #4 (regression test run live, 6/6 passed); F1 above now reflects only the genuine, narrower residual.

Prioritization rationale (severity × exploitability × blast radius):
1. **F2** ranks first: not a single scoped credential leak — it is a structural gap in the path every code change takes to reach production, and it has already produced two real (if non-malicious) incidents in the ~36 hours before this refresh. Exploitability requires either compromising a contributor's write access or a supply-chain actor timing a malicious update against these two unguarded ecosystems; blast radius is whatever that update can do once it ships to `main` unreviewed.
2. **F3** ranks second: a leaked high-privilege credential (the orchestrator token), already substantially mitigated by prior remediation work — the residual is narrow (new-user-into-group only).
3. **F4** is a disclosed, fail-closed ops task, not a live gap — a red CI check is the direct, visible symptom, not a silent pass.
4. **F1, F5, F6, F7, F8, F9** are Low: each has either a narrow blast radius, a real compensating control, or (F9) is unverified rather than confirmed.

---

## 7. When to refresh

Refresh this document after any of the triggers in Appendix C fire for this system — most relevant given Iverson's own trajectory: a new or changed auth/authz model, a new external integration, a new class of sensitive data being handled, or a major architectural change (a new store, a new SDK language, a rewrite of the query DSL). This refresh adds one more standing trigger specific to this repository: **any change to CI/CD gating or branch protection**, given F2 above — the next refresh should re-check `gh api repos/.../branches/main/protection` directly rather than assume it's unchanged. Given how frequently the auth/authz, tenant-isolation, and now CI/supply-chain posture of this codebase has been revised (three prior critical-security-review rounds, two remediation cycles, and ~70 automated dependency bumps in the 48 hours before this refresh alone), a lighter-weight, more frequent refresh cadence than the generic "quarterly check-in" remains warranted — consider refreshing after any batch of security remediation work, or after any large wave of automated dependency updates, rather than waiting for a full quarter.
