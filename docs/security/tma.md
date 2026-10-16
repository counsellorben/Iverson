# Threat Model Analysis — Iverson

## 1. Header

**Last refreshed:** 2026-10-16 against commit SHA `33eb4f1e7ff143df3310c096c5dd9768d33a3bed` (local `main`).
- The previous refresh is dated 2026-10-08, at `19ab57c0`, and is committed as `33eb4f1e`. This document replaces it.
- 111 commits lie between the two. They include all four sub-projects (A, B, C, D) of CSR round 11, `docs/criticalreviews/2026-10-08-iverson-critical-security-review-11.md`.
- **Repository state is ahead of the live state.** `origin/main` is at `d710aadb`, so sub-projects A and B are pushed and C and D are merged locally but not pushed. Until they are pushed:
  - live branch protection is still the pre-D shape (11 required checks);
  - `publish-images.yml` has never run, so no first-party image has been published or signed.

  Statements about C and D below describe the repository, not a deployed system.

**Sources:**
- Code: `/home/ben/repositories/Iverson`, the full monorepo:
  - the server runtime, `Iverson.Server/Iverson.{Api,Sql,StarRocks,Vector,Embeddings,Events,Patterns}` and the protos in `Iverson.Clients/Common/Proto`;
  - the five SDKs, their samples and conformance drivers, `Iverson.Clients/*`;
  - the reasoning agent, `Iverson.Agents/Python`;
  - the admin console, `Iverson.AdminUI`;
  - the tools, `Iverson.Server/Iverson.LoadTest` (with `scripts/`), `Iverson.Server/Iverson.ClientConformance` and `Iverson.Server/deploy/scripts`;
  - deployment: `Iverson.Server/docker-compose.yml`, `Iverson.Server/deploy/` (Helm, Authentik blueprints, kind, Terraform, the operator pin stub), `docs/runbooks/`;
  - CI, `.github/` and `.gitlab-ci.yml`.
- Design inputs:
  - the previous refresh (`git show 33eb4f1e:docs/security/tma.md`);
  - CSR round 11;
  - the four round-11 specs in `docs/specs/2026-10-{08,09,12}-csr-round11-*-design.md`, used for their "Known issues" lists.

  Each claim below was re-checked against the code.
- Reconnaissance: three parallel read-only surface maps, covering (1) the server runtime, (2) clients, console, agent and tools, and (3) deployment, identity and CI.
  - The deployment map rendered the chart for all five profiles and read live branch protection and repository settings with `gh api` GETs.
  - The synthesis re-read `/admin/dlq` (`Program.cs:623-656`; `DlqRepository.cs:31-39`), which lists rows without their payloads.

**References:** threats are categorized with this skill's built-in Vulnerability Taxonomy (Appendix A). Findings are scored with its Severity Classification and SQS rubric (Appendix B). Both ship with the `tma` skill and are not tracked separately here.

---

## 2. System overview

Iverson is a multi-tenant .NET 10 gRPC platform for schema-driven object storage, retrieval and search. It sits in front of four stores:
- PostgreSQL, the system of record, with row-level security;
- StarRocks, for analytics, aggregation, pipelines and pattern matching;
- Qdrant, for vector and chunk search;
- Kafka, the outbox fan-out bus.

Around it are five client SDKs, a React admin console, a Python LLM reasoning agent, and Authentik as the single identity provider. First-party images are built and signed in GitHub Actions and published to GHCR.

Every tenant's data shares the same database, cluster and vector deployment. Tenants are separated only by software-enforced boundaries, and tenant identity comes from an IdP-issued claim. A failure of identity or isolation is therefore a cross-tenant breach, not a single-customer incident. Inside a tenant, a second boundary separates users: row ownership and field permissions declared per type.

## 3. Architecture & data flows

### 3.1 Components

