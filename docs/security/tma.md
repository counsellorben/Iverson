# Threat Model Analysis — Iverson

## 1. Header

**Last refreshed:** 2026-10-08 against commit SHA `19ab57c01056d0bbfb3594a53187dcb9406a421c`.
- The previous refresh was dated 2026-10-02, at `a8c48db4`, and was never committed; this document replaces it.
- 140 commits lie between the two. They include all six sub-projects (A, B, C1, C2, D, E) of CSR round 10, `docs/criticalreviews/2026-10-03-iverson-critical-security-review-10.md`.

**Sources:**
- Code: `/home/ben/repositories/Iverson`, the full monorepo:
  - the server runtime, `Iverson.Server/Iverson.{Api,Sql,StarRocks,Vector,Embeddings,Events,Patterns,Launcher}` and the protos in `Iverson.Clients/Common/Proto`;
  - the five SDKs and their conformance drivers, `Iverson.Clients/*`;
  - the reasoning agent, `Iverson.Agents/Python`;
  - the admin console, `Iverson.AdminUI`;
  - the tools, `Iverson.Server/Iverson.LoadTest` and `Iverson.Server/Iverson.ClientConformance`;
  - deployment: `Iverson.Server/docker-compose.yml`, `Iverson.Server/deploy/` (Helm, Authentik blueprints, kind, Terraform), `docs/runbooks/`;
  - CI, `.github/` and `.gitlab-ci.yml`.
- Design inputs: the six round-10 specs in `docs/specs/2026-10-0{3,4,5,6}-csr-round10-*-design.md`, used for their "Known issues" lists. Each claim below was re-checked against the code.
- Reconnaissance: three parallel read-only surface maps, covering (1) the server runtime, (2) clients, console, agent and tools, and (3) deployment, identity and CI. The deployment map rendered the chart for the aws, azure, gcp and local profiles and queried the live branch-protection settings. The synthesis re-read the StarRocks join builders behind F1.

**References:** threats are categorized with this skill's built-in Vulnerability Taxonomy (Appendix A). Findings are scored with its Severity Classification and SQS rubric (Appendix B). Both ship with the `tma` skill and are not tracked separately here.

---

## 2. System overview

Iverson is a multi-tenant .NET 10 gRPC platform for schema-driven object storage, retrieval and search. It sits in front of four stores:
- PostgreSQL, the system of record, with row-level security;
- StarRocks, for analytics, aggregation, pipelines and pattern matching;
- Qdrant, for vector and chunk search;
- Kafka, the outbox fan-out bus.

Around it are five client SDKs, a React admin console, a Python LLM reasoning agent, and Authentik as the single identity provider.

Every tenant's data shares the same database, cluster and vector deployment. Tenants are separated only by software-enforced boundaries, and tenant identity comes from an IdP-issued claim. A failure of identity or isolation is therefore a cross-tenant breach, not a single-customer incident. Inside a tenant, a second boundary separates users: row ownership and field permissions declared per type.

## 3. Architecture & data flows

### 3.1 Components

