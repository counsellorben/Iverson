# Admin Console — Creating a User and Connecting

Everything needed to go from nothing to a working admin console: what makes a user
*appropriate*, how to create one, and how to connect.

Companion documents, neither of which you need to read first:
[`operator-access-onboarding.md`](operator-access-onboarding.md) covers granting operator
rights as an ongoing operational task; `docs/user-management-and-security.md` is the
fuller identity reference.

---

## 1. What makes a user "appropriate"

Two **independent** requirements. Most of the confusion about this console comes from
satisfying one and not the other, because each fails differently and neither failure
looks like a misconfiguration.

### Requirement A — membership in the `operators` group

`OperatorAuthorizationPolicy` gates three of the nine widgets — the tenant roster, the
metrics band, and Qdrant collections — on a token whose `groups` claim contains
`operators`.

**Without it:** those widgets render an explicit "not authorized" card. Not an error, not
an empty list. That is designed behaviour, so a non-operator seeing refusals is correct
output rather than a fault.

### Requirement B — a `tenant_id` user attribute

Less obvious, and the one people miss. The `tenant_id` scope mapping is:

```python
return {"tenant_id": request.user.attributes.get("tenant_id")}
```

It reads a **user attribute**. If the user has none, the claim is null — and
`RowFieldAuthorizationEvaluator` denies a principal carrying no `tenant_id`, on every
type.

**Without it:** the schema catalog comes back empty and every data-volume row comes back
denied — *even for a full operator*. The page looks broken while behaving exactly as
specified. Group membership does not rescue this; the two requirements are orthogonal.

> A `tenant_id` that does not match a provisioned tenant is not an error either. The
> store has no database to address, so counts render as a legitimate `0`.

### So an appropriate user has both

| | Unlocks | Failure mode if missing |
|---|---|---|
| `operators` group | tenant roster, metrics band, Qdrant | three "not authorized" cards |
| `tenant_id` attribute | schema catalog, data volume | empty catalog, all rows denied |

---

## 2. Compose (local development)

### 2a. The fast path — a user already exists

Nothing to create. The compose blueprint seeds `iverson-loadtest-bypass-user` into
`operators` **and** gives it `tenant_id: tenant_bypass`, so it satisfies both
requirements out of the box:

```
username: iverson-loadtest-bypass-user
password: <IVERSON_BYPASS_PASSWORD from Iverson.Server/.env>
```

The password is generated per checkout by `scripts/generate-compose-secrets.sh` and is **dev-only**, for a stack that binds to
localhost. They are never valid in a shared, CI, or production environment.

### 2b. Creating your own local user instead

1. Bring the stack up: `cd Iverson.Server && docker compose up -d`. Wait for
   `authentik-server` to become healthy — blueprints apply at startup and login will not
   work before then.
2. Open Authentik at <http://localhost:9000> and sign in as
   `admin@iverson.local` / the `AUTHENTIK_BOOTSTRAP_PASSWORD` value in `Iverson.Server/.env`.
3. **Directory → Users → Create.** Set a username, name, and password.
4. **Set the tenant attribute.** Edit the user, and in the **Attributes** field (YAML) add:
   ```yaml
   tenant_id: your-tenant-id
   ```
   This is Requirement B. Skipping it produces the empty-catalog symptom above.
5. **Directory → Groups → `operators` → add the user.** The group already exists — it is
   blueprinted; do not create a second one. This is Requirement A.

---

## 3. Kind / Helm

The `operators` group is created automatically by a blueprint. **Membership is
deliberately not blueprinted for any deployment** — who holds operator rights is an
operational decision, and seeding it into version-controlled config would be a
privilege-escalation defect.

1. Sign in to Authentik with the deployment's bootstrap credentials — email from
   `AUTHENTIK_BOOTSTRAP_EMAIL`, password from the `<release>-authentik-app` Secret's
   `bootstrap-password` key.
2. **Directory → Users → Create**, or locate the existing account.
3. Add the `tenant_id` attribute, as in step 2b.4 above.
4. **Directory → Groups → `operators` → add the user.**
5. Have them complete a browser login and confirm the resulting token's `groups` claim
   contains `operators` (see §6).

### Creating a tenant to point `tenant_id` at

Tenants are created through `TenantLifecycleGrpcService.CreateTenant`, which is itself
`Operator`-gated and served over gRPC on port 8080 (gRPC-Web enabled). So the ordering is:
one operator exists first, creates the tenant, and then user `tenant_id` attributes are
set to it. `ListTenants` on the same service is what the console's tenant-roster widget
reads.