| Component | Role | Key paths |
|---|---|---|
| `Iverson.Api` | One binary with two roles (`WORKLOAD_ROLE`). `api` serves six gRPC services (23 RPCs) and `/v1/traces`, and runs the outbox publisher. `worker` runs five Kafka consumer groups and the timer workers, and alone holds the StarRocks provisioner credential. Both roles map the probes, `/admin/*` and `/admin/console/*`, and both run the startup DDL and the RLS re-apply. | `Iverson.Api/Program.cs:37-39, 273-281, 464-485` |
| Listeners | `:8080` gRPC and the `/admin/reconcile` and `/admin/dlq*` routes. `:8081` `/health`, `/health/live`, `/build`, `/metrics`, and, through the admin-api ingress, `/admin/console/*` and `/v1/traces`. **Kestrel serves TLS on both** when the profile sets `backendTls` (every Helm profile except Azure before its trusted-root step). A listener gate pins routes by accepting port. | `charts/api/templates/deployment.yaml:232-249`; `Program.cs:939-949` |
| Middleware | Port gate, then the pre-auth limiter (keyed by `ClientPartitionKey`: `X-Forwarded-For` honoured only from configured proxy CIDRs), CORS, authentication with revocation, authorization, and the post-auth limiter. gRPC interceptors: rate limit (per `sub`), then acting user. | `Program.cs:512-529`; `RateLimiting/ClientPartitionKey.cs:16-28` |
| `Iverson.Sql` | Dapper and Npgsql over VerifyFull TLS. Runtime reads and writes run `SET LOCAL ROLE iverson_runtime` plus `app.tenant_id` under FORCE RLS. Cross-tenant maintenance runs as `iverson_maintenance` (BYPASSRLS). Every schema-derived identifier passes `PostgresIdentifier` before it reaches SQL. | `PostgresRepository.cs:136-160`; `PostgresIdentifier.cs:16-38` |
| `Iverson.StarRocks` | Query, pipeline and pattern-row builders over VerifyFull TLS. Each tenant has its own database and hashed role, entered with `SET ROLE`. Pipeline joins read a registered type through a derived table already filtered for owner and tenant. **Client-written expressions are parsed into a tree and re-emitted; no client text reaches SQL.** | `StarRocksPipelineBuilder.cs:599-645`; `Expressions/ClientExpression.cs:48-59` |
| `Iverson.Vector` | One collection per tenant, named with a SHA-256 fingerprint. Data-plane calls carry a 30-second HS256 JWT scoped to one collection. TLS with the leaf thumbprint pinned (Helm). | `IntelligenceTenantScope.cs:40-103`; `Vector/ServiceCollectionExtensions.cs:21-64` |
| `Iverson.Patterns` | MatchPattern: an in-process parser and NFA matcher with step, thread, row and time budgets. Never turned into SQL. | `Iverson.Patterns/*` |
| `Iverson.Embeddings` | TEI embeddings and the enrichment LLM (Ollama `/v1/chat/completions`). Plaintext and unauthenticated. | `EmbeddingService.cs`; `EnrichmentService.cs` |
| `Iverson.Events` | Kafka producer and consumer (SASL_SSL). Events are unsigned JSON carrying the full row and the prior row. DLQ metadata travels in `dlq.*` headers. | `EntityEvent.cs:20-35`; `MessageDispatcher.cs:142-157` |
| `Tenancy/` | Authentik admin client over CA-pinned TLS, the orchestrator token exchange, and the tenant-status and token-revocation caches (30 s, per process). | `AuthentikTrust.cs`; `AuthentikOrchestratorTokenHandler.cs`; `TokenRevocationCache.cs` |
| SDKs (`Iverson.Clients/*`) | Send a service bearer plus `x-acting-user-authorization`. TLS by default; plaintext needs a named opt-in. Java now checks the channel's security level when each call is sent. Hand-built .NET and Python coordinators refuse acting tokens unless told the transport is safe. Token endpoints must be https and are never followed through redirects. | §5.10 |
| `Iverson.AdminUI` | React SPA behind nginx-unprivileged. OIDC code plus PKCE, a public client issued no refresh token, tokens in sessionStorage, strict CSP. | `src/auth/AuthProvider.tsx:45-59`; `nginx.conf:32-37` |
| `Iverson.Agents/Python` | A reasoning agent acting as the calling end user. It retrieves tenant passages and sends them to the Anthropic API. Terminal output is neutralised. | `session.py`, `retrieval.py`, `__main__.py:51-60` |
| Tools | LoadTest and ClientConformance: harnesses holding privileged datastore and IdP credentials. A startup check refuses credential-bearing endpoints that are neither loopback nor https. | `LoadTest/Auth/CredentialEndpoints.cs:22-52` |
| Authentik 2026.5.3 | The single IdP and JWT issuer. Server pod listeners: `:9000` plaintext (loopback, kubelet, port-forward), `:8443` TLS (API only), `:9080` the public listener (TLS when `backendTls`). The worker pod carries the `jwks-relay` sidecar. `akadmin` is inactive; a break-glass superuser has no standing password. | `charts/authentik/templates/configmap-tls-proxy.yaml`; `blueprints-configmap.yaml:131-212` |
| Publish pipeline (new) | `publish-images.yml`: `images` lists what to publish; `publish` builds, pushes and makes the SBOM (`packages: write`, no OIDC); `sign` signs and attests keyless with cosign (`packages: write`, `id-token: write`). A published tag is never moved, and an existing tag is reused only if this workflow already signed and attested it. | `.github/workflows/publish-images.yml` |
| Deployment | Docker compose (dev, loopback-only). kind local and laptop profiles (loopback-only). Cloud on EKS, AKS or GKE: Helm umbrella chart plus Terraform, with edge WAF rate limits on the login path. Default-deny NetworkPolicy in the `iverson` namespace. Operator chart versions come from a Dependabot-tracked stub. | `docker-compose.yml`; `deploy/helm/iverson`; `deploy/terraform` |

### 3.2 Primary data flows

1. **Write.** The SDK calls gRPC through the edge to `:8080`, over TLS on every hop except Azure before its trusted-root step.
   - The API validates the service JWT and the acting-user JWT, including the revocation check on each, and checks that the acting user's tenant is active (30 s cache).
   - The row and field evaluator runs. It denies by default.
   - The key is server-assigned (UUIDv7). Update never creates a row, and an owner mismatch answers exactly like an absent row.
   - The entity and the outbox row are written in one Postgres transaction under RLS, then published to Kafka.
2. **Projection.** Workers consume the event.
   - For Created and Updated, they re-derive tenant and owner from the authoritative Postgres row (maintenance role). Other column values come from the event payload. For Deleted, the tenant comes from the event's snapshot.
   - Workers upsert into the tenant's StarRocks database, embed text through TEI into the tenant's Qdrant collection, and optionally send source text to the enrichment LLM, whose output is written back into the entity and re-published.
   - The worker provisions new tenants in StarRocks with the separate `iverson_provisioner` login.
3. **Read and search.**
   - The evaluator builds a constraint per type, joined types included. JOIN `ON` columns, filters, sorts and expression columns are field-checked.
   - StarRocks runs under `SET ROLE` with tenant and owner predicates. Pipeline joins read a pre-filtered derived table. Qdrant runs with a collection-scoped JWT.
   - Another tenant's owned type is indistinguishable from an unregistered one on every data RPC.
   - Responses strip the tenant column and mask fields to AllowedFields. OneToMany relations are hydrated only when the child's FK is readable.