| Component | Role | Key paths |
|---|---|---|
| `Iverson.Api` | One binary with two roles (`WORKLOAD_ROLE`). `api` serves six gRPC services (23 RPCs) and `/v1/traces`. `worker` runs six Kafka consumer groups and five timer workers. Both roles map the probes, `/admin/*` and `/admin/console/*`, and both run the startup DDL and RLS re-apply. | `Iverson.Api/Program.cs:37-39, 448-470, 673-797` |
| Listeners | `:8080` is h2c: gRPC, gRPC-Web, `/admin/reconcile`, `/admin/dlq*`. `:8081` is HTTP/1.1: `/health`, `/health/live`, `/build`, `/metrics`. `RequireListenerPort` plus `ListenerPortGateAsync` pin by the accepting socket. `/admin/console/*` and `/v1/traces` are unpinned and reach the internet through the admin-api ingress. No TLS in-process. | `Program.cs:897-909`; `charts/api/templates/admin-api-ingress.yaml:57-77` |
| Middleware | Port gate, then the pre-auth limiter (keyed by `ClientPartitionKey`: `X-Forwarded-For` is honoured only from configured proxy CIDRs), then CORS, authentication with revocation, authorization, and the post-auth limiter. gRPC interceptors: rate limit (per `sub`), then acting user. | `Program.cs:497-514`; `RateLimiting/ClientPartitionKey.cs:14-33` |
| `Iverson.Sql` | Dapper and Npgsql. Runtime reads and writes run `SET LOCAL ROLE iverson_runtime` plus `app.tenant_id` under FORCE RLS. Cross-tenant maintenance runs as `iverson_maintenance` (BYPASSRLS). | `IRecordStoreRoles.cs:100-163`; `PostgresSchemaManager.cs:139-292` |
| `Iverson.StarRocks` | Query, pipeline and pattern-row builders. Each tenant gets a database `iverson_tenant_{id}` and a hashed role, entered with `SET ROLE`. Tenant and owner predicates are injected. Client-written expressions are validated and spliced as text. | `StarRocksQueryBuilder.cs`, `StarRocksPipelineBuilder.cs`, `EngagementRepository.cs:216-305, 466-515` |
| `Iverson.Vector` | One collection per tenant, named with a SHA-256 fingerprint. Data-plane calls carry a 30-second HS256 JWT scoped to one collection. Management uses the raw key. TLS with the leaf thumbprint pinned (Helm). | `IntelligenceTenantScope.cs:40-103`; `Vector/ServiceCollectionExtensions.cs:21-64` |
| `Iverson.Patterns` | MatchPattern: an in-process parser and NFA matcher with step, thread, row and time budgets. Never turned into SQL. | `Iverson.Patterns/*`; `PatternBudget.cs` |
| `Iverson.Embeddings` | TEI embeddings and the enrichment LLM (`/v1/chat/completions`). Plaintext and unauthenticated. Responses capped at 1 MiB, with a non-backtracking extraction regex. | `EmbeddingService.cs`; `EnrichmentService.cs:24-131` |
| `Iverson.Events` | Kafka producer and consumer. Events are unsigned JSON carrying the full row and the prior row. DLQ metadata travels in `dlq.*` headers. | `EntityEvent.cs:20-36`; `MessageDispatcher.cs:138-158` |
| `Tenancy/` | Authentik admin client. CA-pinned TLS trust with a startup guard. The orchestrator token exchange (`AuthentikOrchestratorTokenHandler`). Tenant-status and token-revocation caches. | `AuthentikTrust.cs:19-106`; `AuthentikOrchestratorTokenHandler.cs`; `TokenRevocationCache.cs` |
| SDKs (`Iverson.Clients/*`) | Send a service bearer plus `x-acting-user-authorization`. TLS by default. Plaintext needs a named opt-in, which is now also enforced on per-call acting-user bindings (.NET, Python, TS). Token endpoints must be https and are not followed through redirects. | §5.10 |
| `Iverson.AdminUI` | React SPA behind nginx-unprivileged. OIDC code plus PKCE, a public client issued no refresh token, tokens in sessionStorage, strict CSP. JSON `fetch` to the admin-api host; browser spans go to `/v1/traces` with the user's bearer. | `src/auth/AuthProvider.tsx:45-59`; `nginx.conf:32-37`; `src/telemetry.ts` |
| `Iverson.Agents/Python` | A reasoning agent acting as the calling end user. It retrieves tenant passages and sends them to the Anthropic API. | `session.py`, `retrieval.py`, `planner.py` |
| Tools | LoadTest and ClientConformance: harnesses that hold privileged datastore and IdP credentials and seed stores directly. | `Iverson.Server/Iverson.LoadTest`, `Iverson.Server/Iverson.ClientConformance` |
| Authentik 2026.5.3 | The single IdP and JWT issuer (global issuer mode, one signing key). The server pod has three listeners: `:9000` plaintext (loopback and in-cluster), `:8443` TLS (API only), and `:9080`, the filtered public listener. The worker pod carries the `jwks-relay` sidecar. | `charts/authentik/templates/configmap-tls-proxy.yaml:9-37`; `deployment-worker.yaml:32-66` |
| Deployment | Docker compose (dev, loopback-only). kind local and laptop profiles (loopback-only). Cloud on EKS, AKS or GKE: Helm umbrella chart plus Terraform. Default-deny NetworkPolicy in the `iverson` namespace, with scoped DNS and API-server egress. | `docker-compose.yml`; `deploy/helm/iverson`; `deploy/terraform` |

### 3.2 Primary data flows

1. **Write.** The SDK calls gRPC through the edge to `:8080`.
   - The API validates the service JWT and the acting-user JWT, including the revocation check on each, and checks that the acting user's tenant is active (30 s cache).
   - The row and field evaluator runs. It denies by default.
   - The key is server-assigned (UUIDv7).
   - Update never creates a row: a key with no row in the caller's tenant gets `NotFound`. Restricted fields the caller omitted are carried forward from the stored row.
   - The entity and the outbox row are written in one Postgres transaction under RLS, then published to Kafka.
2. **Projection.** Workers consume the event.
   - For Created and Updated, they re-derive tenant and owner from the authoritative Postgres row (maintenance role). Other column values come from the event payload.
   - For Deleted, the tenant comes from the event's pre-delete snapshot.
   - Workers upsert into the tenant's StarRocks database, embed text through TEI into the tenant's Qdrant collection, and optionally send up to 8,000 characters to the enrichment LLM, whose output is written back into the entity and re-published.
3. **Read and search.** The evaluator builds a constraint per type, joined types included.
   - JOIN `ON` columns, filters, sorts and expression tokens are field-checked.
   - StarRocks runs under `SET ROLE` with tenant and owner predicates, and Qdrant with a collection-scoped JWT.
   - Responses strip the tenant column and mask fields to AllowedFields.
4. **Identity provisioning (Helm).**
   - The API reads its projected service-account token (audience `iverson-authentik-orchestrator`, 600 s) and exchanges it at Authentik's token endpoint, over the CA-pinned `:8443` hop, for a 5-minute access token.
   - Authentik checks the token's signature against the cluster's service-account keys, which the worker's `kubectl proxy` relay fetches from the API server. A policy then pins the subject, the audience, and membership of a pre-created user in `iverson-admin-orchestrators`.
   - With that token, CreateTenant and InviteUser create users and return a 15-minute recovery link, rewritten to the public Authentik host.
   - Compose keeps a static, non-expiring token instead.
5. **Human login.** The browser reaches `authentik.<host>` through the edge. The public listener `:9080` forwards only the login, flow, OIDC and static paths, and stamps `X-Iverson-Public-Ingress`. MFA is enforced on interactive login.
6. **Agent.** The agent retrieves under the end user's token and sends the passages and the question to Anthropic. Model-chosen tool inputs come back to Iverson, re-validated against the user's schema.

### 3.3 Trust boundaries