---

## 4. Connecting

### Compose — the console runs from the dev server

The console is **not** a compose service. Compose runs the backing stack; the console runs
from Vite against it:

```bash
cd Iverson.AdminUI
npm ci
npm run dev
```

Open <http://localhost:5173/>.

`.env.development` already points at the compose stack:

| Variable | Value |
|---|---|
| `VITE_OIDC_CLIENT_ID` | `dev-iverson-human-oidc-client-id` |
| `VITE_OIDC_AUTHORITY` | `http://localhost:9000/application/o/iverson-api/` |
| `VITE_API_BASE_URL` | `http://localhost:8081` |

**That port matters.** 8080 is `Protocols: Http2` — h2c only — and a browser cannot speak
cleartext HTTP/2, so a console pointed at 8080 fails every request. The operational
endpoints answer only on 8081 by design.

Login redirects to Authentik and returns via `http://localhost:5173/callback`. That URI
and the root are registered on the `iverson-oidc-default` provider; changing the dev-server
port means registering the new URIs in the blueprint.

### Kind — resolve three hostnames first

```
<ingress-controller-ip>  iverson.local admin-api.iverson.local authentik.iverson.local
```

- `iverson.local` — the console
- `admin-api.iverson.local` — the API's dedicated host, which the console's browser-side
  fetches target directly over CORS
- `authentik.iverson.local` — Authentik's login pages, which the browser is
  full-page-redirected to and back from

**Missing the `authentik.iverson.local` entry fails silently** — the redirect cannot
resolve, and nothing in this repo's logs reports it.

Then open <http://iverson.local/>.

---

## 5. What you should see

The landing page is the index route, `/`, with nine widgets in three bands:

| Band | Widgets | Refresh |
|---|---|---|
| A — stores | health strip, tenant roster, schema catalog, data volume | health polls every 60s; the other three fetch on load and on **Refresh** only |
| B — metrics | fan-out backlog, DLQ and retry rate, transport health, embedding latency | one shared poll every 30s |
| C — vectors | Qdrant collections | 30s |

Data volume is **tenant-scoped**, not a deployment total. A zero in that list is a real
zero; types you may not see are reported separately as a count, never as a zero row.

---

## 6. Verifying you got it right

The quickest check is the token itself. After logging in, open browser devtools →
Application → Session Storage, find the `oidc.user:` entry, and decode the `id_token`
payload (any JWT decoder, or `atob` on the middle segment). You want:

- `"groups": ["operators", ...]` — Requirement A
- `"tenant_id": "..."` — Requirement B, and **not** null

If `groups` is absent entirely rather than empty, the console is not requesting the scope
or the provider is not emitting claims into the ID token — both are configured in this
repo, so an absence points at a modified blueprint.

---

## 7. If the page does not look right

| Symptom | Cause |
|---|---|
| Three cards say "not authorized" | Requirement A — the user is not in `operators`. |
| Schema catalog empty **and** every data-volume row denied | Requirement B — no `tenant_id` attribute. This happens to full operators too. |
| Every card sits on a spinner | The access token never arrived. Cards distinguish "waiting for session" from "loading", so a permanent spinner usually means the OIDC flow did not complete. |
| Metrics band says Prometheus is not deployed | `global.prometheusEnabled` is false for that profile — `values-laptop.yaml` disables it deliberately. Reported distinctly from "deployed but unreachable". |
| Backlog and DLQ read "No data" | Those five series are **worker-only**. If the worker is not scraped they are absent, not zero — and absent is what is shown. |
| Transport health's error percentage is blank | Normal on a healthy system. The query divides two rates; with zero 5xx in the window the numerator is empty. Blank means "nothing has failed". |
| StarRocks shows a neutral "Auth pending" chip | First install. Readiness deliberately treats that state as ready so the post-install hook does not deadlock — not a failure, and not coloured as one. |
| Data volume shows zeros for everything | The `tenant_id` does not match a provisioned tenant. The store has no database to address; zero is honest. |
| Login redirects then fails on a cloud profile | The console and Authentik must be on the **same scheme**. An `https://` console calling an `http://` authority is blocked as mixed content before any request is sent. |

---

## Related

- [`operator-access-onboarding.md`](operator-access-onboarding.md) — operator rights as an
  ongoing operational task
- [`grpc-admin-auth-cutover.md`](grpc-admin-auth-cutover.md) — the deploy-time precondition
  that operators are group members before a cutover ships
- `docs/user-management-and-security.md` — the fuller identity reference