4. **Identity provisioning (Helm).**
   - The API reads its projected service-account token (orchestrator audience, 600 s, pod-bound) and exchanges it over the CA-pinned `:8443` hop for a 5-minute access token.
   - Authentik checks the signature against the cluster's service-account keys, which the worker's relay fetches, and a policy pins the subject, the audience, `exp − iat ≤ 600`, the pod claim, and the workload user's group.
   - With that token, CreateTenant and InviteUser create users and return a 15-minute recovery link.
   - The admin-automation identity (Operator scopes) is federated the same way, from a `kubectl create token` assertion for `<rel>-admin-automation`, with no pod claim.
   - Compose keeps a static, non-expiring token.
5. **Human login.** The browser reaches `authentik.<host>` through the edge, which rate-limits the flow executor per client IP (WAFv2, Cloud Armor, WAF_v2).
   - The public listener forwards only the login, flow, OIDC, static and health paths, fixes `Host`, normalises `X-Forwarded-For`, and stamps `X-Iverson-Public-Ingress`.
   - A reputation policy throttles failed logins per client IP.
   - Recovery enrols TOTP before the password is written. Superusers are refused on the public listener.
6. **Agent.** The agent retrieves under the end user's token and sends the passages and the question to Anthropic. Model-chosen tool inputs come back to Iverson, re-validated against the user's schema.
7. **Image supply (new).**
   - A push to `main` or a `vX.Y.Z` tag, or a manual run, builds the app images (and on tags and manual runs the model images).
   - It pushes them to GHCR, signs the digest keyless with the workflow's GitHub OIDC identity (a public Rekor entry), and attaches a signed SPDX SBOM.
   - Clusters pull by tag. Nothing verifies the signature at admission.

### 3.3 Trust boundaries