| # | Boundary | What crosses | Enforcement |
|---|---|---|---|
| TB1 | Internet → cloud edge → ingress | Four ingresses:<br>- `<host>`: six gRPC prefixes, plus `/admin` for the console SPA;<br>- `admin-api.<host>`: `/admin*`, anonymous `/health`, `/v1/traces`;<br>- `authentik.<host>`: the `:9080` public listener only. | Edge TLS (ACM or Secret). No source allow-list at the edge. **Edge → pod is plaintext on every ingress.** |
| TB2 | Public listener ↔ Authentik admin plane | Login, flow executor, OIDC (`/application/`, token endpoint included), static | Path allow-list on `:9080`; the admin interface and `/api/v3` answer 404. The marker header lets admin-automation and orchestrator policies refuse public callers (`configmap-tls-proxy.yaml:21-37`). |
| TB3 | SDK or service → API (service identity) | Bearer JWT for one of the configured audiences | `ValidIssuers` (internal and external), audience allow-list, `RequireHttpsMetadata`, CA-pinned JWKS, revocation hook, fallback `RequireAuthenticatedUser`. |
| TB4 | Service identity → end user (acting user) | `x-acting-user-authorization` JWT: `tenant_id`, `groups`, `sub` | A separate scheme sharing the issuer and keys, distinguished by audience. Missing, suspended or deleted tenants are refused. Data RPCs with no acting user are denied by the evaluator. Console HTTP endpoints treat the primary token as the acting user. |
| TB5 | Acting user → tenant data, and user → user within a tenant | Rows, projections, vectors | Evaluator, then Postgres FORCE RLS, StarRocks per-tenant DB and role, and Qdrant per-collection JWT for tenants. Owner predicates and AllowedFields for users. **Each store's connection credential can itself cross tenants** (§5.4). |
| TB6 | Claim → privilege | `groups ∋ operators` or `scope ∋ admin` gives Operator. `groups ∋ operators` or `scope ∋ schema_admin` gives SchemaAdmin. `groups ∋ tenant-admins` gives TenantAdmin. | Claims come from Authentik groups and scope mappings. `tenant_id` comes from a user attribute users cannot write (verified live 2026-10-02). |
| TB7 | API → Authentik admin plane | User creation, recovery links, group PATCHes | TLS to `:8443` with a chart CA. 5-minute federated token on Helm; static token on compose. |
| TB8 | Kubernetes RBAC → Authentik orchestrator rights (new) | Projected tokens for service account `<rel>-api` with the orchestrator audience | The policy pins `sub`, `aud` and the workload user's group. Signature is checked against the cluster's own keys; `iss` is not checked. Anyone allowed to mint such a token (TokenRequest, or creating a pod with that account) holds orchestrator rights for 5 minutes. |
| TB9 | API and worker ↔ data stores | Tenant records and credentials | Postgres VerifyFull, Kafka TLS plus SCRAM plus ACLs, Qdrant TLS (pinned). **StarRocks, Redis, TEI, Ollama, Jaeger, Prometheus and Qdrant Raft are plaintext.** |
| TB10 | Worker ↔ ML backends | Tenant text out, model output back into tenant data | No auth or TLS. NetworkPolicy limits who reaches them; model pods have no egress. |
| TB11 | Agent ↔ Anthropic, and retrieved content ↔ LLM instructions | Tenant passages, questions, model-chosen tool calls | Escaping and delimiters, schema-validated filters, a tool-call budget, citation enforcement. |
| TB12 | Load-balancer ranges and peers ↔ pods | Everything in TB1 minus the edge TLS | `clusterCidrs` narrowed to the LB subnets (AWS public subnets, Azure AGIC subnet, GCP GFE ranges) plus ingress-nginx (`networkpolicies.yaml:45-62`). Operator namespaces have no NetworkPolicies. |
| TB13 | Developer host ↔ compose and kind | Dev stacks with test identities | Compose and kind both bind `127.0.0.1` (`kind-config.yaml:17-24`). |
| TB14 | CI and contributors ↔ `main`; third-party actions, tools, images | Code, build and scan steps | Branch protection with 11 required checks. Every action SHA-pinned; tool downloads hash-checked; read-only `GITHUB_TOKEN`. |

## 4. Threat actors

| Actor | Capabilities | Motivation | In scope? |
|---|---|---|---|
| External opportunistic | Scanning, credential stuffing against Authentik's public login, floods, known-CVE exploitation | Opportunistic compromise, resource abuse | **Yes.** The API, the console and Authentik's login are internet-facing on cloud profiles. |
| External targeted | Reconnaissance of the public IdP, probing tenant isolation and expression parsers, phishing operators, tenant admins or invitees | Data from a specific tenant, platform-wide compromise | **Yes** |
| Insider, regular tenant user | Tenant-scoped CRUD, search and pipelines with an acting-user token; authors documents that later feed the LLM | Reading other users' private rows in the same tenant; escalating to another tenant or to admin | **Yes.** The central actor. |
| Insider, privileged (Operator, SchemaAdmin, TenantAdmin) | Tenant lifecycle, schema DDL, DLQ replay, cross-tenant reconcile, user invites | Abuse, or a stolen privileged credential | **Yes** |
| Cluster-privileged insider | Kubernetes RBAC in the `iverson` namespace: reads Secrets and Helm release Secrets, creates pods, mints service-account tokens | Credential theft; IdP control through TB8 | **Yes.** Equivalent in reach to the Secret reads the static token was exposed to before C2. |
| Network-positioned in-cluster or VPC attacker | A compromised pod, node or VM on the paths between the edge, the pods and the stores | Sniff or tamper with plaintext hops | **Yes.** Every edge → pod hop and several store hops are plaintext (§5.11). |
| Compromised supply chain | Malicious dependency, action, base image, operator chart or model weight | Code execution at build or run time | **Yes.** Five language ecosystems plus third-party images and operators. |
| Automated bots | High-volume requests | Resource exhaustion, cost inflation | **Yes** |
| LAN peer of a developer | Reaches a dev stack on the local network | Opportunistic access to test identities | **No longer.** kind now binds loopback, like compose. |
| Nation-state | Zero-days, persistent access | Espionage | **No.** Nothing indicates Iverson is critical infrastructure. |

