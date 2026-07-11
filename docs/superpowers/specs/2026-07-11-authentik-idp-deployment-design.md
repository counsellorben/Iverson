# Authentik Identity Provider Deployment Design

**Date:** 2026-07-11
**Origin:** Follow-up to the event-ordering-and-observability work — Iverson currently has no
authentication anywhere (confirmed via codebase grep: no `[Authorize]`, no JWT/OIDC code,
nothing), and the three `/admin/*` endpoints (`reconcile`, `dlq` list, `dlq replay`) are
completely open on the network.

## The bigger picture: a 5-part initiative, this spec is Part 1 only

Adding identity management to Iverson decomposes into 5 sequential sub-projects, each depending
on the one before it:

1. **IdP deployment** (this spec) — stand up Authentik as infrastructure. No Iverson application
   code changes.
2. **Admin endpoint protection** — gate `/admin/*` behind OIDC token validation; a human path
   (SAML-derived login + operator role, MFA-enforced) and an automation path (service-account
   client-credentials token, for CI/runbook tooling).
3. **Client API service authentication** — a gRPC interceptor validating a Bearer token per
   *calling service* (OAuth2 Client Credentials), so every call to Search/Post/Update/etc.
   requires a known, authenticated caller.
4. **End-user identity propagation** — define how a calling service forwards the acting human
   user's identity to Iverson (token exchange vs. pass-through), and extend the proto contract +
   all 5 client SDKs to carry it.
5. **Row/field-level authorization** — the large one: schema registration gains the ability to
   express authorization rules (e.g. an owner field, role→permission mapping), and every query
   path (StarRocks filter injection, Qdrant payload filters, Postgres write validation) enforces
   them. Comparable in size to the Pipeline CTE DSL or vector-search DSL projects already
   completed in this repo — its own multi-plan effort, not attempted here.

**This spec covers Part 1 only.** Parts 2-5 are out of scope and will each get their own
brainstorm → spec → plan cycle once Part 1 ships. Nothing in this design should assume any
Iverson-side enforcement exists yet — Authentik simply needs to exist, be reachable, and be
capable of SAML/OAuth2(+PKCE)/MFA, ready for Part 2/3 to point at.

## Decisions carried in from brainstorming

- **Authentik is the standalone source of truth for accounts** (local users + MFA). SAML is
  exposed as a protocol Authentik can act as IdP for, in case a downstream app needs to SSO into
  it later — there is no existing external corporate IdP (Okta/Entra ID/etc.) to broker from
  today.
- **Full deployment-target parity** with every other component in this repo: docker-compose
  (local dev), `deploy/kind/`, and all 3 cloud values overlays via the umbrella Helm chart.
- **MFA: TOTP + WebAuthn/passkeys only.** No SMS/email OTP (weaker, and SMS costs money per
  message).
- **Config as code**: Authentik's flows/providers are defined via its native **Blueprints**
  feature (YAML, mounted and auto-applied on startup) — not manual admin-UI click-ops — matching
  this repo's IaC discipline everywhere else (Terraform + Helm values, nothing hand-configured).
- **Hand-rolled subchart, not the upstream Authentik Helm chart.** This repo's established
  pattern: hand-roll a minimal `Deployment`/`Service`/`ConfigMap` subchart (Jaeger, Prometheus)
  for anything that doesn't need real clustering; reach for a full upstream operator (CNPG,
  Strimzi, StarRocks-operator) only when the system genuinely needs HA/replication coordination
  that would be unsafe to hand-roll. Authentik (stateless server + worker in front of
  Postgres+Redis) is squarely in the first category — pulling in the official upstream chart
  would be inconsistent with why Authentik was chosen over Keycloak in the first place (Keycloak's
  idiomatic k8s deployment is operator-class complexity; Authentik's isn't).

## Design

### Components

**`deploy/helm/iverson/charts/authentik/`** (new subchart):
- `Deployment` × 2 — `server` and `worker`, sharing the same published Authentik container image
  (`ghcr.io/goauthentik/server`), per Authentik's documented minimum production topology (a single
  merged process is not an officially supported configuration).
- `Service` exposing the server's HTTP port.
- `ConfigMap` for non-secret env config: log level, Postgres host/database name, Redis host.
- `Secret`, generated once and preserved across `helm upgrade` via the `lookup`-guarded
  `randAlphaNum 32 | b64enc` pattern already used in `charts/starrocks/templates/secret.yaml`
  (checked with `{{- $existing := lookup "v1" "Secret" .Release.Namespace ... }}` before
  generating, so re-running `helm upgrade` never rotates it and silently invalidate every active
  session): holds `AUTHENTIK_SECRET_KEY` (session/token signing — must be stable), the Postgres
  app-user password, and the bootstrap admin credentials
  (`AUTHENTIK_BOOTSTRAP_EMAIL`/`AUTHENTIK_BOOTSTRAP_PASSWORD`/`AUTHENTIK_BOOTSTRAP_TOKEN` —
  Authentik's native first-boot admin-provisioning env vars, so a working superuser + API token
  exist immediately after deploy with zero manual steps).