| # | Boundary | What crosses | Enforcement |
|---|---|---|---|
| TB1 | Internet → cloud edge → ingress → pod | Four hosts:<br>- `<host>`: six gRPC prefixes, plus `/admin` for the console SPA;<br>- `admin-api.<host>`: `/admin*`, anonymous `/health`, `/v1/traces`;<br>- `authentik.<host>`: the `:9080` public listener only. | Edge TLS. GCP refuses port 80; AWS and Azure redirect it. **Edge → pod is TLS on alb, gce and nginx, but the edge does not verify the pod certificate.** Azure is plaintext to the pod until its trusted-root step, which an upgrade guard then forces. Edge → admin-ui (static files) stays plaintext. |
| TB2 | Public listener ↔ Authentik admin plane | Login, flow executor, OIDC, static, health | Path allow-list on `:9080`; admin interface and `/api/v3` answer 404. Host fixed, `X-Forwarded-For` rebuilt from trusted proxy ranges. The marker header lets policies refuse admin-automation, the orchestrator, and superuser login and recovery from public callers. |
| TB3 | SDK or service → API (service identity) | Bearer JWT for a configured audience | Issuer set, audience allow-list (test audiences only on `provisionTestIdentities` profiles), CA-pinned JWKS, revocation hook, fallback `RequireAuthenticatedUser`. |
| TB4 | Service identity → end user (acting user) | `x-acting-user-authorization` JWT: `tenant_id`, `groups`, `sub` | A separate scheme sharing the issuer and keys, distinguished by audience. Inactive tenants refused per call. Data RPCs with no acting user are denied by the evaluator. |
| TB5 | Acting user → tenant data, and user → user within a tenant | Rows, projections, vectors | Evaluator, then Postgres FORCE RLS, StarRocks per-tenant DB and role, Qdrant per-collection JWT, and type visibility, for tenants. Owner predicates (derived tables for pipeline joins) and AllowedFields for users. **The StarRocks and Postgres app logins can each still cross tenants** (§5.4). |
| TB6 | Claim → privilege | `groups ∋ operators` or `scope ∋ admin` gives Operator. `groups ∋ operators` or `scope ∋ schema_admin` gives SchemaAdmin. `groups ∋ tenant-admins` gives TenantAdmin. | Claims come from Authentik groups and scope mappings. `tenant_id` comes from a user attribute users cannot write. |
| TB7 | API → Authentik admin plane | User creation, recovery links, group PATCHes | TLS to `:8443` with a chart CA; 5-minute federated token on Helm; static token on compose. |
| TB8 | Kubernetes RBAC → Authentik orchestrator rights | Pod-bound projected tokens for `<rel>-api` | The pin policy checks `sub`, `aud`, `exp − iat ≤ 600`, the pod claim and the workload user's group. It does not check `iss` or that the pod still exists. |
| TB8b | Kubernetes RBAC → Operator API rights (new) | Tokens for `<rel>-admin-automation`, minted with `kubectl create token` | The pin checks `sub`, `aud` and `exp − iat ≤ 600`; no pod claim. The result is a 1-hour Operator token. Who may mint tokens for that account is left to namespace RBAC, which the chart does not set. |
| TB9 | API and worker ↔ data stores | Tenant records and credentials | Postgres VerifyFull, StarRocks VerifyFull, Kafka TLS plus SCRAM plus ACLs, Qdrant TLS (pinned), Authentik → Postgres verify-full. **Still plaintext: StarRocks FE ↔ BE, Qdrant Raft, TEI, Ollama, Jaeger, Prometheus (worker scrape, API → Prometheus).** |
| TB10 | Worker ↔ ML backends | Tenant text out, model output back into tenant data | No auth or TLS. NetworkPolicy limits who reaches them; model pods have no egress. |
| TB11 | Agent ↔ Anthropic, and retrieved content ↔ LLM instructions | Tenant passages, questions, model-chosen tool calls | Escaping and delimiters, schema-validated filters, a tool-call budget, citation enforcement; terminal output neutralised. |
| TB12 | Load-balancer ranges and peers ↔ pods | Everything in TB1 | `clusterCidrs` narrowed to the LB ranges; the ingress-nginx peer only on the nginx class; fallback toggles need `keepFallbacks`. Operator namespaces have no NetworkPolicies. |
| TB13 | Developer host ↔ compose and kind | Dev stacks with test identities | Compose and kind both bind `127.0.0.1`. |
| TB14 | Contributors and CI ↔ `main` and `v*` tags; third-party actions, tools, images | Code, build and scan steps, release tags | Branch protection: live 11 required checks (22 once the repo's script is applied), no reviews, admin bypass, no rulesets. Every action SHA-pinned, tool downloads hash-checked, read-only default `GITHUB_TOKEN`. |
| TB15 | Publish workflow → GHCR, Rekor → clusters (new) | Images, signatures, SBOM attestations | Only `sign` holds `id-token: write`. Signatures bind this workflow on `main` or a `vX.Y.Z` tag. Reused app tags must match the commit or release ref. Clusters pull by tag with no admission-time verification. |

## 4. Threat actors

| Actor | Capabilities | Motivation | In scope? |
|---|---|---|---|
| External opportunistic | Scanning, credential stuffing against Authentik's public login, floods, known-CVE exploitation | Opportunistic compromise, resource abuse | **Yes.** The API, the console and Authentik's login are internet-facing on cloud profiles. Login is now rate-limited at the edge and throttled per IP. |
| External targeted | Reconnaissance of the public IdP, probing tenant isolation and expression parsing, phishing operators, tenant admins or invitees | Data from a specific tenant, platform-wide compromise | **Yes** |
| Insider, regular tenant user | Tenant-scoped CRUD, search and pipelines with an acting-user token; authors documents that later feed the LLM | Reading other users' private rows; escalating to another tenant or to admin | **Yes.** The central actor. |
| Insider, privileged (Operator, SchemaAdmin, TenantAdmin) | Tenant lifecycle, schema DDL, DLQ replay, cross-tenant reconcile, user invites | Abuse, or a stolen privileged credential | **Yes** |
| Cluster-privileged insider | Kubernetes RBAC in the `iverson` namespace: reads Secrets and Helm release Secrets, creates or execs pods, mints service-account tokens, port-forwards | Credential theft; IdP and Operator control through TB8 and TB8b | **Yes** |
| Network-positioned in-cluster or VPC attacker | A compromised pod, node or VM on the paths between the edge, the pods and the stores | Capture or tamper with traffic | **Yes, reduced.** Authentication-carrying hops are now TLS (unverified at the edge); data-carrying store and ML hops remain plaintext (§5.11). |
| Compromised supply chain | Malicious dependency, action, base image, operator chart or model weight; a repository writer creating release tags | Code execution at build or run time; a signed malicious image | **Yes.** Five language ecosystems, third-party images and operators, and a public repository with admin bypass. |
| Automated bots | High-volume requests | Resource exhaustion, cost inflation | **Yes** |
| LAN peer of a developer | Reaches a dev stack on the local network | Opportunistic access to test identities | **No.** Compose and kind bind loopback. |
| Nation-state | Zero-days, persistent access | Espionage | **No.** Nothing indicates Iverson is critical infrastructure. |

## 5. Threat analysis (STRIDE-per-element)

### 5.1 Listeners, ingress and rate limiting
- **Spoofing.** The listener partition keys on the accepting socket's port, not on `Host`.
- **Denial of service.**
  - The pre-auth limiter keys on `ClientPartitionKey`, which trusts `X-Forwarded-For` only from configured proxy CIDRs. The post-auth limiter, the gRPC per-`sub` limit, query caps, MatchPattern budgets and the 4 MB receive cap stay in place.
  - The login path now has edge rate limits on all three clouds; the chart refuses a cloud class without its WAF value (`charts/authentik/templates/ingress.yaml:1-4`).
  - Residual: a shared NAT can be locked out of login for up to 24 h once its failures reach the reputation threshold (accepted in the identity-plane spec).
- **Information disclosure.**
  - Anonymous `/health` (store up/down) is published on `admin-api.<host>`. `/build` and `/metrics` are reachable only from the LB ranges and Prometheus.
  - The `authentik.` and `admin-api.` hosts send no HSTS at the edge, while AWS and Azure redirect port 80. A first visit over HTTP can be intercepted by an attacker on the user's network (F2).
- **Elevation of privilege.** `/admin/console/*` and `/v1/traces` are unpinned and authenticated; `/v1/traces` admits only console-audience tokens of active tenants.
- **Residual:** Low.

### 5.2 Identity: Authentik, the two JWT schemes, acting users and revocation
- **Spoofing, closed.** JWKS and discovery go over TLS to `:8443` with a chart CA; `RequireHttpsMetadata` on both schemes.
- **Spoofing, by design.** Both schemes trust one issuer set and one key set and differ only by audience.
- **Spoofing, verified mitigated.** Users cannot rewrite their own `tenant_id` (live probe 2026-10-02).
- **Elevation of privilege, standing credentials largely closed (CSR-11 #9):**
  - `akadmin` is inactive. A break-glass superuser has no standing password, and is activated by a 1-hour recovery link from `ak shell` used over port-forward.
  - Superusers are refused at login and in recovery on the public listener.
  - Admin-automation has no client secret; it needs a service-account token assertion.
  - Residuals (F1):
    - the bootstrap password value remains in `<rel>-authentik-app`, the worker's environment and Helm release Secrets, though unusable while `akadmin` is inactive;
    - `AUTHENTIK_SECRET_KEY` is standing and never rotated;
    - compose's static tokens are unchanged (dev, loopback-only).
- **Elevation of privilege, workload federation (TB8, TB8b; F1).**
  - **Orchestrator.** The pin now bounds lifetime (`exp − iat ≤ 600`) and requires a pod claim. A token copied from a pod stays usable until it expires, even after the pod is deleted (accepted in the identity-plane spec). `iss` is not checked. The orchestrator role's global `add_user` can still create a user into any non-superuser group, `operators` included.
  - **Admin-automation (new).** Anyone allowed to mint tokens for `<rel>-admin-automation` in the namespace, plus a port-forward, obtains a 1-hour Operator token. The runbook asks operators to restrict `serviceaccounts/token`, `pods/exec` and pod creation; the chart sets no RBAC.
- **Elevation of privilege, revocation (F4).**
  - RemoveUser now revokes before and after deactivation (CSR-11 #13).
  - Remaining timing:
    - revocation and tenant-status caches are 30 s per replica, with process-local invalidation;
    - `iat` is compared with Postgres `now()` with no skew allowance;
    - suspending or deleting a tenant writes no revocation rows;
    - a stream already open finishes;
    - the acting-user provider still issues refresh tokens, whose claims are recomputed at refresh.
- **Repudiation.** Policy denials and admin operations are audit-logged with the service and acting `sub`. Authentik now records the client address derived from trusted proxy ranges (CSR-11 #8). Only callers entering `:9000` through port-forward can still set it.
- **Residual:** Low (F1, F4).

### 5.3 Row and field authorization evaluator
- **Elevation of privilege.** Denial is the default: no rules, no acting user, no tenant column, no `tenant_id`, or no owner field for a caller without a bypass role.
- **Elevation of privilege, closed since the last refresh (CSR-11 sub-project A):**
  - **Pipeline outer joins (old F1, CSR-11 #1).** A fresh registered join source is joined as a derived table already filtered for owner and tenant (`StarRocksPipelineBuilder.cs:599-645`). Tests pin every join kind and a self-join.
  - **OneToMany hydration (#3).** Skipped when the child is denied or its FK is unreadable; the schema catalog drops the same edge.
  - **Template relation traversal (#12).** Removed: relation placeholders are refused at registration and render empty.
  - **Update existence oracle (#14).** An owner mismatch answers exactly like an absent row.
- **Information disclosure.** Request-level Search, Aggregate and GroupBy joins still put joined-type predicates in `ON`. That is safe only because the primary's predicates sit in the outer `WHERE` (`StarRocksQueryBuilder.cs:914-936`). No shared "authorized source" helper exists, so a new builder or join kind needs the same care (§7 trigger).
- **Residual:** Low.

### 5.4 Data stores and tenant isolation
- **Tampering and injection, largely closed (old F3, CSR-11 #10).**
  - Client-written aggregation, metric and derive expressions now go through a lexer, parser and emitter (`Iverson.StarRocks/Expressions/`).
    - SQL is written from the tree.
    - Columns resolve through each site's field-checked callback.
    - Functions come from an allow-list, and derive aggregates require `OVER`.
    - No client text is copied into the query.
  - Identifiers into Postgres pass `PostgresIdentifier` at registration, rehydration and every sink (CSR-11 #2).
  - Values are always parameters.
- **Information disclosure, credential breadth (F3).**
  - **StarRocks.** `iverson_app` no longer holds `user_admin` or `CREATE DATABASE`. It still holds every tenant role, so isolation depends on the session's `SET ROLE`, and `OPERATE ON SYSTEM` (accepted in the server-authorization spec). The worker-only `iverson_provisioner` now holds `user_admin`, `CREATE DATABASE`, every tenant role and `ALTER`.
  - **Postgres.** The app login owns the tables and is a member of `iverson_maintenance` (BYPASSRLS), so RLS guards against query bugs, not a compromised app.
- **Information disclosure, Qdrant.** Data calls use 30-second JWTs scoped to one collection; the raw key is used for management.
- **Residual:** Low (F3).

### 5.5 Schema registry and DDL (RegisterSchema)
- **Tampering.** SchemaAdmin defines tables, columns, rules, templates and enrichment hints. Re-registration drops undeclared columns after the drift check and is denied across owners. A SchemaAdmin call with no acting user registers an unowned, platform-wide type.
- **Information disclosure, closed for data RPCs (old F9, CSR-11 #20).** Another tenant's owned type is indistinguishable from an unregistered one on every data RPC (`SchemaVisibility.TryGetVisible`). RegisterSchema still answers PermissionDenied for a foreign name, to a SchemaAdmin only (accepted).
- **Elevation of privilege.** SchemaAdmin and Operator can be satisfied by an OAuth scope on the admin-automation token (TB8b).
- **Residual:** Low.

### 5.6 Query languages: search, pipeline and MatchPattern
- **Tampering and injection.** See §5.4. MatchPattern is interpreted in-process and never becomes SQL.
- **Denial of service.** MatchPattern is bounded by steps, threads, rows and a 30 s timeout; expressions are capped at 1,000 characters and nesting depth 32.
- **Residual:** Low.

### 5.7 Kafka, consumers and the DLQ
- **Tampering.**
  - Events are unsigned. Created and Updated re-derive tenant and owner from Postgres; other projected columns come from the event payload; Deleted events trust the snapshot's tenant.
  - DLQ replay produces to the stored `SourceTopic` without an allow-list.
  - Every path needs Kafka write access (SCRAM plus prefix ACLs, one principal shared by the api and worker).
- **Information disclosure.** `/admin/dlq` lists exception messages, not payloads, to an Operator primary acting for a user of the same tenant (`DlqRepository.cs:31-39`). Exception messages can embed SQL text, Authentik error bodies or LLM output.
- **Residual:** Low.

### 5.8 ML backends and enrichment
- **Information disclosure.** Tenant text reaches TEI and Ollama over plaintext with no authentication (F2). Model pods have no egress.
- **Tampering, prompt injection into persisted data (F8).**
  - A tenant document can steer the enrichment LLM, whose output is written back into that entity and re-embedded. The blast radius is that tenant's own row.
  - Registration now refuses write roles on enrichment targets, and requires each target's readable roles to be a subset of every restricted source's (CSR-11 #11).
  - That invariant is checked at registration only, not when stored descriptors are reloaded, so a type registered before the change keeps any inconsistency until it is re-registered.
  - The SchemaAdmin-authored hint is inserted unescaped.
- **Supply chain.** TEI weights are pinned to a revision; the Ollama build checks the full manifest digest; compose still pulls floating models.
- **Residual:** Low.

### 5.9 Admin plane: `/admin/*`, the console and the tenant services
- **Elevation of privilege.**
  - `/admin/reconcile` and `/admin/dlq*` need Operator on the primary principal, and the DLQ routes check the acting principal's tenant and status. They are pinned to `:8080`.
  - Tenant lifecycle requires Operator, and deletion is now terminal.
  - TenantAdmin takes the tenant from the primary principal, requires it to be active, and checks the target user against it.
  - CreateTenant and InviteUser return 15-minute recovery links (F5).
- **Information disclosure.** Console readers check tenant status and evaluate the console token as the acting user. `/admin/console/metrics` is Operator-only with no tenant filter.
- **Residual:** Low.

### 5.10 Clients: SDKs, admin console and reasoning agent
- **Spoofing and information disclosure, SDKs.**
  - TLS by default, plaintext by named opt-in, https token endpoints, no redirects.
  - **Java:** credentials are checked against the channel's security level when each call is sent, so TLS clients need no opt-in (CSR-11 #16).
  - **.NET and Python:** hand-built coordinators refuse ambient and per-call acting tokens unless told the transport is safe (#22).
  - **Raw metadata helpers:** in .NET, Python and TS they have no transport check, by design.
- **Admin console.** Unchanged: no refresh token, sessionStorage, strict CSP, no `innerHTML` sinks; `OIDC_ORIGIN` reaches the CSP unvalidated but is built by the chart.
- **Tampering, agent prompt injection (F8).**
  - Retrieved passages can steer answers and tool queries. Tools are retrieval-only under the end user's token, filters are re-validated, the budget is 3 tool calls, and citations are enforced.
  - Echoed query text is now escaped (#23).
  - Terminal output passes through `_printable`, and tracebacks are suppressed (#17).
- **Information disclosure, agent.** Tenant passages, metadata and schema text go to Anthropic, an accepted processor.
- **Information disclosure, tools (F7).**
  - LoadTest and the harness refuse non-loopback, non-https credential endpoints at startup, and LoadTest's plaintext opt-in now applies only to a loopback gRPC URL (#15).
  - Remaining:
    - both tools' token clients still follow redirects; in assertion mode the service-account token is in the POST body;
    - the mint script's `--base-url` is not scheme-checked;
    - `teacher_rerank.py --api-key` takes a key on argv and sends it to any scheme;
    - harness drivers and their build tools (`mvn`, `npm`, `go`, `dotnet`) inherit the harness's full environment, secrets included;
    - the .NET and Java samples opt into insecure credentials unconditionally.
- **Residual:** Low.

### 5.11 Deployment, network and secrets
- **Information disclosure, transport (F2).** Closed by sub-project C: Kestrel TLS on 8080 and 8081, Authentik `:9080` TLS, StarRocks VerifyFull, Authentik → Postgres verify-full, Redis removed, GCP port 80 off. Remaining:
  - **Azure:** edge → pod plaintext until the trusted-root step. An upgrade guard forces the step once internal TLS exists; the first install is the window.
  - **Edge verification:** alb, gce and nginx encrypt to the pod but do not verify its certificate, so the hop resists passive capture but not an active on-path attacker inside the VPC.
  - **StarRocks:** FE still accepts plaintext from permitted peers (every Iverson client requires TLS), and FE ↔ BE traffic, which carries every tenant's projections, is plaintext.
  - **Other plaintext:** Qdrant Raft (vector payloads), TEI and Ollama (tenant text), Jaeger (spans with SQL text, audit lines, relayed browser spans), and Prometheus.
  - **Jobs:** the revoke-cross-db Job connects with `sslmode=prefer`.
  - **Edge → admin-ui:** plaintext, static files.
- **Information disclosure, reach (F6).**
  - Closed:
    - the ingress-nginx peer only on the nginx class (CSR-11 #18);
    - fallback toggles refuse to render without `keepFallbacks`.
  - Remaining, accepted in the transport spec:
    - operator namespaces without NetworkPolicies;
    - the AKS node-subnet fallback, documented with no render gate;
    - no node-level IMDS block on AKS;
    - no explicit etcd KMS on AKS.
- **Information disclosure, secrets.**
  - Helm generates secrets with `keep` and never rotates them; internal CAs are valid ten years.
  - On cloud profiles the blueprint Secret no longer carries a usable client secret.
  - Helm release Secrets still hold every rendered Secret, internal TLS keys included.
- **Tampering, container hardening.**
  - First-party workloads run non-root with `RuntimeDefault` seccomp, all capabilities dropped and no privilege escalation; read-only root filesystems except admin-ui and Authentik.
  - Pod Security Admission enforces `baseline`.
  - StarRocks FE runs as root with no security context in the CR.
- **Residual:** Low (F2, F6).

### 5.12 Supply chain and CI
- **Tampering (F7).**
  - **Pinning:** Dockerfile bases, compose images and GitLab CI images are digest-pinned. Helm third-party images use exact tags tracked by Dependabot. Operator charts are now under Dependabot through the stub; minor and major bumps of the two coupled AWS charts are ignored.
  - **Outside Dependabot:** EKS add-ons, the kind-only Calico, ingress-nginx and metrics-server pins, and GitLab images (bumped by hand).
  - **Publishing (new):** keyless signing and an SPDX attestation per digest, with the OIDC token only in `sign`, tags that never move, and reuse bound to the commit or release ref. Residuals from its review:
    - three consecutive GHCR denials on an existing tag still rebuild and move it;
    - the runbook's operator verify commands use the general identity, which is weaker than the workflow's own check;
    - the release binding names the ref, not the commit;
    - a signature does not bind the image name.
  - **Deploy side:** clusters pull by mutable tag and nothing verifies signatures at admission (accepted in the clients-tooling-CI spec).
  - **Repository:**
    - public, with secret-scanning push protection off;
    - no rulesets, so any writer can create a `v*` tag, which earns a release-identity signature;
    - branch protection keeps admin bypass and no required reviews (accepted), and is live with 11 checks until the D change set is pushed and the script applied;
    - Java and .NET have no lockfiles.
- **Residual:** Low (F7).

## 6. Findings — prioritized roadmap

Ordered by severity × exploitability × blast radius.

| # | Finding | Type | Severity | Rationale |
|---|---|---|---|---|
| F1 | **Namespace RBAC is the root of two privileged Authentik identities.** Anyone who can mint a token for `<rel>-admin-automation` and port-forward obtains a 1-hour Operator token: the pin has no pod claim, and the chart sets no RBAC restricting `serviceaccounts/token`. A pod-bound orchestrator token copied from a pod stays usable up to 600 s after the pod is gone; `iss` is not checked; the orchestrator's global `add_user` can place a user in `operators`. The bootstrap password value stays in the worker environment and Helm release Secrets (inactive `akadmin`), and `AUTHENTIK_SECRET_KEY` never rotates. | **Implemented** | **Low** | Needs a cluster-privileged insider, whose Secret reads already reach the store credentials. Operator gives cross-tenant reach for an hour, so the RBAC is load-bearing. Fix: ship a Role and RoleBinding (or an admission policy) limiting token minting, `pods/exec` and pod creation for these service accounts to named operators; consider a short-lived Job identity with a pod claim for admin-automation; drop the bootstrap password from the worker once break-glass is in place. |
| F2 | **Some hops are still plaintext or unverified.** Azure edge → pod until the trusted-root step (credentials in transit on first install). alb, gce and nginx do not verify the pod certificate. StarRocks FE ↔ BE (every tenant's projections), Qdrant Raft, TEI, Ollama, Jaeger (SQL text, audit lines) and Prometheus are plaintext; the revoke-cross-db Job uses `sslmode=prefer`. The login and admin-api hosts send no HSTS while AWS and Azure redirect port 80. | **Implemented** (largely remediated) | **Low** | Down from Medium. Authentication-carrying hops are TLS on the default path, so passive capture of bearer tokens and passwords is closed. What remains needs an active on-path position in the VPC or cluster, or reaches data rather than credentials. Fix: verify pod certificates at the edge (or a mesh), set `ssl_force_secure_transport` and FE ↔ BE TLS on StarRocks, TLS for Raft and the ML backends, `PGSSLMODE=verify-full` on the Job, and HSTS on every host. |
| F3 | **Store logins can still cross tenants.** StarRocks `iverson_app` holds every tenant role and `OPERATE ON SYSTEM`; isolation depends on `SET ROLE`. The worker-only `iverson_provisioner` holds `user_admin`, `CREATE DATABASE`, every tenant role and `ALTER`. The Postgres app login owns the tables and holds `BYPASSRLS` membership. | **Planned** (a constraint; no bypass known) | **Low** | Down from Medium. The expression parser removed the single point of failure that made a validator bypass a cross-tenant read, and the provisioning rights left the api role. A stolen app credential still reads every tenant. Constraints: a per-tenant or per-request StarRocks credential, a Postgres runtime login that is not the owner and holds no BYPASSRLS membership, and no new cross-tenant uses of either login. |
| F4 | **Revocation and tenant-lifecycle enforcement lag.** Revocation and tenant-status caches are 30 s per replica with local invalidation; `iat` is compared with no skew allowance; suspend and delete write no revocation rows; streams already open finish; the acting-user provider still issues refresh tokens. | **Implemented** | **Low** | A removed user, or a suspended tenant's users, keep access for up to about 30 s per replica, and open streams for their duration. Fix: a shared invalidation signal (or a shorter TTL), a small skew allowance, revocation rows on suspend and delete, and dropping refresh tokens from the acting-user provider if no client needs them. |
| F5 | **Onboarding and login residuals.** Recovery links remain 15-minute single-use bearer credentials for a not-yet-enrolled account. Recovery is not covered by the reputation policy (the edge limit applies). A password login on an account with no device still enrols one first-come, for accounts not onboarded through recovery. Callers entering `:9000` by port-forward set their own recorded address. | **Planned** (inherent to passwordless onboarding; partly accepted in the identity-plane spec) | **Low** | Down in reach: recovery now enrols TOTP before the password is set, superusers are refused publicly, and login is throttled at the edge and per IP. Constraints: keep links out of telemetry and tickets, deliver them only over TLS, and keep enrolment inside recovery. |
| F6 | **Network-policy residuals.** These are:<br>- operator namespaces (`cnpg-system`, `kafka`, `starrocks`, `ingress-nginx` on kind) without NetworkPolicies;<br>- the AKS node-subnet fallback, documented with no render gate;<br>- no node-level IMDS block on AKS;<br>- no explicit etcd KMS on AKS. | **Implemented** (accepted in the transport spec) | **Low** | Each widens what a compromised pod can reach without granting access by itself. Fix: default-deny policies for the operator namespaces; a render gate for the AKS fallback. |
| F7 | **Supply-chain, CI and tooling hygiene (grouped).**<br>- The C and D changes are unpushed: live protection has 11 checks, and no image has been published or signed.<br>- Admin bypass, no required reviews, no rulesets (any writer can create a `v*` tag that is then signed as a release), push protection off on a public repository.<br>- Clusters pull by mutable tag with no admission-time signature check.<br>- Publish residuals: repeated GHCR denials can move a tag; the runbook verify is weaker than the workflow's; the release binding is by ref; signatures do not bind the image name.<br>- EKS add-ons, kind pins and GitLab images outside Dependabot; no Java or .NET lockfiles.<br>- Tooling: redirect-following token clients, an unchecked mint `--base-url`, an argv API key in `teacher_rerank.py`, harness secrets inherited by build tools, samples that opt into insecure credentials. | **Implemented** | **Low** | Each needs a separate compromise, a repository writer, or an operator misstep to matter. Grouped as one finding. The first steps: push, apply the protection script, add a ruleset restricting `v*` tag creation, turn on push protection, and give the runbook's verify commands the same per-tag binding as the workflow. |
| F8 | **Prompt injection through tenant-authored content.** Enrichment output is written back into the entity and re-embedded; its field-permission invariant is checked only at registration, so descriptors registered before CSR-11 #11 are not re-checked on reload, and the SchemaAdmin hint is unescaped. Retrieved passages can steer the agent's answers and tool queries. | **Planned** (monitor) | **Low** | Bounded to the authoring tenant: projections and retrieval are tenant-scoped, and the agent's tools are retrieval-only with a budget. Re-assess if either gains write or cross-tenant action surface. Constraint: re-check the enrichment invariant on rehydration. |

**Closed or reduced since the 2026-10-08 refresh:**

| Previous refresh's finding | Status now |
|---|---|
| F1, pipeline outer-join ownership | **Closed** (A, CSR-11 #1). Derived-table joins, with every join kind and a self-join under test. |
| F2, plaintext authentication-carrying hops | **Reduced to Low** (C, #4, #6). Kestrel, Authentik `:9080`, StarRocks and Authentik → Postgres are TLS; Redis removed; GCP port 80 off. Residuals in the new F2. |
| F3, StarRocks validator plus cross-tenant login | **Reduced to Low** (A, #10). The token scan is replaced by a parser that emits SQL from a tree; provisioning rights moved to a worker-only login. The credential breadth that remains is the new F3. |
| F4, standing Authentik credentials | **Reduced** (B, #9). `akadmin` inactive, break-glass with no standing password, admin-automation federated. The workload-federation residuals are the new F1. |
| F5, recovery links | **Reduced** (B, #5). TOTP enrolled during recovery; superusers refused publicly; login throttled. |
| F6, network-policy residuals | **Reduced** (C, #18). The ingress-nginx peer is class-gated and fallbacks need `keepFallbacks`. The rest is the new F6. |
| F7, supply chain and tooling | **Reduced** (D, #15, #16, #19). Signing and SBOM attestation, digest-pinned GitLab images, operator charts under Dependabot, hashed Python locks, Duende.IdentityModel, tool endpoint checks, password off argv. The rest is the new F7. |
| F8, prompt injection | **Reduced** (A, D, #11, #17, #23). Enrichment field permissions at registration, neutralised agent output, escaped echoes. The rest is the new F8. |
| F9, type-name existence oracle | **Closed for data RPCs** (A, #20). The RegisterSchema answer to a SchemaAdmin is accepted. |

**Also closed from CSR round 11:**
- OneToMany foreign-key identifiers (#2);
- OneToMany hydration on a restricted FK (#3);
- the orchestrator pin's lifetime and pod binding (#7);
- the client-chosen IP recorded by Authentik (#8);
- the template guard (#12, by removal);
- the RemoveUser revocation window (#13);
- the Update existence oracle (#14);
- tenant deletion now terminal and checked on `/v1/traces` and `/admin/dlq*` (#21, partly; the rest is F4);
- the .NET acting-token entry points (#22).

**SQS:** 100 − 2 × 8 (F1–F8; all Low; F7 is already grouped as one) = **84**.

No hard gate triggers:
- **Critical findings:** none.
- **CVEs:** dependency scans cover every ecosystem in CI, and CSR round 11 found no Critical or High CVE. This refresh ran no new scan.
- **Hardcoded credentials:** none live in a deployable profile. The remaining literals are dev-only compose and tool defaults; LoadTest's former tenant-admin default survives in git history and on dev tenants provisioned before round-10 sub-project E, all loopback-only.

**Posture: Acceptable (70–84) by the rubric, one point below Strong.**
- Up from 64. Round 11 closed old F1 and reduced F2 and F3 from Medium to Low, leaving no Medium finding.
- The score describes the repository at `33eb4f1e`. Sub-projects C and D take effect live only after they are pushed and the cloud profiles are upgraded (with Azure's trusted-root step), and the protection script is applied.
- The architectural items that remain before a production cloud deployment are F3 (store credentials that span tenants) and F1 (namespace RBAC as the root of Operator rights).

## 7. When to refresh

Refresh this model:
- **after any change on the security-trigger list:** a new or changed auth/authz model, tenant model or isolation boundary, external integration, class of sensitive data or trust boundary, or a major architectural change;
- **after an incident;**
- **at least quarterly.**

Iverson-specific triggers that should force a refresh:
- **after the C and D change sets are pushed and live:** confirm the first publish and its signatures, apply the branch-protection script, and re-read live protection;
- any change to the Authentik blueprints: providers, grants, scope mappings, groups, MFA, recovery or reputation flows, the federation sources, their pins or marker policies, the superuser stage, or the public listener's path list;
- any change to who can mint tokens for `<rel>-api` or `<rel>-admin-automation`, to namespace RBAC, or to the JWKS relay;
- new uses of `CrossTenantMaintenance`, `BYPASSRLS`, the StarRocks provisioner, or tenant-role grants;
- new client-written expression or query languages, new join kinds, join sources or query builders, or changes to the expression grammar or its function allow-list;
- the agent or enrichment gaining actions beyond retrieval or write-back;
- any change to `clusterCidrs`, `trustedProxies`, the egress fallbacks, the edge TLS settings or the ingress host layout;
- moving any plaintext hop in F2 to TLS, or the reverse;
- adding admission-time signature verification, rulesets, or changes to the publish workflow's permissions, triggers or identity bindings.