## 5. Threat analysis (STRIDE-per-element)

### 5.1 Listeners, ingress and rate limiting
- **Spoofing.** The listener partition keys on the accepting socket's port, not on `Host`.
- **Denial of service.** Closed since the last refresh: the pre-auth limiter no longer collapses every client behind the ingress into one bucket (CSR-10 #3).
  - It keys on `ClientPartitionKey`, which trusts `X-Forwarded-For` only when the peer is a configured proxy CIDR, at a configured hop count (`ClientPartitionKey.cs:14-33`, `TrustedProxyOptions.cs:20-35`).
  - Each profile sets its proxy ranges, and the chart refuses an empty list (`charts/api/templates/deployment.yaml:1-8`).
  - The post-auth limiter, the gRPC per-`sub` limit, per-request query caps, MatchPattern budgets and the 4 MB receive cap stay in place.
  - Residuals:
    - A host inside a trusted LB subnet can choose its own bucket (accepted in the edge-transport spec).
    - Tokens without a `sub` share one gRPC rate bucket (`RateLimitInterceptor.cs:41`). Authentik always issues `sub`.
- **Information disclosure.** Anonymous `/health` (store up/down) is published on `admin-api.<host>`. `/build` (assembly hashes) and `/metrics` are reachable only from the LB ranges and Prometheus.
- **Elevation of privilege.** `/admin/console/*` and `/v1/traces` are unpinned and authenticated.
  - `/v1/traces` now admits only console-audience tokens (CSR-10 #13). Any console user can still write arbitrary spans to Jaeger; that is accepted in the server-authorization spec.
- **Residual:** Low.

### 5.2 Identity: Authentik, the two JWT schemes, acting users and revocation
- **Spoofing, closed.** The JWKS and discovery hop is now TLS to `:8443`, with a chart CA, `RequireHttpsMetadata = true` on both schemes, and a startup guard refusing http outside Development (`Program.cs:177-203`, `AuthentikTrust.cs:69-106`). An in-cluster attacker can no longer substitute signing keys. Refresh on an unknown `kid` stays on, but it can only fetch genuine keys.
- **Spoofing, by design.** Both schemes trust one issuer set and one key set and differ only by audience. Privilege still needs `groups` or `scope`, and data access still needs a valid acting user.
- **Spoofing, verified mitigated.** Users cannot rewrite their own `tenant_id`: a live probe on 2026-10-02 covered the self-service flow and the admin API. Self-service is now also off the public listener.
- **Elevation of privilege, largely closed.** CSR-10 #4 is addressed on Helm:
  - Authentik's admin interface and `/api/v3` no longer reach the internet (TB2).
  - The bootstrap token is removed (`state: absent`).
  - The admin-automation client is refused on public requests.
  - The static orchestrator token is replaced by the 5-minute federated token (TB8).

  Residuals (F4):
  - **The `akadmin` superuser password.** Random and kept forever with no rotation. It can log in through the public flow; MFA is enrolled at first login.
  - **The admin-automation client secret.** It mints Operator tokens from anywhere inside the cluster or VPC that reaches `:8443` or `:9000`.
  - **Compose's static orchestrator and bootstrap tokens.** Dev, loopback-only.
- **Elevation of privilege, residual by permission model.** The orchestrator role's global `add_user` can create a user directly into any non-superuser group, `operators` included. Authentik 2026.5.3 has no group-scoped `add_user`. Since C2 this needs the API pod's identity (TB8), whose compromise already implies cross-tenant access.
- **Spoofing (new boundary).** TB8 turns Kubernetes RBAC on `<rel>-api` into Authentik orchestrator rights. Authentik does not check `iss`. It accepts only signatures from the cluster's own service-account keys, which the relay fetches through an allow-listed read-only path (`--accept-paths=^/openid/v1/jwks$`, mutating methods rejected, empty `--www`). Two items are accepted as unverified in the C2 spec:
  - whether the managed clusters publish a new key before signing with it;
  - whether their API servers serve the projected-token keys.
- **Elevation of privilege, revocation (new control).** `RemoveUser` and demotion write revocation rows, checked in `OnTokenValidated` on both schemes (`Program.cs:882-895`), with a 30 s cache that fails closed. Gaps:
  - A token with no `sub` skips the lookup.
  - Suspending or deleting a tenant writes no rows, relying instead on the tenant-status checks. Those cover the acting-user path, TenantAdmin and four of five console readers; `/admin/console/metrics` is Operator-only and has none.
  - The production acting-user provider still issues refresh tokens (`secret-service-clients.yaml:386`). A refreshed access token carries a fresh `iat`, so it outlives a demotion's revocation row. Its claims are recomputed at refresh.
- **Repudiation.** Policy denials and admin operations are audit-logged with the service and acting `sub`.
  - The public listener does not normalize `X-Forwarded-For`, and Authentik trusts the header from loopback. Client-supplied entries can therefore reach Authentik's event log; which entry Authentik records is unverified.
- **Residual:** Low (F4, F5).

### 5.3 Row and field authorization evaluator
- **Elevation of privilege.** Denial is the default. It applies when a type has no rules, there is no acting user, the type has no tenant column, the token has no `tenant_id`, or there is no owner field for a caller without a bypass role.
- **Elevation of privilege, closed since the last refresh (CSR-10 sub-project A):**
  - JOIN `ON` columns are field-authorized against each side's own constraint (`StarRocksQueryBuilder.cs:867-902`).
  - Update never creates a row and returns `NotFound` after the denial check. That closes the cross-tenant existence oracle and the Update-as-create path.
  - Omitted restricted fields are carried forward rather than cleared, and the Mapping Update response strips the carried keys.
- **Elevation of privilege, intra-tenant, open (F1).** In both builders, a joined type's owner and tenant predicates sit in the JOIN's `ON` clause, so that LEFT joins are not collapsed to INNER joins.
  - **Search.** The primary type's tenant predicate stays in the outer `WHERE`, which drops the rows a RIGHT or FULL join would preserve.
  - **Pipeline.** A non-aggregate step that outer-joins a fresh row-owned type has only the caller's own filters in its outer `WHERE` (`StarRocksPipelineBuilder.cs:640-680`). Rows of the joined type that the caller does not own are therefore not removed by anything.
  - Tenant isolation is unaffected: the join target is qualified to the caller's tenant database.
  - Field masking still applies.
  - This was established by reading; no test pins outer-join ownership in the pipeline builder.
- **Tampering.** `RelationValidator` checks only the shape of a foreign key. Relation reads are tenant-scoped, so a key into another tenant resolves to nothing.
- **Information disclosure.** Document templates are guarded at registration: no restricted properties, no relations to row-owned types. The renderer reads related rows tenant-scoped, without a runtime evaluator (`DocumentRenderer.cs:100-160`). CSR-10 #2 was refuted on that basis.
- **Residual:** Medium (F1).

### 5.4 Data stores and tenant isolation
- **Information disclosure, Postgres.** FORCE RLS with a per-transaction role and GUC; `ForTenant(null)` fails closed. The app login `iverson` owns the database and is a member of `iverson_maintenance BYPASSRLS` (`charts/postgres/templates/cluster.yaml:43-47`). RLS therefore guards against query bugs, not a compromised app. Compose runs the app as superuser.
- **Information disclosure, StarRocks.** Each tenant has its own database and role. The single app login `iverson_app` also holds `user_admin`, `CREATE DATABASE` and `OPERATE ON SYSTEM`, and every tenant role it grants itself (`job-create-user.yaml:68-77`, `EngagementRepository.cs:481-500`). Its transport is plaintext MySQL protocol.
- **Information disclosure, Qdrant.** Data calls use 30-second JWTs scoped to one collection. The raw key is used for management and the console listing.
- **Tampering and injection.**
  - Values are always parameters.
  - Identifiers are interpolated behind `^[A-Za-z][A-Za-z0-9]*$`, re-validated on rehydration.
  - Client-written aggregation, metric and derive expressions are spliced into StarRocks SQL after a character denylist (now also `"` and `\`) and a token allow-list. Function names count only when followed by `(`, so a same-named column is field-checked (`StarRocksPipelineBuilder.cs:23-53, 395-409`).
  - The parser and the credential split, CSR-10 #7's primary and architectural remediations, are deferred. One validator bypass would run as a login that can cross tenants and administer users (F3).
- **Residual:** Medium (F3).

### 5.5 Schema registry and DDL (RegisterSchema)
- **Tampering.** SchemaAdmin defines tables, columns, rules, templates and enrichment hints.
  - Re-registering drops undeclared columns after the drift check, so it is destructive by design.
  - Re-registration across owners is denied (`SchemaRegistrationOrchestrator.cs:78-81`).
  - A SchemaAdmin call with no acting user registers an ownerless, global type.
- **Information disclosure.** Type names share one platform-wide namespace, so `RequireSchema` tells a foreign name apart from one never registered (F9, accepted).
- **Elevation of privilege.** SchemaAdmin and Operator can be satisfied by an OAuth scope on a client-credentials token (the admin-automation client). That client is refused on public requests.
- **Residual:** Low.

### 5.6 Query languages: search, pipeline and MatchPattern
- **Tampering and injection.** See §5.4 for the expression path. MatchPattern patterns and expressions are interpreted in-process and never become SQL text.
- **Elevation of privilege.** See §5.3 for outer joins in pipeline steps (F1).
- **Denial of service.** MatchPattern is bounded by steps, threads, rows and a 30 s timeout. Length caps bound parser recursion. Similarity terms are capped per request.
- **Residual:** Medium, through F1 and F3.

### 5.7 Kafka, consumers and the DLQ
- **Tampering.**
  - Events are unsigned. Created and Updated re-derive tenant and owner from Postgres, but other projected columns come from the event payload.
  - Deleted events trust the tenant in the snapshot.
  - DLQ tenant attribution is parsed from the payload, and replay produces to the `SourceTopic` header without an allow-list.
  - Every path needs Kafka write access: SCRAM plus prefix ACLs, one principal shared by the api and worker.
- **Information disclosure.** DLQ rows and headers can embed SQL text, Authentik error bodies and LLM output. `/admin/dlq` serves `ExceptionMessage` (not `MessageValue`) to Operators, filtered to the caller's tenant.
- **Residual:** Low. Each path presumes a compromised Kafka credential, and Postgres overrides tampered projections on the next reconcile.

### 5.8 ML backends and enrichment
- **Information disclosure.** Tenant text reaches TEI and Ollama over plaintext with no authentication. Model pods now have no egress at all: weights are baked into the images (`networkpolicies.yaml:427-428, 452-453`).
- **Tampering, prompt injection into persisted data.** A tenant document can steer the enrichment LLM, whose output is written back into that entity and re-embedded. The blast radius is that tenant's own data (F8). The schema-admin-controlled extraction hint sits outside the escaped markers.
- **Denial of service, closed.** The quadratic fence regex is gone, and responses are capped at 1 MiB under one deadline (CSR-10 #21).
- **Supply chain.**
  - TEI weights are pinned to a revision SHA.
  - The Ollama build compares the full SHA-256 of the pulled manifest; the image tag carries a 12-character prefix.
  - Compose still pulls floating models at runtime.
- **Residual:** Low.

### 5.9 Admin plane: `/admin/*`, the console and the tenant services
- **Elevation of privilege.**
  - `/admin/reconcile` and `/admin/dlq*` need Operator on both the primary and the acting principal. They are pinned to `:8080`, so they are not reachable through the admin-api ingress.
  - Tenant lifecycle requires Operator.
  - TenantAdmin takes the tenant from the primary principal, requires it to be active, and checks the target user against it.
  - CreateTenant and InviteUser return 15-minute recovery links (F5).
- **Information disclosure.** The console readers check tenant status (new), and evaluate the console token as the acting user. `ListUsersByTenantAsync` pages every Authentik user into API memory before filtering by tenant.
- **Residual:** Low.

### 5.10 Clients: SDKs, admin console and reasoning agent
- **Spoofing and information disclosure.**
  - **SDKs:** TLS by default, with plaintext needing a named opt-in. The per-call acting-user binding is now checked against that guard in .NET, Python and TS (CSR-10 #24). Go and Java rely on their credential-level guards. The raw acting-user metadata helpers in .NET, Python and TS have no transport check, by design.
  - **Admin console:** no refresh token at the provider (CSR-10 #12), tokens in sessionStorage, strict CSP with no `innerHTML` sinks. Node 22 and nginx 1.30.5, digest-pinned. `OIDC_ORIGIN` reaches the CSP header unvalidated, but it is derived from the validated `ingressHost`.
- **Tampering, agent prompt injection.** Retrieved passages could steer the agent. Its tools are retrieval-only, filters are re-validated, the budget is 3 tool calls, and citations are enforced. Model-chosen `query_text` is echoed outside the escaped `<doc>` boundary (`session.py:226, 241`) (F8).
- **Information disclosure, agent.** Tenant passages go to Anthropic, an accepted processor. The CLI now opts into insecure credentials only when every plaintext leg is loopback.
- **Information disclosure, tools.** Closed:
  - LoadTest requires the tenant-admin password and logs in at once, enrolling TOTP.
  - TOTP caches are 0600 in a 0700 directory.
  - The conformance harness passes secrets in the environment, not on argv.

  Remaining (F7):
  - LoadTest sets `allowInsecureChannelCallCredentials: true` unconditionally (`LoadTest/Program.cs:187`).
  - LoadTest sends its Operator-level admin-automation token as raw metadata to whatever `IVERSON_GRPC_URL` names, minted by a client that follows redirects and accepts http (`:389-424`; the harness repeats this at `TokenBroker.cs:178-194`).
  - `scripts/pattern_leg.py:668-669` still passes a password on argv.
  - The .NET and Java samples opt into insecure credentials unconditionally.
- **Residual:** Low.

### 5.11 Deployment, network and secrets
- **Information disclosure, in-cluster transport (F2).** API ↔ Authentik is now TLS. Still plaintext:
  - every edge → pod hop: gRPC bearer and acting-user tokens, console tokens, and on Authentik's `:9080` the passwords, TOTP codes, recovery flow tokens and authorization codes;
  - StarRocks, including the root password the bootstrap job sends;
  - Redis, TEI, Ollama, Jaeger and Prometheus;
  - Qdrant Raft;
  - Authentik → Postgres (sslmode unset).
- **Information disclosure, reach.** Closed (CSR-10 #15, #17):
  - `clusterCidrs` is narrowed to the LB ranges.
  - DNS egress is scoped to kube-dns, and API-server egress to `apiServerCidrs` for CNPG, Strimzi and the Authentik worker only.
  - EKS IMDS hop limit is 1; GKE uses `GKE_METADATA`.

  Remaining (F6):
  - The first-deploy fallback toggles (`dnsAnyDestination`, `apiServerAnyDestination`) restore any-destination rules.
  - The documented AKS fallback adds the node subnet, which Azure CNI shares with pods, and so admits every pod.
  - The operator namespaces (`cnpg-system`, `kafka`, `starrocks`, `ingress-nginx`) have no NetworkPolicies.
  - AKS has no node-level IMDS block.
- **Information disclosure, secrets.**
  - Helm generates secrets with `keep` and never rotates them.
  - The credential-bearing blueprint Secret, and the Helm release Secrets that embed it, hold every client secret, so reading Secrets in the namespace exposes them.
  - EKS and GKE use KMS envelope encryption; AKS has no explicit etcd KMS configuration in Terraform.
  - Closed:
    - The root `.dockerignore` excludes `**/.env*`, and the API Dockerfile copies named projects only (CSR-10 #19).
    - AKS local accounts are disabled, so no cluster-admin certificate sits in Terraform state, and the Azure state account rejects shared keys (CSR-10 #6).
- **Tampering, container hardening.**
  - First-party workloads run non-root with `RuntimeDefault` seccomp, all capabilities dropped and no privilege escalation.
  - All have a read-only root filesystem except admin-ui and Authentik.
  - `automountServiceAccountToken` is off everywhere except the two deliberate projected tokens.
  - Pod Security Admission enforces `baseline`.
  - StarRocks, CNPG and Strimzi pods take operator defaults; the StarRocks CR sets no security context.
- **Elevation of privilege, dev stacks.**
  - Compose and kind bind loopback.
  - kind's ingress-nginx enables snippet annotations at the Critical risk level (`setup.sh:110-118`). `setup.ps1` omits both those flags and the TLS certificate step.
  - Dev tenants provisioned before sub-project E keep LoadTest's former public default tenant-admin password, because a changed variable does not reset an existing tenant (E spec, Known issues).
- **Residual:** Medium (F2). The rest is Low.

### 5.12 Supply chain and CI
- **Tampering.**
  - **Pinning:** every Dockerfile base and compose image is digest-pinned. Helm third-party images use exact tags so that Dependabot can track them. CNPG and Strimzi workload images come from operator defaults.
  - **Actions:** every `uses:` is SHA-pinned, and tool downloads are hash-checked. GitLab CI images use floating tags.
  - **Dependabot:** covers 14 entries across nuget, npm, gomod, maven, pip, actions, docker, compose, terraform and helm. It does not cover operator chart versions in Terraform or setup scripts, or EKS add-on versions.
  - **Scanning:**
    - Dependency scans across six ecosystems.
    - CodeQL in six languages.
    - Trivy: first-party images fail on fixable HIGH and CRITICAL findings; third-party images are report-only.
    - kube-score, tfsec, kubeconform and `helm lint`.
  - **Images:** no registry publish, signing, SBOM or provenance for first-party or model images, which are built and pushed by hand.
  - **Branch protection:** 11 required checks, but no required reviews, admins exempt, and govulncheck, OWASP, pip-audit, image-scan and deploy-validate not required. The repository is public with secret-scanning push protection off.
  - **Lockfiles:** the Python SDK and the agent have none. The .NET SDK uses the archived `IdentityModel` 7.0.0.
  - **Git history:** the removed `.pyc` keeps LoadTest's former default password in history.
- **Residual:** Low (F7).

## 6. Findings — prioritized roadmap

Ordered by severity × exploitability × blast radius.

| # | Finding | Type | Severity | Rationale |
|---|---|---|---|---|
| F1 | **Owner scoping is lost on outer joins in pipeline steps.** A non-aggregate pipeline step that RIGHT- or FULL-joins a fresh row-owned type places that type's owner predicate only in the `ON` clause. Its outer `WHERE` holds only the caller's own filters, so the outer join keeps joined rows the caller does not own (`StarRocksPipelineBuilder.cs:640-680`). The Search builder is not affected: its primary-type tenant predicate in `WHERE` removes those rows. | **Implemented** (by code reading; confirm in CSR) | **Medium** | A regular tenant user could read other users' rows of a row-owned type in the same tenant. Row ownership is the platform's per-user privacy boundary. Tenant isolation holds (the join target is the caller's tenant database), and field masking still applies, which keeps this at Medium. Re-score if confirmed against owner-private data. Fix: enforce the joined type's ownership on the rows each join kind preserves (for example, filter the joined source in a derived table before joining), and pin every join kind with tests in both builders. |
| F2 | **Authentication-carrying hops are still plaintext.** Every edge → pod hop carries bearer tokens, acting-user tokens and console tokens. Authentik's `:9080` hop also carries passwords, TOTP codes, recovery flow tokens and authorization codes. StarRocks carries the `iverson_app` password, the bootstrap root password and all tenants' projections. Redis, TEI, Ollama, Jaeger, Prometheus and Qdrant Raft are plaintext too. | **Implemented** (documented choice, partly remediated) | **Medium** | Needs network position inside the VPC or cluster (§4). The worst case was closed: the JWKS hop is now TLS, so keys cannot be substituted to forge identities. What remains is credential capture. A captured Operator token gives cross-tenant reach for its hour of life, and the StarRocks password gives every tenant's projections. Fix: TLS or a mesh on the edge → pod hops (Authentik first), then StarRocks `SslMode`. |
| F3 | **StarRocks tenant isolation rests on validating client-written SQL expressions, run by a session that can cross tenants.** Aggregation, metric and derive expressions are spliced as text after a denylist and a token allow-list. They run as `iverson_app`, which holds `user_admin` and every tenant role. The Postgres app login likewise holds `BYPASSRLS` membership. | **Planned** (a constraint; no bypass known) | **Medium** | No injection is known, and the round-10 interim patch narrowed the validator. One bypass would still become cross-tenant reads and StarRocks user administration, so the validator is a single point of failure for isolation. Constraints: replace the token scan with a real expression parser (CSR-10 #7's deferred primary remediation), split the provisioning credential (`user_admin`, `CREATE DATABASE`) from the query credential, and limit the query login to the active tenant's role. |
| F4 | **Standing Authentik credentials remain.** These are:<br>- the `akadmin` superuser password: random, kept forever, never rotated, and usable through the public login flow, where MFA is enrolled on first login;<br>- the admin-automation client secret, which mints 1-hour Operator tokens from anywhere that reaches `:8443` or `:9000`;<br>- compose's static, non-expiring orchestrator token and bootstrap token.<br>All Helm client secrets are also readable from the blueprint Secret and the Helm release Secrets. | **Implemented** | **Low** | Down from Medium. The admin interface and admin API are no longer public, the bootstrap token and the static Helm orchestrator token are gone, and admin-automation is refused on public requests. Exploitation now needs a leaked Secret and, for admin-automation, a position inside the cluster or VPC. A public `akadmin` session has no Operator group and no admin paths. Fix: rotate or disable the `akadmin` password after bootstrap, give admin-automation a federated or short-lived credential like C2's, and move the compose orchestrator to an expiring token if compose ever leaves loopback. |
| F5 | **Recovery links are MFA-free bearer credentials, and so is the session they open.** CreateTenant and InviteUser return links, now valid for 15 minutes and pointing at the public host. The recovery flow signs the invitee in, so console sign-ins in that browser need no second factor until the Authentik session ends. | **Planned** (inherent to passwordless onboarding; accepted in the C1 spec) | **Low** | Takeover of a not-yet-onboarded account, or a tenant's user management if the invitee is a tenant admin. The link crosses the plaintext edge → pod hop (F2). It is single-use and short-lived, and never logged. Constraint: keep links out of telemetry and support tickets, deliver them only over TLS, and keep the expiry short. |
| F6 | **Network-policy residuals.** These are:<br>- operator namespaces (`cnpg-system`, `kafka`, `starrocks`, `ingress-nginx`) without NetworkPolicies;<br>- first-deploy fallback toggles that restore any-destination DNS and API-server egress;<br>- the AKS fallback that admits the node subnet, shared with every pod;<br>- no node-level IMDS block on AKS. | **Implemented** | **Low** | Each widens what a compromised pod can reach without granting access by itself. The narrowing from round 10 is real on the default path. Fix: default-deny policies for the operator namespaces, and a runbook gate that refuses to finish a deploy with a fallback toggle still on. |
| F7 | **Supply-chain, CI and tooling hygiene (grouped).**<br>- No image registry publish, signing, SBOM or provenance.<br>- GitLab CI images use floating tags.<br>- Operator charts and EKS add-ons are outside Dependabot.<br>- Branch protection has no required reviews, exempts admins, and leaves several security scans optional, on a public repository with push protection off.<br>- No lockfiles for the Python SDK and agent; legacy `IdentityModel`.<br>- LoadTest waives the SDK transport guards and mints its Operator-level token through a redirect-following, http-accepting client.<br>- `pattern_leg.py` passes a password on argv. | **Implemented** | **Low** | Each needs a separate compromise or an operator misconfiguration to matter. Grouped as one finding. |
| F8 | **Prompt injection through tenant-authored content.** Enrichment output is written back into the entity and re-embedded. Retrieved passages can steer the agent's answers and tool queries, and model-chosen query text is echoed unescaped. | **Planned** (monitor) | **Low** | Bounded to the authoring tenant: projections and retrieval are tenant-scoped, and the agent's tools are retrieval-only with a budget. Re-assess if either gains write or cross-tenant action surface. |
| F9 | **Type-name existence oracle.** `RequireSchema` distinguishes a name another tenant registered from one never registered, because the type namespace is platform-wide. | **Planned** (accepted in its design spec) | **Low** | Discloses type names only, never data. The closing mechanism (`IsShared` opt-in) remains deferred. |

**Closed or reduced since the 2026-10-02 refresh:**

| Previous refresh's finding | Status now |
|---|---|
| F1, long-lived MFA-exempt Authentik tokens usable from the internet | **Reduced to Low (now F4).** Public listener, bootstrap token removed, admin-automation in-cluster only, orchestrator federated (C1, C2). |
| F2, plaintext hops including the JWKS hop | **Reduced (now F2).** The Authentik hop, which carried JWKS, discovery and the admin API, is TLS. Identity forgery by key substitution is closed. |
| F4, VPC-wide reach | **Closed.** LB-range `clusterCidrs`, scoped DNS and API-server egress, no model egress, IMDS hop limit 1 (D). Residuals are in F6. |
| F5, recovery links | **Reduced.** 15 minutes, public host, the flow ends at the console (C1). |
| F6, kind on the LAN plus LoadTest's default password | **Closed.** kind binds loopback; the password is required and the first login is immediate (D, E). |
| F8, supply chain | **Reduced (now F7).** `.env` excluded from the build context, all actions SHA-pinned, Trivy, Dependabot for docker, compose, terraform and helm, Node 22 console, full-digest Ollama check, `setup.ps1` pins (D). |

**Closed from the CSR-10 round** (net-new there, not in the previous TMA):
- JOIN `ON` field authorization (#1);
- the restricted-field clearing on Update (#8);
- Update-as-create and the existence oracle (#10);
- revocation on removal and demotion (#11);
- console refresh tokens (#12);
- `/v1/traces` restricted to console tokens (#13);
- the console tenant-status check (#14);
- the rate-limiter key behind the proxy (#3);
- the AKS certificate in state (#6);
- the enrichment regex (#21).

**SQS:** 100 − 8 × 3 (F1–F3; Medium) − 2 × 6 (F4–F9; Low; F7 is already grouped as one) = **64**.

No hard gate triggers:
- **Critical findings:** none.
- **CVEs:** dependency scans cover every ecosystem in CI, and CSR-10 found no Critical or High CVE. This refresh ran no new scan.
- **Hardcoded credentials:** none live in a deployable profile. The remaining literals are dev-only compose and tool defaults. LoadTest's former tenant-admin default survives in git history and on dev tenants provisioned before sub-project E; both are loopback-only.

**Posture: Unacceptable by the rubric (< 70).**
- The score matches the previous refresh's 64, but it is made up differently. Round 10 closed or reduced six of the previous nine findings, and the old F1 fell from Medium to Low. The new F1 is an intra-tenant ownership gap this refresh found by reading the join builders.
- Fixing F1, a contained builder change, brings the score to 72 (Acceptable with a remediation commitment).
- F2 and F3 are the architectural items that remain before a production cloud deployment.

## 7. When to refresh

Refresh this model:
- **after any change on the security-trigger list:** a new or changed auth/authz model, tenant model or isolation boundary, external integration, class of sensitive data or trust boundary, or a major architectural change;
- **after an incident;**
- **at least quarterly.**

Iverson-specific triggers that should force a refresh:
- any change to the Authentik blueprints: providers, grants, scope mappings, groups, MFA or recovery flows, the federation source, its pin or marker policies, or the public listener's path list;
- any change to who can mint tokens for the API's service account, or to the JWKS relay;
- new uses of `CrossTenantMaintenance`, `BYPASSRLS` or the StarRocks `user_admin` path;
- new client-written expression or query languages, or new join kinds, join sources or query builders;
- the agent or enrichment gaining actions beyond retrieval or write-back;
- any change to `clusterCidrs`, `trustedProxies`, the egress fallbacks or the ingress host layout;
- moving any plaintext hop in F2 to TLS, or the reverse.