- `ConfigMap` holding Blueprint YAML files, mounted into the server container's blueprints
  directory, defining:
  - The MFA-enforcement authentication flow: an MFA stage requiring at least one enrolled second
    factor before login completes, with both TOTP and WebAuthn offered as enrollment options — a
    user satisfies the requirement with either one, not both.
  - A default OAuth2/OIDC provider with PKCE enabled (S256 challenge method).
  - A default SAML provider.
  - Both providers are currently unused by any relying application — they exist so Part 2/3 have
    something concrete to point at, not because anything consumes them yet.

**`deploy/helm/iverson/charts/redis/`** (new subchart): minimal `Deployment` + `Service`, no
`PersistentVolumeClaim` — Authentik uses Redis purely as a cache/session/websocket-notification
broker with no durable state of its own (confirmed in Authentik's own architecture docs); losing
Redis contents on restart degrades gracefully rather than losing data.

**Postgres wiring**: a new `authentik` database added to the existing single CloudNativePG
`Cluster` resource (`charts/postgres/templates/cluster.yaml`), alongside the existing `iverson`
database — CNPG supports multiple databases per cluster natively, so this is a manifest/values
addition, not a new cluster or a new operator.

### Deployment targets

- **`docker-compose.yml`**: `authentik-server`, `authentik-worker`, and `redis` services following
  the existing pattern (named volumes where applicable, healthchecks, `restart: unless-stopped`).
  Server port published (e.g. `9000:9000`) so the admin UI is reachable at
  `http://localhost:9000`, alongside Jaeger (`16686`) and Prometheus (`9090`).
- **`deploy/kind/`**: no kind-specific work needed beyond what already exists — the new subcharts
  are picked up automatically by the documented `setup.sh`/`build-and-load-image.sh`/`helm
  upgrade --install` pipeline, same as the Prometheus subchart before it.
- **`values.yaml`** (prod defaults) + **`values-local.yaml`** (kind-sized overrides): new
  `authentik:` and `redis:` blocks, mirroring the existing `jaeger:`/`prometheus:` block shape
  (image tag, resources, `nodeSelector`).
- **`values-aws.yaml`/`values-azure.yaml`/`values-gcp.yaml`**: **no changes needed.** Neither
  Authentik nor Redis has a `PersistentVolumeClaim` in this design (state lives in the shared CNPG
  Postgres cluster, which already has its cloud `StorageClassName` overrides), so there is no new
  storage-class surface to wire per cloud.
- **`templates/networkpolicies.yaml`**: Authentik needs egress to Postgres (existing cluster) and
  Redis (new); Redis needs ingress scoped to Authentik's pods only. Both follow the existing
  per-component policy pattern in this file (see the `worker-ingress`/`prometheus-egress` policies
  added in the prior plan for the shape to match).

### Testing & verification

Pure infrastructure — no C# code, no automated `dotnet test` involvement, matching how the
Prometheus subchart was verified in the prior plan:

- `helm dependency build` + `helm template` + `helm lint` clean across all values-overlay
  combinations.
- `docker compose config --quiet` clean.
- **Live docker-compose smoke test**: bring the stack up, confirm Authentik's login flow reaches
  the MFA-enforcement stage, log in with the bootstrap admin credentials, complete TOTP
  enrollment, then confirm — via Authentik's own API using the bootstrap token — that the
  blueprint-defined OIDC provider (PKCE/S256 enabled) and SAML provider both exist. This proves
  the blueprints actually applied, not just that the container started.
- **Live kind smoke test**: same checks, deployed via Helm, following this repo's established
  `setup.sh` → `build-and-load-image.sh` → `helm upgrade --install` pipeline, pod health +
  port-forwarded API check as the final gate — mirroring exactly how the Prometheus subchart was
  smoke-tested at the end of the prior plan.

## Explicitly out of scope (do not silently fill these in)

- Any change to `Iverson.Api`, the proto contracts, or any of the 5 client SDKs — that's Parts
  2-4.
- Any authorization model (row/field-level access control) — that's Part 5, and is a materially
  larger effort touching the schema registry and every query engine.
- Brokering SSO from an external corporate IdP — no such IdP exists today; Authentik is the
  standalone source of truth. If this changes later, SAML federation can be added without
  reworking this design (the SAML *provider* config here is Authentik acting as IdP for other
  apps, not Authentik acting as a SAML *Service Provider* federating outward — a different,
  additive configuration).
- SMS/email OTP MFA methods.
- Redis persistence/HA — single instance, cache-only, matches the resource-minimal footprint of
  every other hand-rolled subchart in this repo.

## Self-review

- **Placeholder scan:** no TBD/TODO; every component, file path, and configuration decision named
  explicitly.
- **Internal consistency:** the "hand-rolled, not upstream chart" decision is justified
  consistently with the earlier Keycloak-vs-Authentik reasoning (operator-class vs.
  app-class complexity) rather than asserted independently.
- **Scope:** deliberately narrow — infrastructure only, zero Iverson code changes, matching the
  5-part decomposition agreed during brainstorming. Parts 2-5 are named but explicitly deferred,
  not designed here.
- **Ambiguity check:** "SAML as a protocol capability with no current consumer" is stated
  explicitly to avoid the design being misread as "SAML SSO is wired up end-to-end" — it isn't,
  that's Part 2's job once an application actually points at the SAML provider.
