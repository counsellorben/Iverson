# Admin Console Landing Page — Usage

How to get a user who can see the console, and how to open the landing page. Two
environments: **compose** (local development) and **kind/Helm**.

For the deployment-side half of operator access — granting a real human operator
rights in a real environment — see
[`operator-access-onboarding.md`](operator-access-onboarding.md). This document is
the end-to-end "how do I look at the page" companion to it.

---

## What the page shows

The landing page lives at `/` and renders nine widgets in three bands:

| Band | Widgets | Source | Refresh |
|---|---|---|---|
| A — stores | health strip, tenant roster, schema catalog, data volume | `/health`, `/admin/console/{tenants,schema,data-volume}` | health polls every 60s; the other three fetch on load and on **Refresh** only |
| B — metrics | fan-out backlog, DLQ and retry rate, transport health, embedding latency | `/admin/console/metrics` → Prometheus | one shared poll every 30s |
| C — vectors | Qdrant collections | `/admin/console/qdrant` | 30s |

Three of those — tenant roster, transport/metrics band, and Qdrant — are gated on the
`Operator` policy. **Without operator rights they render an explicit "not authorized"
card, not an error and not an empty list.** That is the designed behaviour, so seeing
six of nine cards refuse is the correct output for a non-operator, not a fault.

Data volume is **tenant-scoped**. A zero in that list is a real zero; types you may not
see are reported separately as a count, never as a zero row.

---

## Compose (local development)

The console is **not** a compose service. Compose runs the backing stack; the console
runs from the Vite dev server against it.

### 1. Bring up the stack

```bash
cd Iverson.Server
docker compose up -d
```

Wait for `authentik-server` to become healthy — the blueprints are applied at startup,
and the login flow will not work until they are. `authentik-migrate` runs first and
must complete.

### 2. The operator user already exists

Nothing to create. `blueprints/compose-only/service-clients.yaml` seeds
`iverson-loadtest-bypass-user` into the `operators` group at blueprint-apply time, so
that identity satisfies the `Operator` policy out of the box:

```
username: iverson-loadtest-bypass-user
password: dev-only-not-for-production-bypass-password-0123456789
```

These credentials are **dev-only and hardcoded on purpose**, for a stack that binds to
localhost. They are never valid for a shared, CI, or production environment.

To use a *different* local user instead, create it in the Authentik admin UI at
<http://localhost:9000> (bootstrap login `admin@iverson.local` / `dev-admin-password`)
and add it to the `operators` group — the group itself already exists, blueprinted.

### 3. Start the console

```bash
cd Iverson.AdminUI
npm ci
npm run dev
```

Then open <http://localhost:5173/>.

`.env.development` already points at the compose stack:

| Variable | Value | What it is |
|---|---|---|
| `VITE_OIDC_CLIENT_ID` | `dev-iverson-human-oidc-client-id` | the console's Authentik client |
| `VITE_OIDC_AUTHORITY` | `http://localhost:9000/application/o/iverson-api/` | Authentik's OIDC issuer |
| `VITE_API_BASE_URL` | `http://localhost:8081` | the API's **HTTP/1.1** listener |

That port matters. 8080 is `Protocols: Http2` — h2c only — and a browser cannot speak
cleartext HTTP/2, so pointing the console at 8080 fails every request. The operational
endpoints answer only on 8081 by design.

### 4. Log in

The console redirects to Authentik, you authenticate, and it returns to
`http://localhost:5173/callback` before landing on `/`. Both that callback URI and the
root are registered on the `iverson-oidc-default` provider; changing the dev-server port
means registering the new URIs in the blueprint.

---

## Kind / Helm

### 1. Resolve three hostnames

The browser must resolve **three** names to the ingress controller's IP:

```
<ingress-controller-ip>  iverson.local admin-api.iverson.local authentik.iverson.local
```

- `iverson.local` — the console itself
- `admin-api.iverson.local` — the API's dedicated host, which the console's browser-side
  fetches target directly over CORS
- `authentik.iverson.local` — Authentik's login pages, which the browser is
  full-page-redirected to and back from

**Missing the `authentik.iverson.local` entry fails silently.** The redirect simply
cannot resolve, and nothing in this repo's logs reports it.

### 2. Create the user and grant operator rights

The `operators` group is created automatically by a blueprint. **Membership is
deliberately not blueprinted for any real deployment** — who holds operator rights is an
operational decision, and seeding it into version-controlled config would be a
privilege-escalation defect.

Follow [`operator-access-onboarding.md`](operator-access-onboarding.md): create the user
in Authentik, add them to `operators`, then have them complete a browser login and
confirm the token's `groups` claim contains `operators`.

### 3. Open the console

<http://iverson.local/> — the landing page is the index route.

---

## If the page does not look right

| Symptom | Cause |
|---|---|
| Six of nine cards say "not authorized" | The signed-in user is not in `operators`. Expected for a non-operator; see the onboarding runbook. |
| Every card sits on a spinner | The access token has not arrived. Cards distinguish "waiting for session" from "loading" — a permanent spinner usually means the OIDC flow did not complete. |
| Metrics band says Prometheus is not deployed | `global.prometheusEnabled` is false for that profile — `values-laptop.yaml` disables it deliberately. This is a supported configuration, reported distinctly from "deployed but unreachable". |
| Backlog and DLQ figures read "No data" | Those five series are **worker-only**. If the worker is not being scraped they are absent, not zero — and absent is what the widget shows. |
| Transport health's error percentage is blank | Normal on a healthy system: the query divides two rates, and with zero 5xx in the window the numerator is empty. Blank means "nothing has failed", not "no data collected". |
| StarRocks shows a neutral "Auth pending" chip | First install. Readiness deliberately treats that state as ready so the post-install hook does not deadlock; it is not a failure and is not coloured as one. |
| Login redirects then fails on a cloud profile | Check the console and Authentik are on the **same scheme**. A `https://` console calling an `http://` authority is blocked by the browser as mixed content before any request is sent. |

---

## Related

- [`operator-access-onboarding.md`](operator-access-onboarding.md) — granting operator
  rights in a real deployment
- [`grpc-admin-auth-cutover.md`](grpc-admin-auth-cutover.md) — the deploy-time
  precondition that operators are group members before a cutover ships
- `docs/user-management-and-security.md` — the fuller identity reference
